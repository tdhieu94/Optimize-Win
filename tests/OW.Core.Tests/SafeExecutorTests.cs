using OW.Core.Execution;
using OW.Core.Journal;
using OW.Core.Safety;

namespace OW.Core.Tests;

public sealed class SafeExecutorTests : IDisposable
{
    private readonly string _root;
    private readonly string _work;
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 8, 2, 0, 0, TimeSpan.Zero));
    private readonly SqliteJournal _journal;
    private readonly QuarantineStore _quarantine;
    private readonly SafeExecutor _executor;

    public SafeExecutorTests()
    {
        _root = PathNames.ExpandShortNames(Path.Combine(Path.GetTempPath(), "ow-tests-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_root);
        _root = PathNames.ExpandShortNames(_root);
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(_work);

        _journal = new SqliteJournal(Path.Combine(_root, "journal.db"), _time);
        _quarantine = new QuarantineStore(Path.Combine(_root, "quarantine"));
        var style = OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Unix;
        var guard = new PathGuard(style, [Path.Combine(_root, "protected")], [Path.Combine(_root, "mydocs")]);
        _executor = new SafeExecutor(_journal, _quarantine, guard, _time);
    }

    public void Dispose()
    {
        _journal.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Quarantine_then_undo_puts_the_file_back_unchanged()
    {
        var file = MakeFile(_work, "a.tmp", "hello OW");

        var result = _executor.Execute(Plan(ActionOrigin.Rule, file));

        Assert.Equal(1, result.Done);
        Assert.Equal(8, result.BytesFreed);
        Assert.False(File.Exists(file));
        Assert.Equal(BatchState.Completed, _journal.GetBatch(result.BatchId)!.State);

        var undo = _executor.UndoBatch(result.BatchId);

        Assert.Equal(1, undo.Restored);
        Assert.Equal(0, undo.Failed);
        Assert.Equal("hello OW", File.ReadAllText(file));
        Assert.Equal(BatchState.Undone, _journal.GetBatch(result.BatchId)!.State);
        Assert.Empty(Directory.EnumerateFiles(_quarantine.RootDirectory, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Undo_restores_files_inside_folders_that_were_removed_meanwhile()
    {
        var sub = Path.Combine(_work, "sub");
        Directory.CreateDirectory(sub);
        var file = MakeFile(sub, "b.log", "log");

        var result = _executor.Execute(Plan(ActionOrigin.Rule, file));
        Directory.Delete(sub);

        _executor.UndoBatch(result.BatchId);

        Assert.Equal("log", File.ReadAllText(file));
    }

    [Fact]
    public void Undo_never_overwrites_a_new_file_with_the_same_name()
    {
        var file = MakeFile(_work, "c.txt", "old");
        var result = _executor.Execute(Plan(ActionOrigin.Rule, file));
        File.WriteAllText(file, "new");

        var undo = _executor.UndoBatch(result.BatchId);

        Assert.Equal(1, undo.Restored);
        Assert.Equal("new", File.ReadAllText(file));
        var restored = Path.Combine(_work, "c (OW khôi phục 1).txt");
        Assert.Equal("old", File.ReadAllText(restored));
        Assert.Single(undo.Messages);
    }

    [Fact]
    public void Skips_files_that_changed_after_the_scan()
    {
        var file = MakeFile(_work, "d.tmp", "1234");
        var plan = Plan(ActionOrigin.Rule, file);
        File.AppendAllText(file, "more");

        var result = _executor.Execute(plan);

        Assert.Equal(0, result.Done);
        Assert.Equal(1, result.Skipped);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Skips_protected_and_missing_files_but_keeps_going()
    {
        var protectedDir = Path.Combine(_root, "protected");
        Directory.CreateDirectory(protectedDir);
        var system = MakeFile(protectedDir, "sys.dll", "x");
        var mine = Path.Combine(_root, "mydocs");
        Directory.CreateDirectory(mine);
        var doc = MakeFile(mine, "cv.docx", "cv");
        var ok = MakeFile(_work, "ok.tmp", "ok");
        var missing = new QuarantineFileAction(Path.Combine(_work, "ghost.tmp"), 1, DateTime.UtcNow);

        var plan = new ActionPlan("test", ActionOrigin.Rule, [Action(system), Action(doc), missing, Action(ok)]);
        var result = _executor.Execute(plan);

        Assert.Equal(1, result.Done);
        Assert.Equal(3, result.Skipped);
        Assert.True(File.Exists(system));
        Assert.True(File.Exists(doc));
        Assert.False(File.Exists(ok));
        Assert.All(
            _journal.GetOperations(result.BatchId).Where(o => o.State == OperationState.Skipped),
            o => Assert.False(string.IsNullOrWhiteSpace(o.Message)));
    }

    [Fact]
    public void User_selected_plans_may_touch_personal_files()
    {
        var mine = Path.Combine(_root, "mydocs");
        Directory.CreateDirectory(mine);
        var doc = MakeFile(mine, "dup.jpg", "img");

        var result = _executor.Execute(Plan(ActionOrigin.UserSelected, doc));

        Assert.Equal(1, result.Done);
    }

    [Fact]
    public void Refuses_paths_through_symlinks()
    {
        var target = Path.Combine(_root, "protected");
        Directory.CreateDirectory(target);
        MakeFile(target, "secret.dll", "s");
        var link = Path.Combine(_work, "link");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // Creating symlinks needs extra rights on some Windows machines.
        }

        var viaLink = Path.Combine(link, "secret.dll");
        var result = _executor.Execute(Plan(ActionOrigin.UserSelected, viaLink));

        Assert.Equal(0, result.Done);
        Assert.True(File.Exists(Path.Combine(target, "secret.dll")));
    }

    [Fact]
    public void Recovery_settles_operations_interrupted_by_a_crash()
    {
        var movedFile = MakeFile(_work, "moved.tmp", "m");
        var notMoved = MakeFile(_work, "notmoved.tmp", "n");
        var batch = _journal.CreateBatch("crash");

        var opMoved = Guid.NewGuid();
        var backupMoved = _quarantine.GetBackupPath(batch.Id, opMoved);
        _journal.AddOperation(batch.Id, 0, OperationKind.QuarantineFile, movedFile, backupMoved, 1, OperationState.Pending, id: opMoved);
        _quarantine.MoveIn(movedFile, backupMoved, 1);

        var opNotMoved = Guid.NewGuid();
        _journal.AddOperation(batch.Id, 1, OperationKind.QuarantineFile, notMoved, _quarantine.GetBackupPath(batch.Id, opNotMoved), 1, OperationState.Pending, id: opNotMoved);

        Assert.Equal(2, _executor.RecoverInterrupted());

        var ops = _journal.GetOperations(batch.Id);
        Assert.Equal(OperationState.Done, ops.Single(o => o.Id == opMoved).State);
        Assert.Equal(OperationState.Failed, ops.Single(o => o.Id == opNotMoved).State);
        Assert.Empty(_journal.GetOperationsInState(OperationState.Pending));

        _executor.UndoBatch(batch.Id);
        Assert.True(File.Exists(movedFile));
        Assert.True(File.Exists(notMoved));
    }

    [Fact]
    public void Purge_only_after_retention_and_then_undo_explains_why()
    {
        var file = MakeFile(_work, "e.tmp", "e");
        var result = _executor.Execute(Plan(ActionOrigin.Rule, file));

        _time.Advance(TimeSpan.FromDays(6));
        Assert.Equal(0, _executor.PurgeExpired(TimeSpan.FromDays(7)));

        _time.Advance(TimeSpan.FromDays(2));
        Assert.Equal(1, _executor.PurgeExpired(TimeSpan.FromDays(7)));
        Assert.Empty(Directory.EnumerateFiles(_quarantine.RootDirectory, "*", SearchOption.AllDirectories));

        var undo = _executor.UndoBatch(result.BatchId);
        Assert.Equal(0, undo.Restored);
        Assert.Contains(undo.Messages, m => m.Contains("quá hạn", StringComparison.Ordinal));
        Assert.Equal(BatchState.PartiallyUndone, _journal.GetBatch(result.BatchId)!.State);
    }

    [Fact]
    public void Undo_of_unknown_batch_is_harmless()
    {
        var undo = _executor.UndoBatch(Guid.NewGuid());
        Assert.Equal(0, undo.Restored);
        Assert.Single(undo.Messages);
    }

    [Fact]
    public void Purge_refuses_paths_outside_quarantine()
    {
        var outside = MakeFile(_work, "keep.txt", "k");
        Assert.Throws<InvalidOperationException>(() => _quarantine.Purge(outside));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Journal_survives_reopening()
    {
        var file = MakeFile(_work, "f.tmp", "f");
        var result = _executor.Execute(Plan(ActionOrigin.Rule, file));
        _journal.Dispose();

        using var reopened = new SqliteJournal(Path.Combine(_root, "journal.db"), _time);
        var batches = reopened.GetRecentBatches(10);
        Assert.Contains(batches, b => b.Id == result.BatchId);
        Assert.Equal(OperationState.Done, reopened.GetOperations(result.BatchId).Single().State);
    }

    private static string MakeFile(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static QuarantineFileAction Action(string path)
    {
        var info = new FileInfo(path);
        return new QuarantineFileAction(path, info.Length, info.LastWriteTimeUtc);
    }

    private static ActionPlan Plan(ActionOrigin origin, params string[] files) =>
        new("Dọn thử", origin, files.Select(Action).ToList());

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
