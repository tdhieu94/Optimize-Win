using OW.Core.Execution;
using OW.Core.Journal;
using OW.Core.Safety;
using OW.Core.WindowsCleanup;

namespace OW.Core.Tests;

public class ComponentStoreTests
{
    private const string Sample = """

        Deployment Image Servicing and Management tool
        Version: 10.0.26100.1150

        Image Version: 10.0.26100.2314

        [===========================100.0%==========================]

        Component Store (WinSxS) information:

        Windows Explorer Reported Size of Component Store : 9.12 GB

        Actual Size of Component Store : 8.85 GB

            Shared with Windows : 5.60 GB
            Backups and Disabled Features : 2.95 GB
            Cache and Temporary Data :  310.25 MB

        Date of Last Cleanup : 2026-09-14 03:12:45

        Number of Reclaimable Packages : 4
        Component Store Cleanup Recommended : Yes

        The operation completed successfully.
        """;

    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Parses_dism_analysis()
    {
        var a = ComponentStore.Parse(Sample.Replace("\n", "\r\n"));
        Assert.Equal((long)Math.Round(8.85 * Gb), a.ActualSizeBytes);
        Assert.Equal((long)Math.Round(5.60 * Gb), a.SharedWithWindowsBytes);
        Assert.Equal((long)Math.Round(2.95 * Gb), a.BackupsAndDisabledBytes);
        Assert.Equal((long)Math.Round(310.25 * 1024 * 1024), a.CacheAndTemporaryBytes);
        Assert.Equal(a.BackupsAndDisabledBytes + a.CacheAndTemporaryBytes, a.CleanableBytes);
        Assert.Equal("2026-09-14 03:12:45", a.LastCleanup);
        Assert.Equal(4, a.ReclaimablePackages);
        Assert.True(a.CleanupRecommended);
    }

    [Fact]
    public void Unreadable_output_gives_nulls_not_guesses()
    {
        var a = ComponentStore.Parse("Error: 740\r\nElevated permissions are required to run DISM.");
        Assert.Null(a.ActualSizeBytes);
        Assert.Null(a.CleanableBytes);
        Assert.Null(a.ReclaimablePackages);
        Assert.Null(a.CleanupRecommended);
        Assert.Null(a.LastCleanup);
    }

    [Theory]
    [InlineData("512 bytes", 512L)]
    [InlineData("1.5 KB", 1536L)]
    [InlineData("1,5 KB", 1536L)]
    [InlineData("2 GB", 2L * 1024 * 1024 * 1024)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void Parses_sizes(string text, long? expected) => Assert.Equal(expected, ComponentStore.ParseSize(text));

    [Fact]
    public void Reads_the_last_progress_percentage() =>
        Assert.Equal(42.5, ComponentStore.LastPercent("[==  10.0%  ]\r[=====  42.5%   ]"));

    [Fact]
    public void Never_uses_reset_base()
    {
        Assert.DoesNotContain(ComponentStore.CleanupArguments, a => a.Contains("ResetBase", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("/StartComponentCleanup", ComponentStore.CleanupArguments);
        Assert.Contains("/English", ComponentStore.AnalyzeArguments);
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(3010, true, true)]
    [InlineData(unchecked((int)0x800F0806), false, true)]
    [InlineData(5, false, false)]
    public void Explains_exit_codes(int code, bool success, bool restart)
    {
        var (ok, needRestart, message) = ComponentStore.Explain(code, string.Empty);
        Assert.Equal(success, ok);
        Assert.Equal(restart, needRestart);
        Assert.False(string.IsNullOrWhiteSpace(message));
    }
}

public sealed class ComponentCleanupExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ow-cc-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteJournal _journal;
    private readonly FakeCleaner _cleaner = new();
    private readonly SafeExecutor _executor;

    public ComponentCleanupExecutorTests()
    {
        Directory.CreateDirectory(_root);
        _journal = new SqliteJournal(Path.Combine(_root, "j.db"));
        var style = OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Unix;
        _executor = new SafeExecutor(_journal, new QuarantineStore(Path.Combine(_root, "q")), new PathGuard(style, [Path.Combine(_root, "sys")], []), componentCleaner: _cleaner);
    }

    public void Dispose()
    {
        _journal.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private static ActionPlan Plan(ActionOrigin origin = ActionOrigin.UserSelected) => new("Dọn kho thành phần Windows", origin, [new RunComponentCleanupAction()]);

    [Fact]
    public void Runs_once_and_journals_the_result()
    {
        _cleaner.Next = new ComponentCleanupOutcome(true, false, 3 * 1024L * 1024 * 1024, "Windows đã dọn xong kho thành phần.");

        var result = _executor.Execute(Plan());

        Assert.Equal(1, _cleaner.Calls);
        Assert.Equal(1, result.Done);
        Assert.Equal(3 * 1024L * 1024 * 1024, result.BytesFreed);
        var op = Assert.Single(_journal.GetOperations(result.BatchId));
        Assert.Equal(OperationKind.RunComponentCleanup, op.Kind);
        Assert.Equal(OperationState.Done, op.State);
        Assert.Equal(SafeExecutor.ComponentCleanupTarget, op.TargetPath);
    }

    [Fact]
    public void Failure_is_recorded_as_failed()
    {
        _cleaner.Next = new ComponentCleanupOutcome(false, true, 0, "Windows đang chờ khởi động lại.");
        var result = _executor.Execute(Plan());
        Assert.Equal(1, result.Failed);
        Assert.Equal("Windows đang chờ khởi động lại.", _journal.GetOperations(result.BatchId).Single().Message);
        Assert.Equal(BatchState.CompletedWithProblems, _journal.GetBatch(result.BatchId)!.State);
    }

    [Fact]
    public void Never_runs_from_a_rule_or_schedule()
    {
        var result = _executor.Execute(Plan(ActionOrigin.Rule));
        Assert.Equal(0, _cleaner.Calls);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void Without_a_cleaner_nothing_runs()
    {
        var style = OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Unix;
        var plain = new SafeExecutor(_journal, new QuarantineStore(Path.Combine(_root, "q2")), new PathGuard(style, [], []));
        Assert.Equal(1, plain.Execute(Plan()).Skipped);
    }

    [Fact]
    public void Undo_explains_and_changes_nothing()
    {
        _cleaner.Next = new ComponentCleanupOutcome(true, false, 10, "ok");
        var result = _executor.Execute(Plan());

        var undo = _executor.UndoBatch(result.BatchId);

        Assert.Equal(0, undo.Restored);
        Assert.Equal(0, undo.Failed);
        Assert.Contains(undo.Messages, m => m.Contains("không hoàn tác được", StringComparison.Ordinal));
        Assert.Equal(BatchState.Completed, _journal.GetBatch(result.BatchId)!.State);
        Assert.Equal(OperationState.Done, _journal.GetOperations(result.BatchId).Single().State);
    }

    [Fact]
    public void Interrupted_run_is_settled_as_failed()
    {
        var batch = _journal.CreateBatch("Dọn kho thành phần Windows");
        _journal.AddOperation(batch.Id, 0, OperationKind.RunComponentCleanup, SafeExecutor.ComponentCleanupTarget, null, 0, OperationState.Pending);

        Assert.Equal(1, _executor.RecoverInterrupted());
        Assert.Equal(OperationState.Failed, _journal.GetOperations(batch.Id).Single().State);
    }

    private sealed class FakeCleaner : IComponentStoreCleaner
    {
        public int Calls { get; private set; }

        public ComponentCleanupOutcome Next { get; set; } = new(true, false, 0, "ok");

        public ComponentCleanupOutcome Cleanup(CancellationToken cancellationToken)
        {
            Calls++;
            return Next;
        }
    }
}
