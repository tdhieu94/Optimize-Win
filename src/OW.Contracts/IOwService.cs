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
}

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
