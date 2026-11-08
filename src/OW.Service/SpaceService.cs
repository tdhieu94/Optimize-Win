using System.Diagnostics;
using OW.Contracts;
using OW.Core.Space;

namespace OW.Service;

/// <summary>
/// Runs read-only space scans in the background, one at a time, so the UI can show progress and cancel.
/// </summary>
public sealed class SpaceService(ILogger<SpaceService> logger)
{
    private readonly Lock _gate = new();
    private Job? _current;
    private SpaceReport? _lastReport;

    public bool Start(string driveLetter)
    {
        var drive = ValidateDrive(driveLetter);
        lock (_gate)
        {
            if (_current is { Task.IsCompleted: false })
            {
                return false;
            }

            var job = new Job(drive.Name[..1]);
            _current = job;
            _lastReport = null;
            job.Task = Task.Factory.StartNew(() => Run(job, drive), job.Cancel.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return true;
        }
    }

    public SpaceScanStatus GetStatus()
    {
        lock (_gate)
        {
            var job = _current;
            if (job is null)
            {
                return new SpaceScanStatus(null, false, 0, 0, 0, string.Empty, 0, false, null);
            }

            return new SpaceScanStatus(
                job.Drive,
                !job.Task.IsCompleted,
                job.Progress.Files,
                job.Progress.Bytes,
                job.Progress.Folders,
                job.Progress.CurrentFolder,
                job.Clock.Elapsed.TotalSeconds,
                _lastReport is not null,
                job.Error);
        }
    }

    public SpaceReport? GetReport()
    {
        lock (_gate)
        {
            return _lastReport;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _current?.Cancel.Cancel();
        }
    }

    private void Run(Job job, DriveInfo drive)
    {
        logger.LogInformation("Space scan of {Drive} started", drive.Name);
        try
        {
            var scan = SpaceAnalyzer.Scan(drive.RootDirectory.FullName, null, job.Cancel.Token, job.Progress);
            var report = new SpaceReport(
                job.Drive,
                drive.TotalSize,
                drive.TotalFreeSpace,
                ToItem(scan.Root),
                scan.LargestFiles.Select(ToItem).ToList(),
                scan.FolderCount,
                scan.InaccessibleFolders,
                scan.Duration.TotalSeconds);
            lock (_gate)
            {
                _lastReport = report;
            }

            logger.LogInformation("Space scan of {Drive} finished in {Seconds:0.0}s, {Folders} folders", drive.Name, scan.Duration.TotalSeconds, scan.FolderCount);
        }
        catch (OperationCanceledException)
        {
            job.Error = "Đã dừng phân tích.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Space scan of {Drive} failed", drive.Name);
            job.Error = $"Phân tích bị lỗi: {ex.Message}";
        }
        finally
        {
            job.Clock.Stop();
        }
    }

    /// <summary>Only a single letter naming a ready, local, fixed drive is accepted.</summary>
    internal static DriveInfo ValidateDrive(string? driveLetter)
    {
        if (driveLetter is not { Length: 1 } || !char.IsAsciiLetter(driveLetter[0]))
        {
            throw new ArgumentException("Chỉ nhận một chữ cái tên ổ, ví dụ C.", nameof(driveLetter));
        }

        var drive = new DriveInfo(char.ToUpperInvariant(driveLetter[0]) + @":\");
        if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
        {
            throw new ArgumentException($"Ổ {drive.Name} không phải ổ cứng trong máy hoặc chưa sẵn sàng.", nameof(driveLetter));
        }

        return drive;
    }

    private static SpaceItem ToItem(SpaceNode n) =>
        new(n.Name, n.Path, n.IsFolder, n.SizeBytes, n.FileCount, n.Children.Select(ToItem).ToList());

    private sealed class Job(string drive)
    {
        public string Drive { get; } = drive;

        public SpaceScanProgress Progress { get; } = new();

        public CancellationTokenSource Cancel { get; } = new();

        public Stopwatch Clock { get; } = Stopwatch.StartNew();

        public Task Task { get; set; } = Task.CompletedTask;

        public volatile string? Error;
    }
}
