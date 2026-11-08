namespace OW.Contracts;

/// <summary>
/// Everything the UI may ask the admin service to do. The service exposes nothing else:
/// there is deliberately no "run this command" or "delete this path" method.
/// New abilities are added here one typed method at a time.
/// </summary>
public interface IOwService
{
    Task<ServiceInfo> GetInfoAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BatchSummary>> GetHistoryAsync(int maxCount, CancellationToken cancellationToken = default);

    Task<UndoSummary> UndoBatchAsync(Guid batchId, CancellationToken cancellationToken = default);

    /// <summary>Read-only: physical disks, their health readings and the drives (C:, D:, ...) on each.</summary>
    Task<IReadOnlyList<DiskInfo>> GetDisksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only: starts measuring which folders use the most space on one local drive, in the background.
    /// Takes only a drive letter ("C"), never a path. Returns false if another scan is still running.
    /// </summary>
    Task<bool> StartSpaceScanAsync(string driveLetter, CancellationToken cancellationToken = default);

    /// <summary>Progress of the current (or last) space scan.</summary>
    Task<SpaceScanStatus> GetSpaceScanStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Result of the last finished space scan, or null if there is none.</summary>
    Task<SpaceReport?> GetSpaceReportAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops the running space scan, if any.</summary>
    Task CancelSpaceScanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only: looks for junk files of the signed-in user (temporary files, crash dumps).
    /// The service decides which folders; the UI cannot name a path.
    /// </summary>
    Task<JunkScanSummary> ScanJunkAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the files found by <paramref name="scanId"/> in the chosen categories into quarantine
    /// through the safe executor. Undoable for the retention period.
    /// </summary>
    Task<CleanSummary> CleanJunkAsync(Guid scanId, IReadOnlyList<string> categoryIds, CancellationToken cancellationToken = default);
}

public sealed record JunkCategoryInfo(string Id, string Title, string Description, int FileCount, long Bytes);

public sealed record JunkScanSummary(Guid ScanId, IReadOnlyList<JunkCategoryInfo> Categories, string? Note);

public sealed record CleanSummary(Guid BatchId, int Done, int Skipped, int Failed, long Bytes, string Message);

/// <param name="Error">Vietnamese message if the last scan failed or was stopped.</param>
public sealed record SpaceScanStatus(
    string? DriveLetter,
    bool Running,
    long Files,
    long Bytes,
    long Folders,
    string CurrentFolder,
    double Seconds,
    bool HasReport,
    string? Error);

/// <summary>One folder or file in a space report. <see cref="Path"/> is null for an "other items" summary row.</summary>
public sealed record SpaceItem(string Name, string? Path, bool IsFolder, long SizeBytes, long FileCount, IReadOnlyList<SpaceItem> Children);

public sealed record SpaceReport(
    string DriveLetter,
    long DriveSizeBytes,
    long DriveFreeBytes,
    SpaceItem Root,
    IReadOnlyList<SpaceItem> LargestFiles,
    long FolderCount,
    long InaccessibleFolders,
    double Seconds);

public static class OwPipe
{
    /// <summary>Name of the local named pipe the service listens on.</summary>
    public const string Name = "OptimizeWin.Service.v1";
}

public sealed record ServiceInfo(string Version, DateTimeOffset StartedUtc, int QuarantineRetentionDays);

public sealed record BatchSummary(
    Guid Id,
    string Title,
    DateTimeOffset CreatedUtc,
    string State,
    int Done,
    int Skipped,
    int Failed,
    int Undone,
    long Bytes);

public sealed record UndoSummary(Guid BatchId, int Restored, int Failed, IReadOnlyList<string> Messages);

public sealed record DiskInfo(
    int Number,
    string Model,
    string MediaType,
    string BusType,
    long SizeBytes,
    string? WindowsHealthStatus,
    int? TemperatureC,
    int? WearPercentUsed,
    long? PowerOnHours,
    long? ReadErrorsUncorrected,
    long? WriteErrorsUncorrected,
    string Verdict,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<VolumeInfo> Volumes);

public sealed record VolumeInfo(string DriveLetter, string Label, string FileSystem, long SizeBytes, long FreeBytes);
