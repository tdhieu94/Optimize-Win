using OW.Core.Safety;

namespace OW.Core.Execution;

/// <summary>
/// What a scan proposes to do. Modules only build plans; only <see cref="SafeExecutor"/> carries them out.
/// </summary>
public sealed record ActionPlan(string Title, ActionOrigin Origin, IReadOnlyList<PlannedAction> Actions);

public abstract record PlannedAction;

/// <summary>
/// Move one file into quarantine. The size and last-write time seen during the scan are checked again
/// right before the move: if the file changed in between, it is left alone.
/// </summary>
public sealed record QuarantineFileAction(string Path, long ExpectedSizeBytes, DateTime ExpectedLastWriteUtc) : PlannedAction;

/// <summary>Turn a startup program on or off with Windows' own switch (nothing is deleted).</summary>
public sealed record SetStartupApprovalAction(OW.Core.Startup.StartupApprovalTarget Target, bool Enable) : PlannedAction;

public sealed record ExecutionResult(Guid BatchId, int Done, int Skipped, int Failed, long BytesFreed);

public sealed record UndoResult(Guid BatchId, int Restored, int Failed, IReadOnlyList<string> Messages);
