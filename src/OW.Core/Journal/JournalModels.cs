namespace OW.Core.Journal;

/// <summary>Kinds of change the safety layer knows how to make and undo.</summary>
public enum OperationKind
{
    /// <summary>A file was moved into quarantine (kept for a while so it can be restored).</summary>
    QuarantineFile,

    /// <summary>A program's "start with Windows" switch was turned on or off. The previous value is kept so it can be put back.</summary>
    SetStartupApproval,

    /// <summary>Windows' own component cleanup (DISM /StartComponentCleanup) ran. It removes superseded Windows files and cannot be undone.</summary>
    RunComponentCleanup,
}

public enum OperationState
{
    /// <summary>Written before the change starts; if the service crashes we know what was in flight.</summary>
    Pending,
    Done,
    Failed,

    /// <summary>The action was refused (protected path, file changed since scan, ...). Nothing was touched.</summary>
    Skipped,
    Undone,
    UndoFailed,

    /// <summary>The quarantined copy was deleted after the retention period; it can no longer be restored.</summary>
    Purged,
}

public enum BatchState
{
    Running,
    Completed,
    CompletedWithProblems,
    Undone,
    PartiallyUndone,
}

public sealed record BatchRecord(
    Guid Id,
    string Title,
    DateTimeOffset CreatedUtc,
    BatchState State);

public sealed record OperationRecord(
    Guid Id,
    Guid BatchId,
    int Sequence,
    OperationKind Kind,
    string TargetPath,
    string? BackupPath,
    long SizeBytes,
    OperationState State,
    string? Message,
    DateTimeOffset UpdatedUtc);
