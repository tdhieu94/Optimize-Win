using OW.Core.Execution;
using OW.Core.Journal;
using OW.Core.Safety;
using OW.Core.Startup;

namespace OW.Core.Tests;

public sealed class StartupApprovalTests : IDisposable
{
    private const string Sid = "S-1-5-21-1111-2222-3333-1001";
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ow-startup-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteJournal _journal;
    private readonly MemoryStore _store = new();
    private readonly SafeExecutor _executor;

    public StartupApprovalTests()
    {
        Directory.CreateDirectory(_root);
        _journal = new SqliteJournal(Path.Combine(_root, "journal.db"));
        var style = OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Unix;
        _executor = new SafeExecutor(_journal, new QuarantineStore(Path.Combine(_root, "q")), new PathGuard(style, [Path.Combine(_root, "sys")], []), null, _store);
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

    private static StartupApprovalTarget User(string name = "Spotify") => new(StartupScope.User, Sid, "Run", name);

    [Fact]
    public void Turning_off_writes_the_task_manager_value_and_undo_puts_back_nothing()
    {
        var result = _executor.Execute(new ActionPlan("Tắt khởi động: Spotify", ActionOrigin.UserSelected, [new SetStartupApprovalAction(User(), false)]));

        Assert.Equal(1, result.Done);
        var value = _store.Get(User())!;
        Assert.Equal(12, value.Length);
        Assert.Equal(0x03, value[0]);
        Assert.False(StartupRules.IsEnabled(value));

        var undo = _executor.UndoBatch(result.BatchId);
        Assert.Equal(1, undo.Restored);
        Assert.Null(_store.Get(User()));
        Assert.Equal(BatchState.Undone, _journal.GetBatch(result.BatchId)!.State);
    }

    [Fact]
    public void Turning_on_again_and_undo_restores_the_exact_old_value()
    {
        var old = StartupApprovalValues.For(false, Now.AddDays(-3));
        _store.Set(User(), old);

        var result = _executor.Execute(new ActionPlan("Bật", ActionOrigin.UserSelected, [new SetStartupApprovalAction(User(), true)]));
        Assert.Equal(1, result.Done);
        Assert.True(StartupRules.IsEnabled(_store.Get(User())));
        Assert.Equal(0x02, _store.Get(User())![0]);

        _executor.UndoBatch(result.BatchId);
        Assert.Equal(old, _store.Get(User()));
    }

    [Fact]
    public void Already_in_that_state_is_skipped_and_nothing_written()
    {
        var result = _executor.Execute(new ActionPlan("Bật", ActionOrigin.UserSelected, [new SetStartupApprovalAction(User(), true)]));

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, _store.Writes);
    }

    [Theory]
    [InlineData(StartupScope.User, Sid, "RunOnce", "x")]
    [InlineData(StartupScope.User, Sid, "Run", "")]
    [InlineData(StartupScope.User, Sid, "Run", @"..\..\Policies")]
    [InlineData(StartupScope.User, "S-1-5-18", "Run", "x")]
    [InlineData(StartupScope.User, null, "Run", "x")]
    [InlineData(StartupScope.Machine, Sid, "Run", "x")]
    [InlineData(StartupScope.User, Sid, "Run", "a|b")]
    public void Refuses_anything_but_a_startup_switch(StartupScope scope, string? sid, string group, string name)
    {
        var target = new StartupApprovalTarget(scope, sid, group, name);
        Assert.NotNull(target.Validate());

        var result = _executor.Execute(new ActionPlan("x", ActionOrigin.UserSelected, [new SetStartupApprovalAction(target, false)]));
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, _store.Writes);
    }

    [Fact]
    public void Targets_survive_the_journal()
    {
        foreach (var t in new[] { User("OneDrive Setup"), new StartupApprovalTarget(StartupScope.Machine, null, "Run32", "Logi Options"), new StartupApprovalTarget(StartupScope.User, Sid, "StartupFolder", "Zalo.lnk") })
        {
            Assert.Equal(t, StartupApprovalTarget.Decode(t.Encode()));
        }

        Assert.Null(StartupApprovalTarget.Decode("startup|User|S-1-5-18|Run|x"));
        Assert.Null(StartupApprovalTarget.Decode(@"C:\Users\x\file.txt"));
    }

    [Fact]
    public void Interrupted_change_is_put_back_at_restart()
    {
        var batch = _journal.CreateBatch("Tắt");
        _store.Set(User(), StartupApprovalValues.For(false, Now)); // the write happened, then the service died
        _journal.AddOperation(batch.Id, 0, OperationKind.SetStartupApproval, User().Encode(), "-", 0, OperationState.Pending);

        Assert.Equal(1, _executor.RecoverInterrupted());
        Assert.Null(_store.Get(User()));
    }

    [Fact]
    public void Purging_old_quarantine_ignores_startup_changes()
    {
        var result = _executor.Execute(new ActionPlan("Tắt", ActionOrigin.UserSelected, [new SetStartupApprovalAction(User(), false)]));

        Assert.Equal(0, _executor.PurgeExpired(TimeSpan.Zero - TimeSpan.FromDays(1)));
        Assert.Equal(1, _executor.UndoBatch(result.BatchId).Restored);
    }

    [Fact]
    public void Without_a_store_nothing_changes()
    {
        var style = OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Unix;
        var bare = new SafeExecutor(_journal, new QuarantineStore(Path.Combine(_root, "q2")), new PathGuard(style, [], []));
        Assert.Equal(1, bare.Execute(new ActionPlan("x", ActionOrigin.UserSelected, [new SetStartupApprovalAction(User(), false)])).Skipped);
    }

    private sealed class MemoryStore : IStartupApprovalStore
    {
        private readonly Dictionary<StartupApprovalTarget, byte[]> _values = [];

        public int Writes { get; private set; }

        public byte[]? Get(StartupApprovalTarget target) => _values.TryGetValue(target, out var v) ? v : null;

        public void Set(StartupApprovalTarget target, byte[]? value)
        {
            Writes++;
            if (value is null)
            {
                _values.Remove(target);
            }
            else
            {
                _values[target] = value;
            }
        }
    }
}
