using OW.Contracts;

namespace OW.Service;

/// <summary>
/// What one connected UI sees. It knows which Windows account is on the other end of the pipe
/// (read from the client's process, not claimed by the client), so per-user work uses that account.
/// </summary>
public sealed class OwClientSession(OwServiceImpl service, JunkService junk, UpdateService updates, string? userSid) : IOwService
{
    public Task<ServiceInfo> GetInfoAsync(CancellationToken cancellationToken = default) => service.GetInfoAsync(cancellationToken);

    public Task<IReadOnlyList<BatchSummary>> GetHistoryAsync(int maxCount, CancellationToken cancellationToken = default) =>
        service.GetHistoryAsync(maxCount, cancellationToken);

    public Task<UndoSummary> UndoBatchAsync(Guid batchId, CancellationToken cancellationToken = default) =>
        service.UndoBatchAsync(batchId, cancellationToken);

    public Task<IReadOnlyList<DiskInfo>> GetDisksAsync(CancellationToken cancellationToken = default) => service.GetDisksAsync(cancellationToken);

    public Task<bool> StartSpaceScanAsync(string driveLetter, CancellationToken cancellationToken = default) =>
        service.StartSpaceScanAsync(driveLetter, cancellationToken);

    public Task<SpaceScanStatus> GetSpaceScanStatusAsync(CancellationToken cancellationToken = default) =>
        service.GetSpaceScanStatusAsync(cancellationToken);

    public Task<SpaceReport?> GetSpaceReportAsync(CancellationToken cancellationToken = default) =>
        service.GetSpaceReportAsync(cancellationToken);

    public Task CancelSpaceScanAsync(CancellationToken cancellationToken = default) => service.CancelSpaceScanAsync(cancellationToken);

    public Task<JunkScanSummary> ScanJunkAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => junk.Scan(userSid), cancellationToken);

    public Task<CleanSummary> CleanJunkAsync(Guid scanId, IReadOnlyList<string> categoryIds, CancellationToken cancellationToken = default) =>
        Task.Run(() => junk.Clean(userSid, scanId, categoryIds ?? []), cancellationToken);

    public Task<UpdateInfo> CheckForUpdateAsync(CancellationToken cancellationToken = default) => updates.CheckAsync(cancellationToken);

    public Task<UpdateStartResult> InstallUpdateAsync(string version, CancellationToken cancellationToken = default) =>
        updates.InstallAsync(version, cancellationToken);
}
