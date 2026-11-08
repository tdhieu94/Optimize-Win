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

    /// <summary>
    /// Asks GitHub whether OW's own repository has a newer release. Sends nothing about this computer
    /// beyond the HTTP request itself.
    /// </summary>
    Task<UpdateInfo> CheckForUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the release found by <see cref="CheckForUpdateAsync"/> (must match <paramref name="version"/>),
    /// verifies its SHA-256 and starts the installer. The app and the service restart during the update.
    /// </summary>
    Task<UpdateStartResult> InstallUpdateAsync(string version, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only: starts looking for duplicate personal files (photos, videos, music, documents, archives)
    /// on the given local drives, in the background. Takes only drive letters, never paths.
    /// Returns false if another duplicate scan is still running.
    /// </summary>
    Task<bool> StartDuplicateScanAsync(IReadOnlyList<string> driveLetters, CancellationToken cancellationToken = default);

    Task<DuplicateScanStatus> GetDuplicateScanStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Result of the last finished duplicate scan, or null.</summary>
    Task<DuplicateReport?> GetDuplicateReportAsync(CancellationToken cancellationToken = default);

    Task CancelDuplicateScanAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the chosen copies into quarantine (undoable). Only paths from report <paramref name="scanId"/> are accepted,
    /// every group must keep at least one copy, and every file is hashed again first.
    /// </summary>
    Task<CleanSummary> RemoveDuplicatesAsync(Guid scanId, IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Turns one startup program on or off with Windows' own switch (like Task Manager). Nothing is deleted;
    /// the change is journaled and can be undone. Windows' own entries cannot be turned off.
    /// </summary>
    Task<StartupChangeResult> SetStartupEnabledAsync(StartupChangeRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Read-only: in the background, asks Windows (DISM) how big its component store (WinSxS) is and what cleanup could do,
    /// and measures Windows.old and restore points. Returns false if analysis or cleanup is already running.
    /// </summary>
    Task<bool> StartWindowsAnalysisAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts Windows' own component cleanup (DISM /StartComponentCleanup, never /ResetBase) in the background, through the
    /// safe executor's journal. Cannot be undone; the UI asks for confirmation first. Returns false if something is already running.
    /// </summary>
    Task<bool> StartComponentCleanupAsync(CancellationToken cancellationToken = default);

    Task<WindowsCleanupStatus> GetWindowsCleanupStatusAsync(CancellationToken cancellationToken = default);
}

/// <param name="ComponentStoreBytes">Real size of WinSxS (much is shared with Windows itself).</param>
/// <param name="CleanableBytes">Backups and cache inside it: the most cleanup could remove; usually it removes less.</param>
/// <param name="WindowsOldBytes">Size of C:\Windows.old, or null if there is none.</param>
public sealed record WindowsCleanupReport(
    DateTimeOffset MeasuredUtc,
    long? ComponentStoreBytes,
    long? CleanableBytes,
    int? ReclaimablePackages,
    bool? CleanupRecommended,
    string? LastCleanup,
    string? ComponentStoreError,
    long? WindowsOldBytes,
    int RestorePointCount,
    long? RestorePointBytes,
    long SystemDriveFreeBytes);

/// <param name="Percent">DISM's own progress, when it reports one.</param>
/// <param name="LastResult">Vietnamese outcome of the last cleanup (or analysis error).</param>
public sealed record WindowsCleanupStatus(bool Running, string Stage, double? Percent, double Seconds, WindowsCleanupReport? Report, string? LastResult, bool LastResultOk);

/// <param name="MachineWide">True for entries of every user (HKLM Run or the all-users Startup folder).</param>
/// <param name="Group">"Run", "Run32" or "StartupFolder".</param>
/// <param name="ValueName">The Run value name, or the shortcut's file name.</param>
public sealed record StartupChangeRequest(bool MachineWide, string Group, string ValueName, bool Enable);

public sealed record StartupChangeResult(Guid BatchId, bool Changed, string Message);

/// <param name="Stage">What the scan is doing now, in Vietnamese.</param>
public sealed record DuplicateScanStatus(bool Running, long Files, long HashedBytes, string Stage, string Current, double Seconds, bool HasReport, string? Error);

public sealed record DuplicateFileInfo(string Path, DateTime LastWriteUtc);

public sealed record DuplicateGroupInfo(long SizeBytes, IReadOnlyList<DuplicateFileInfo> Files);

public sealed record DuplicateReport(
    Guid ScanId,
    IReadOnlyList<string> DriveLetters,
    IReadOnlyList<DuplicateGroupInfo> Groups,
    long WastedBytes,
    long FilesCompared,
    long SkippedProgramFolders,
    double Seconds);

public sealed record UpdateInfo(string CurrentVersion, string? LatestVersion, bool Available, string? Notes, string? Error);

public sealed record UpdateStartResult(bool Started, string Message);

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
    long Bytes,
    bool CanUndo = true);

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
