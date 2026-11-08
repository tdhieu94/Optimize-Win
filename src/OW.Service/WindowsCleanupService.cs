using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.Text;
using OW.Contracts;
using OW.Core.Execution;
using OW.Core.Safety;
using OW.Core.WindowsCleanup;

namespace OW.Service;

/// <summary>
/// Runs dism.exe with the fixed arguments from <see cref="ComponentStore"/> only, and reports its progress.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DismRunner
{
    private readonly Lock _gate = new();
    private double? _percent;

    public double? Percent
    {
        get
        {
            lock (_gate)
            {
                return _percent;
            }
        }
    }

    public static string DismPath => Path.Combine(Environment.SystemDirectory, "dism.exe");

    public (int ExitCode, string Output) Run(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _percent = null;
        }

        var psi = new ProcessStartInfo(DismPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Không mở được công cụ DISM của Windows.");
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var output = new StringBuilder();
        var buffer = new char[512];
        int read;

        // Read in chunks: DISM redraws its progress bar with '\r', so line-based reading would only see it at the end.
        while ((read = process.StandardOutput.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Append(buffer, 0, read);
            if (ComponentStore.LastPercent(new string(buffer, 0, read)) is { } p)
            {
                lock (_gate)
                {
                    _percent = p;
                }
            }
        }

        // DISM is never killed half-way: stopping Windows servicing in the middle is worse than waiting.
        process.WaitForExit();
        return (process.ExitCode, output.ToString() + errors.Result);
    }
}

/// <summary>The real cleaner: DISM /StartComponentCleanup, measuring free space on the system drive before and after.</summary>
[SupportedOSPlatform("windows")]
public sealed class DismComponentCleaner(DismRunner dism, ILogger<DismComponentCleaner> logger) : IComponentStoreCleaner
{
    public ComponentCleanupOutcome Cleanup(CancellationToken cancellationToken)
    {
        var system = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
        var before = system.AvailableFreeSpace;
        var (code, output) = dism.Run(ComponentStore.CleanupArguments, cancellationToken);
        system = new DriveInfo(system.Name);
        var freed = Math.Max(0, system.AvailableFreeSpace - before);
        logger.LogInformation("DISM StartComponentCleanup exit {Code}, free space +{Freed} bytes", code, freed);
        var (ok, restart, message) = ComponentStore.Explain(code, output);
        return new ComponentCleanupOutcome(ok, restart, freed, message);
    }
}

/// <summary>
/// "Dọn sâu Windows": a read-only analysis (component store, Windows.old, restore points) and Windows' own component cleanup,
/// both in the background because DISM can take many minutes. The cleanup goes through <see cref="SafeExecutor"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsCleanupService(SafeExecutor executor, OW.Core.Journal.SqliteJournal journal, DismRunner dism, ILogger<WindowsCleanupService> logger)
{
    private readonly Lock _gate = new();
    private Task? _task;
    private string _stage = string.Empty;
    private Stopwatch _clock = new();
    private WindowsCleanupReport? _report;
    private string? _lastResult;
    private bool _lastResultOk;

    public bool StartAnalysis()
    {
        lock (_gate)
        {
            if (_task is { IsCompleted: false })
            {
                return false;
            }

            _stage = "Đang hỏi Windows kho thành phần chiếm bao nhiêu chỗ (thường 1–5 phút)...";
            _clock = Stopwatch.StartNew();
            _task = Task.Factory.StartNew(RunAnalysis, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return true;
        }
    }

    public bool StartCleanup()
    {
        lock (_gate)
        {
            if (_task is { IsCompleted: false })
            {
                return false;
            }

            _stage = "Windows đang dọn kho thành phần. Việc này có thể mất 10–40 phút; cứ dùng máy bình thường, đừng tắt máy.";
            _clock = Stopwatch.StartNew();
            _lastResult = null;
            _task = Task.Factory.StartNew(RunCleanup, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return true;
        }
    }

    public WindowsCleanupStatus GetStatus()
    {
        lock (_gate)
        {
            var running = _task is { IsCompleted: false };
            return new WindowsCleanupStatus(running, running ? _stage : string.Empty, running ? dism.Percent : null, _clock.Elapsed.TotalSeconds, _report, _lastResult, _lastResultOk);
        }
    }

    /// <summary>Read-only. Also used by the CI self-test.</summary>
    public WindowsCleanupReport Analyze()
    {
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var root = Path.GetPathRoot(windowsDir)!;

        ComponentStoreAnalysis? store = null;
        string? storeError = null;
        try
        {
            var (code, output) = dism.Run(ComponentStore.AnalyzeArguments, CancellationToken.None);
            store = ComponentStore.Parse(output);
            if (code != 0 || store.ActualSizeBytes is null)
            {
                storeError = ComponentStore.Explain(code, output) is { Success: false } e ? e.Message : "Windows không cho biết kích thước kho thành phần.";
                logger.LogWarning("DISM AnalyzeComponentStore exit {Code}: {Output}", code, output.Length > 2000 ? output[^2000..] : output);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            storeError = "Không chạy được công cụ DISM của Windows: " + ex.Message;
        }

        var oldDir = Path.Combine(root, "Windows.old");
        long? windowsOld = Directory.Exists(oldDir) ? FolderSize(oldDir) : null;
        var (points, used) = RestorePoints(root);

        return new WindowsCleanupReport(
            DateTimeOffset.UtcNow,
            store?.ActualSizeBytes,
            store?.CleanableBytes,
            store?.ReclaimablePackages,
            store?.CleanupRecommended,
            store?.LastCleanup,
            storeError,
            windowsOld,
            points,
            used,
            new DriveInfo(root).AvailableFreeSpace);
    }

    private void RunAnalysis()
    {
        try
        {
            var report = Analyze();
            lock (_gate)
            {
                _report = report;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Windows cleanup analysis failed");
            lock (_gate)
            {
                _lastResult = "Phân tích bị lỗi: " + ex.Message;
                _lastResultOk = false;
            }
        }
    }

    private void RunCleanup()
    {
        string message;
        bool ok;
        try
        {
            var result = executor.Execute(new ActionPlan("Dọn kho thành phần Windows (WinSxS)", ActionOrigin.UserSelected, [new RunComponentCleanupAction()]));
            ok = result.Done == 1;
            var gained = result.BytesFreed >= 1024L * 1024
                ? string.Create(System.Globalization.CultureInfo.GetCultureInfo("vi-VN"), $" Ổ hệ thống trống thêm khoảng {result.BytesFreed / 1073741824.0:0.##} GB.")
                : " Windows không tìm thấy nhiều thứ để dọn lần này.";
            var op = journal.GetOperations(result.BatchId).FirstOrDefault()?.Message;
            message = ok ? op + gained : op ?? "Không dọn được.";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Component cleanup failed");
            ok = false;
            message = "Dọn bị lỗi: " + ex.Message;
        }

        WindowsCleanupReport? fresh = null;
        if (ok)
        {
            lock (_gate)
            {
                _stage = "Đang đo lại dung lượng...";
            }

            try
            {
                fresh = Analyze();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Re-analysis after cleanup failed");
            }
        }

        lock (_gate)
        {
            _lastResult = message;
            _lastResultOk = ok;
            _report = fresh ?? _report;
        }
    }

    private static long FolderSize(string folder)
    {
        var options = new System.IO.EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        long total = 0;
        try
        {
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", options))
            {
                total += f.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return total;
    }

    /// <summary>Number of restore points (shadow copies) on the system drive and the space they use, from WMI. Read-only.</summary>
    private (int Count, long? UsedBytes) RestorePoints(string root)
    {
        try
        {
            string? volumeId = null;
            using (var vols = new ManagementObjectSearcher("SELECT DeviceID, Name FROM Win32_Volume"))
            {
                foreach (ManagementObject v in vols.Get())
                {
                    if (string.Equals(v["Name"] as string, root, StringComparison.OrdinalIgnoreCase))
                    {
                        volumeId = v["DeviceID"] as string;
                    }
                }
            }

            if (volumeId is null)
            {
                return (0, null);
            }

            var count = 0;
            using (var copies = new ManagementObjectSearcher("SELECT VolumeName FROM Win32_ShadowCopy"))
            {
                foreach (ManagementObject c in copies.Get())
                {
                    if (string.Equals(c["VolumeName"] as string, volumeId, StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                    }
                }
            }

            long? used = null;
            using (var storage = new ManagementObjectSearcher("SELECT UsedSpace, Volume FROM Win32_ShadowStorage"))
            {
                foreach (ManagementObject s in storage.Get())
                {
                    if ((s["Volume"] as string)?.Contains(volumeId.Replace(@"\", @"\\"), StringComparison.OrdinalIgnoreCase) == true
                        || (s["Volume"] as string)?.Contains(volumeId, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        used = (used ?? 0) + Convert.ToInt64(s["UsedSpace"], System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
            }

            return (count, used ?? (count == 0 ? 0 : null));
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            logger.LogWarning(ex, "Could not read restore points");
            return (0, null);
        }
    }
}
