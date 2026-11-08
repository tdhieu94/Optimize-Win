using OW.Core.Journal;
using OW.Core.Safety;
using OW.Core.Startup;

namespace OW.Core.Execution;

/// <summary>
/// The only component allowed to change files. Every change follows the same steps:
/// check the path → record it as pending → do it → record the outcome. Every change can be undone
/// until its quarantined copy is purged.
/// </summary>
public sealed class SafeExecutor
{
    private readonly SqliteJournal _journal;
    private readonly QuarantineStore _quarantine;
    private readonly PathGuard _guard;
    private readonly TimeProvider _time;
    private readonly IStartupApprovalStore? _startup;
    private readonly Lock _runGate = new();

    public SafeExecutor(SqliteJournal journal, QuarantineStore quarantine, PathGuard guard, TimeProvider? time = null, IStartupApprovalStore? startup = null)
    {
        _journal = journal;
        _quarantine = quarantine;
        _guard = guard;
        _time = time ?? TimeProvider.System;
        _startup = startup;
    }

    /// <summary>Carries out a plan. One bad file never stops the rest; each outcome is recorded.</summary>
    public ExecutionResult Execute(ActionPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        lock (_runGate)
        {
            var batch = _journal.CreateBatch(plan.Title);
            int done = 0, skipped = 0, failed = 0;
            long bytes = 0;

            for (var i = 0; i < plan.Actions.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                switch (plan.Actions[i])
                {
                    case QuarantineFileAction or SetStartupApprovalAction:
                        var outcome = plan.Actions[i] is QuarantineFileAction q
                            ? QuarantineOne(batch.Id, i, q, plan.Origin)
                            : SetStartupOne(batch.Id, i, (SetStartupApprovalAction)plan.Actions[i]);
                        if (outcome.State == OperationState.Done)
                        {
                            done++;
                            bytes += outcome.SizeBytes;
                        }
                        else if (outcome.State == OperationState.Skipped)
                        {
                            skipped++;
                        }
                        else
                        {
                            failed++;
                        }

                        break;

                    default:
                        _journal.AddOperation(batch.Id, i, OperationKind.QuarantineFile, "?", null, 0, OperationState.Skipped, "Loại thao tác không được hỗ trợ.");
                        skipped++;
                        break;
                }
            }

            _journal.SetBatchState(batch.Id, failed > 0 ? BatchState.CompletedWithProblems : BatchState.Completed);
            return new ExecutionResult(batch.Id, done, skipped, failed, bytes);
        }
    }

    /// <summary>Puts back everything a batch changed, newest change first.</summary>
    public UndoResult UndoBatch(Guid batchId)
    {
        lock (_runGate)
        {
            var batch = _journal.GetBatch(batchId);
            if (batch is null)
            {
                return new UndoResult(batchId, 0, 0, ["Không tìm thấy lần dọn này trong nhật ký."]);
            }

            var messages = new List<string>();
            int restored = 0, failed = 0;

            foreach (var op in _journal.GetOperations(batchId).Where(o => o.State is OperationState.Done or OperationState.UndoFailed).OrderByDescending(o => o.Sequence))
            {
                if (op.Kind == OperationKind.SetStartupApproval)
                {
                    if (UndoStartup(op) is { } problem)
                    {
                        messages.Add(problem);
                        failed++;
                    }
                    else
                    {
                        restored++;
                    }

                    continue;
                }

                try
                {
                    var landedAt = _quarantine.Restore(op.BackupPath!, op.TargetPath);
                    var note = landedAt == op.TargetPath ? null : $"Đã có file trùng tên nên khôi phục thành: {landedAt}";
                    _journal.UpdateOperation(op.Id, OperationState.Undone, note, landedAt);
                    if (note is not null)
                    {
                        messages.Add(note);
                    }

                    restored++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _journal.UpdateOperation(op.Id, OperationState.UndoFailed, ex.Message);
                    messages.Add($"Không khôi phục được {op.TargetPath}: {ex.Message}");
                    failed++;
                }
            }

            foreach (var op in _journal.GetOperations(batchId).Where(o => o.State == OperationState.Purged))
            {
                messages.Add($"{op.TargetPath} đã quá hạn giữ trong vùng cách ly nên không khôi phục được.");
            }

            var anyLeft = _journal.GetOperations(batchId).Any(o => o.State is OperationState.UndoFailed or OperationState.Purged);
            _journal.SetBatchState(batchId, anyLeft ? BatchState.PartiallyUndone : BatchState.Undone);
            return new UndoResult(batchId, restored, failed, messages);
        }
    }

    /// <summary>
    /// Run at service start. Settles operations left <see cref="OperationState.Pending"/> by a crash:
    /// if the file reached quarantine the move counts as done, otherwise as failed (the original is still in place).
    /// </summary>
    public int RecoverInterrupted()
    {
        lock (_runGate)
        {
            var settled = 0;
            foreach (var op in _journal.GetOperationsInState(OperationState.Pending))
            {
                if (op.Kind == OperationKind.SetStartupApproval)
                {
                    // We cannot tell whether the switch was flipped: put back what was there before.
                    var problem = UndoStartup(op);
                    _journal.UpdateOperation(op.Id, OperationState.Failed, problem ?? "Bị gián đoạn; công tắc khởi động đã được trả lại như cũ.");
                    settled++;
                    continue;
                }

                var inQuarantine = op.BackupPath is not null && File.Exists(op.BackupPath);
                var stillAtSource = File.Exists(op.TargetPath);

                if (inQuarantine && !stillAtSource)
                {
                    _journal.UpdateOperation(op.Id, OperationState.Done, "Hoàn tất sau khi dịch vụ khởi động lại.");
                }
                else
                {
                    _journal.UpdateOperation(op.Id, OperationState.Failed, "Bị gián đoạn; file gốc vẫn ở chỗ cũ.");
                }

                settled++;
            }

            return settled;
        }
    }

    /// <summary>Deletes quarantined copies older than <paramref name="retention"/>. After this they cannot be restored.</summary>
    public int PurgeExpired(TimeSpan retention)
    {
        lock (_runGate)
        {
            var cutoff = _time.GetUtcNow() - retention;
            var purged = 0;
            foreach (var op in _journal.GetOperationsInState(OperationState.Done).Concat(_journal.GetOperationsInState(OperationState.UndoFailed)))
            {
                if (op.Kind != OperationKind.QuarantineFile || op.UpdatedUtc > cutoff || op.BackupPath is null)
                {
                    continue;
                }

                try
                {
                    _quarantine.Purge(op.BackupPath);
                    _journal.UpdateOperation(op.Id, OperationState.Purged, "Đã xóa hẳn sau thời gian giữ.");
                    purged++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Try again next time; nothing is lost by waiting.
                }
            }

            return purged;
        }
    }

    private OperationRecord SetStartupOne(Guid batchId, int sequence, SetStartupApprovalAction action)
    {
        var target = action.Target;
        if (_startup is null)
        {
            return _journal.AddOperation(batchId, sequence, OperationKind.SetStartupApproval, target.Encode(), null, 0, OperationState.Skipped, "Không đổi được công tắc khởi động ở đây.");
        }

        if (target.Validate() is { } invalid)
        {
            return _journal.AddOperation(batchId, sequence, OperationKind.SetStartupApproval, target.Encode(), null, 0, OperationState.Skipped, invalid);
        }

        byte[]? before;
        try
        {
            before = _startup.Get(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return _journal.AddOperation(batchId, sequence, OperationKind.SetStartupApproval, target.Encode(), null, 0, OperationState.Failed, ex.Message);
        }

        if (StartupRules.IsEnabled(before) == action.Enable)
        {
            return _journal.AddOperation(batchId, sequence, OperationKind.SetStartupApproval, target.Encode(), null, 0, OperationState.Skipped, action.Enable ? "Đã bật sẵn." : "Đã tắt sẵn.");
        }

        // Record first (with the old value, for undo), act second.
        var op = _journal.AddOperation(batchId, sequence, OperationKind.SetStartupApproval, target.Encode(), StartupApprovalValues.ToText(before), 0, OperationState.Pending, action.Enable ? "Bật khởi động" : "Tắt khởi động");
        try
        {
            _startup.Set(target, StartupApprovalValues.For(action.Enable, _time.GetUtcNow().UtcDateTime));
            _journal.UpdateOperation(op.Id, OperationState.Done, op.Message);
            return op with { State = OperationState.Done };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _journal.UpdateOperation(op.Id, OperationState.Failed, ex.Message);
            return op with { State = OperationState.Failed };
        }
    }

    /// <summary>Puts a startup switch back to the value it had before. Returns a message if that failed.</summary>
    private string? UndoStartup(OperationRecord op)
    {
        var target = StartupApprovalTarget.Decode(op.TargetPath);
        if (_startup is null || target is null)
        {
            _journal.UpdateOperation(op.Id, OperationState.UndoFailed, "Không đọc được công tắc khởi động cần trả lại.");
            return $"Không hoàn tác được: {op.TargetPath}";
        }

        try
        {
            _startup.Set(target, StartupApprovalValues.FromText(op.BackupPath));
            if (op.State != OperationState.Pending)
            {
                _journal.UpdateOperation(op.Id, OperationState.Undone);
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or FormatException)
        {
            if (op.State != OperationState.Pending)
            {
                _journal.UpdateOperation(op.Id, OperationState.UndoFailed, ex.Message);
            }

            return $"Không hoàn tác được {target.ValueName}: {ex.Message}";
        }
    }

    private OperationRecord QuarantineOne(Guid batchId, int sequence, QuarantineFileAction action, ActionOrigin origin)
    {
        action = action with { Path = PathNames.ExpandShortNames(action.Path) };
        var check = _guard.Check(action.Path, origin);
        if (!check.Allowed)
        {
            return Skip(check.Reason!);
        }

        FileInfo info;
        try
        {
            info = new FileInfo(action.Path);
            if (!info.Exists)
            {
                return Skip("File không còn tồn tại.");
            }

            if (FileSafety.HasReparsePointOnPath(info))
            {
                return Skip("Đường dẫn đi qua liên kết (symlink/junction); bỏ qua để an toàn.");
            }

            if (info.Length != action.ExpectedSizeBytes || info.LastWriteTimeUtc != action.ExpectedLastWriteUtc)
            {
                return Skip("File đã thay đổi sau khi quét; bỏ qua.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Skip($"Không đọc được thông tin file: {ex.Message}");
        }

        // Record first, act second: a crash between the two is settled by RecoverInterrupted().
        var opId = Guid.NewGuid();
        var backupPath = _quarantine.GetBackupPath(batchId, opId, action.Path);
        var op = _journal.AddOperation(batchId, sequence, OperationKind.QuarantineFile, action.Path, backupPath, info.Length, OperationState.Pending, id: opId);

        try
        {
            _quarantine.MoveIn(action.Path, backupPath, info.Length);
            _journal.UpdateOperation(op.Id, OperationState.Done);
            return op with { State = OperationState.Done };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If the move half-happened, put the file back where it was.
            if (File.Exists(backupPath) && !File.Exists(action.Path))
            {
                try
                {
                    File.Move(backupPath, action.Path);
                }
                catch (Exception moveBack) when (moveBack is IOException or UnauthorizedAccessException)
                {
                    _journal.UpdateOperation(op.Id, OperationState.Done, $"Đã vào vùng cách ly nhưng kiểm tra sau đó lỗi: {ex.Message}");
                    return op with { State = OperationState.Done };
                }
            }

            _journal.UpdateOperation(op.Id, OperationState.Failed, ex.Message);
            return op with { State = OperationState.Failed };
        }

        OperationRecord Skip(string reason) =>
            _journal.AddOperation(batchId, sequence, OperationKind.QuarantineFile, action.Path, null, 0, OperationState.Skipped, reason);
    }
}
