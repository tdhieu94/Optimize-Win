using OW.Core.Cleanup;
using OW.Core.Execution;
using OW.Core.Journal;
using OW.Core.Safety;

namespace OW.Core.Tests;

public sealed class JunkRulesTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ow-junk-" + Guid.NewGuid().ToString("N"));
    private readonly string _temp;

    public JunkRulesTests()
    {
        _temp = Path.Combine(_root, "Temp");
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Picks_only_files_older_than_two_days()
    {
        var old = Make("old.tmp", Now.AddDays(-10));
        Make("recent.tmp", Now.AddHours(-5));
        Make("edge.tmp", Now.AddDays(-1.9));

        var cat = JunkRules.UserTemp(_temp, Now, NotInUse());

        Assert.Equal([old], cat.Files.Select(f => f.Path));
        Assert.Equal(JunkRules.UserTempId, cat.Id);
    }

    [Fact]
    public void A_fresh_copy_of_an_old_file_is_kept()
    {
        var path = Make("copied.tmp", Now.AddDays(-30));
        File.SetCreationTimeUtc(path, Now.AddMinutes(-10));

        var cat = JunkRules.UserTemp(_temp, Now, NotInUse());

        if (File.GetCreationTimeUtc(path) > Now.AddDays(-2))
        {
            Assert.Empty(cat.Files);
        }
    }

    [Fact]
    public void Looks_inside_sub_folders()
    {
        var nested = Make(Path.Combine("a", "b", "c.log"), Now.AddDays(-5));

        var cat = JunkRules.UserTemp(_temp, Now, NotInUse());

        Assert.Equal([nested], cat.Files.Select(f => f.Path));
    }

    [Fact]
    public void Skips_files_in_use()
    {
        var busy = Make("busy.tmp", Now.AddDays(-5));
        var free = Make("free.tmp", Now.AddDays(-5));

        var cat = JunkRules.UserTemp(_temp, Now, new JunkScanOptions { IsInUse = p => p == busy });

        Assert.Equal([free], cat.Files.Select(f => f.Path));
    }

    [Fact]
    public void Records_size_and_date_so_changed_files_are_left_alone_later()
    {
        var path = Make("x.tmp", Now.AddDays(-5), bytes: 123);

        var file = Assert.Single(JunkRules.UserTemp(_temp, Now, NotInUse()).Files);

        Assert.Equal(123, file.ExpectedSizeBytes);
        Assert.Equal(File.GetLastWriteTimeUtc(path), file.ExpectedLastWriteUtc);
    }

    [Fact]
    public void Stops_at_the_file_cap()
    {
        for (var i = 0; i < 10; i++)
        {
            Make($"f{i}.tmp", Now.AddDays(-5));
        }

        var cat = JunkRules.UserTemp(_temp, Now, new JunkScanOptions { IsInUse = _ => false, MaxFiles = 4 });

        Assert.Equal(4, cat.Files.Count);
    }

    [Fact]
    public void Does_not_follow_folder_links()
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var precious = Path.Combine(outside, "precious.docx");
        File.WriteAllText(precious, "keep");
        File.SetCreationTimeUtc(precious, Now.AddDays(-50));
        File.SetLastWriteTimeUtc(precious, Now.AddDays(-50));
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_temp, "link"), outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var cat = JunkRules.UserTemp(_temp, Now, NotInUse());

        Assert.DoesNotContain(cat.Files, f => f.Path.EndsWith("precious.docx", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_folder_gives_an_empty_category()
    {
        var cat = JunkRules.CrashDumps(Path.Combine(_root, "nope"), Now);
        Assert.Empty(cat.Files);
        Assert.Equal(0, cat.Bytes);
    }

    [Fact]
    public void Default_in_use_check_sees_an_open_file()
    {
        var path = Make("open.tmp", Now.AddDays(-5));
        Assert.False(JunkScanOptions.FileInUse(path));
        if (OperatingSystem.IsWindows())
        {
            using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Assert.True(JunkScanOptions.FileInUse(path));
        }
    }

    [Fact]
    public void Plan_from_a_rule_goes_through_the_safe_executor_and_can_be_undone()
    {
        var a = Make("a.tmp", Now.AddDays(-5), bytes: 10);
        var b = Make(Path.Combine("sub", "b.tmp"), Now.AddDays(-5), bytes: 20);
        var cat = JunkRules.UserTemp(_temp, Now, NotInUse());

        using var journal = new SqliteJournal(Path.Combine(_root, "journal.db"));
        var executor = new SafeExecutor(
            journal,
            new QuarantineStore(Path.Combine(_root, "quarantine")),
            new PathGuard(OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Unix, [Path.Combine(_root, "system")], [Path.Combine(_root, "Documents")]));

        var result = executor.Execute(new ActionPlan("Dọn rác", ActionOrigin.Rule, cat.Files));

        Assert.Equal(2, result.Done);
        Assert.Equal(30, result.BytesFreed);
        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));

        executor.UndoBatch(result.BatchId);
        Assert.True(File.Exists(a));
        Assert.True(File.Exists(b));
    }

    private static JunkScanOptions NotInUse() => new() { IsInUse = _ => false };

    private string Make(string relative, DateTime whenUtc, int bytes = 4)
    {
        var path = Path.Combine(_temp, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetCreationTimeUtc(path, whenUtc);
        File.SetLastWriteTimeUtc(path, whenUtc);
        return path;
    }
}
