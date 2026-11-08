using System.Diagnostics;
using OW.Contracts;
using OW.Core.Duplicates;
using OW.Core.Execution;
using OW.Core.Safety;

namespace OW.Service;

/// <summary>
/// Finds duplicate personal files in the background (read-only) and removes the copies the user picked,
/// through <see cref="SafeExecutor"/> only. The UI sends drive letters and paths from the report; nothing else.
/// </summary>
public sealed class DuplicateService(SafeExecutor executor, TimeProvider time, ILogger<DuplicateService> logger)
{
    private readonly Lock _gate = new();
    private Job? _current;
    private (DuplicateReport Report, IReadOnlyList<DuplicateGroup> Groups, DateTimeOffset At)? _last;

    public bool Start(IReadOnlyList<string> driveLetters)
    {
        var drives = driveLetters.Distinct(StringComparer.OrdinalIgnoreCase).Select(SpaceService.ValidateDrive).ToList();
        if (drives.Count == 0)
        {
            throw new ArgumentException("Hãy chọn ít nhất một ổ.", nameof(driveLetters));
        }

        lock (_gate)
        {
            if (_current is { Task.IsCompleted: false })
            {
                return false;
            }

            var job = new Job(drives.Select(d => d.Name[..1]).ToList());
            _current = job;
            _last = null;
            job.Task = Task.Factory.StartNew(() => Run(job, drives), job.Cancel.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            return true;
        }
    }

    public DuplicateScanStatus GetStatus()
    {
        lock (_gate)
        {
            var job = _current;
            return job is null
                ? new DuplicateScanStatus(false, 0, 0, string.Empty, string.Empty, 0, false, null)
                : new DuplicateScanStatus(!job.Task.IsCompleted, job.Progress.Files, job.Progress.HashedBytes, job.Progress.Stage, job.Progress.Current, job.Clock.Elapsed.TotalSeconds, _last is not null, job.Error);
        }
    }

    public DuplicateReport? GetReport()
    {
        lock (_gate)
        {
            return _last?.Report;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _current?.Cancel.Cancel();
        }
    }

    public CleanSummary Remove(Guid scanId, IReadOnlyList<string> paths)
    {
        IReadOnlyList<DuplicateGroup> groups;
        lock (_gate)
        {
            if (_last is not { } last || last.Report.ScanId != scanId)
            {
                return new CleanSummary(Guid.Empty, 0, 0, 0, 0, "Kết quả quét đã cũ. Hãy quét lại rồi chọn.");
            }

            if (time.GetUtcNow() - last.At > TimeSpan.FromHours(3))
            {
                return new CleanSummary(Guid.Empty, 0, 0, 0, 0, "Kết quả quét đã quá 3 giờ. Hãy quét lại rồi chọn.");
            }

            groups = last.Groups;
        }

        var plan = DuplicateRemoval.Plan(groups, paths, p => DuplicateFinder.Sha256Of(p));
        if (plan.Error is not null)
        {
            return new CleanSummary(Guid.Empty, 0, 0, 0, 0, plan.Error);
        }

        if (plan.Actions.Count == 0)
        {
            return new CleanSummary(Guid.Empty, 0, plan.Skipped, 0, 0, "Không bỏ file nào: các file đã thay đổi hoặc bản giữ lại không còn. Hãy quét lại.");
        }

        var result = executor.Execute(new ActionPlan($"Bỏ {plan.Actions.Count} file trùng", ActionOrigin.UserSelected, plan.Actions));
        logger.LogInformation("Duplicate removal batch {Batch}: {Done} done, {Skipped} skipped, {Failed} failed", result.BatchId, result.Done, result.Skipped, result.Failed);

        // Removed files must not be offered again from the same report.
        lock (_gate)
        {
            _last = null;
        }

        var skipped = result.Skipped + plan.Skipped;
        var message = $"Đã chuyển {result.Done} file trùng vào vùng cách ly.";
        if (result.Failed > 0)
        {
            message += $" {result.Failed} file không chuyển được (có thể đang mở); chúng vẫn ở chỗ cũ.";
        }

        if (skipped > 0)
        {
            message += $" Bỏ qua {skipped} file vì đã thay đổi sau khi quét.";
        }

        return new CleanSummary(result.BatchId, result.Done, skipped, result.Failed, result.BytesFreed, message);
    }

    private void Run(Job job, IReadOnlyList<DriveInfo> drives)
    {
        logger.LogInformation("Duplicate scan of {Drives} started", string.Join(",", job.Drives));
        try
        {
            var scan = DuplicateFinder.Find(drives.Select(d => d.RootDirectory.FullName), null, job.Cancel.Token, job.Progress);
            var report = new DuplicateReport(
                Guid.NewGuid(),
                job.Drives,
                scan.Groups.Select(g => new DuplicateGroupInfo(g.SizeBytes, g.Files.Select(f => new DuplicateFileInfo(f.Path, f.LastWriteUtc)).ToList())).ToList(),
                scan.Groups.Sum(g => g.WastedBytes),
                scan.FilesSeen,
                scan.SkippedProgramFolders,
                scan.Duration.TotalSeconds);
            lock (_gate)
            {
                _last = (report, scan.Groups, time.GetUtcNow());
            }

            logger.LogInformation("Duplicate scan finished: {Groups} groups, {Files} files compared, {Seconds:0.0}s", scan.Groups.Count, scan.FilesSeen, scan.Duration.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            job.Error = "Đã dừng tìm file trùng.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Duplicate scan failed");
            job.Error = $"Tìm file trùng bị lỗi: {ex.Message}";
        }
        finally
        {
            job.Clock.Stop();
        }
    }

    private sealed class Job(IReadOnlyList<string> drives)
    {
        public IReadOnlyList<string> Drives { get; } = drives;

        public DuplicateScanProgress Progress { get; } = new();

        public CancellationTokenSource Cancel { get; } = new();

        public Stopwatch Clock { get; } = Stopwatch.StartNew();

        public Task Task { get; set; } = Task.CompletedTask;

        public volatile string? Error;
    }
}
