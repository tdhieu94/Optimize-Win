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
    /// Read-only: measures which folders use the most space on one local drive.
    /// Takes only a drive letter ("C"), never a path. Can take a few minutes on a full drive.
    /// </summary>
    Task<SpaceReport> AnalyzeSpaceAsync(string driveLetter, CancellationToken cancellationToken = default);
}

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
