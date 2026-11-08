using System.Reflection;
using OW.Contracts;
using OW.Core.Execution;
using OW.Core.Journal;

namespace OW.Service;

/// <summary>Shared service logic behind <see cref="IOwService"/> (see <see cref="OwClientSession"/>). Every write goes through <see cref="SafeExecutor"/>.</summary>
public sealed class OwServiceImpl(SqliteJournal journal, SafeExecutor executor, SpaceService space, ILogger<OwServiceImpl> logger)
{
    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;

    public Task<ServiceInfo> GetInfoAsync(CancellationToken cancellationToken = default)
    {
        var version = typeof(OwServiceImpl).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        return Task.FromResult(new ServiceInfo(version, Started, (int)ServiceConstants.QuarantineRetention.TotalDays));
    }

    public Task<IReadOnlyList<BatchSummary>> GetHistoryAsync(int maxCount, CancellationToken cancellationToken = default)
    {
        var list = journal.GetRecentBatches(Math.Clamp(maxCount, 1, 200))
            .Select(b =>
            {
                var ops = journal.GetOperations(b.Id);
                return new BatchSummary(
                    b.Id,
                    b.Title,
                    b.CreatedUtc,
                    b.State.ToString(),
                    ops.Count(o => o.State is OperationState.Done or OperationState.Purged),
                    ops.Count(o => o.State == OperationState.Skipped),
                    ops.Count(o => o.State is OperationState.Failed or OperationState.UndoFailed),
                    ops.Count(o => o.State == OperationState.Undone),
                    ops.Where(o => o.State is OperationState.Done or OperationState.Purged).Sum(o => o.SizeBytes),
                    ops.Any(o => o.Kind != OperationKind.RunComponentCleanup));
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<BatchSummary>>(list);
    }

    public Task<IReadOnlyList<DiskInfo>> GetDisksAsync(CancellationToken cancellationToken = default) =>
        Task.Run(DiskInspector.GetDisks, cancellationToken);

    public Task<bool> StartSpaceScanAsync(string driveLetter, CancellationToken cancellationToken = default) =>
        Task.FromResult(space.Start(driveLetter));

    public Task<SpaceScanStatus> GetSpaceScanStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(space.GetStatus());

    public Task<SpaceReport?> GetSpaceReportAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(space.GetReport());

    public Task CancelSpaceScanAsync(CancellationToken cancellationToken = default)
    {
        space.Cancel();
        return Task.CompletedTask;
    }

    public async Task<UndoSummary> UndoBatchAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Undo requested for batch {BatchId}", batchId);
        var result = await Task.Run(() => executor.UndoBatch(batchId), cancellationToken).ConfigureAwait(false);
        return new UndoSummary(result.BatchId, result.Restored, result.Failed, result.Messages);
    }
}
