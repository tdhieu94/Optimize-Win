using OW.Core.Cleanup;
using OW.Core.Execution;
using OW.Core.Journal;
using OW.Core.Safety;

namespace OW.Core.Tests;

/// <summary>The phase 2 junk categories: browser and graphics caches, error reports, Windows temp and update downloads.</summary>
public sealed class WindowsJunkTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ow-wjunk-" + Guid.NewGuid().ToString("N"));
    private readonly string _local;

    public WindowsJunkTests()
    {
        _local = Path.Combine(_root, "Local");
        Directory.CreateDirectory(_local);
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
    public void Browser_cache_takes_only_cache_folders_of_real_profiles()
    {
        var edge = Path.Combine(_local, "Microsoft", "Edge", "User Data");
        var chrome = Path.Combine(_local, "Google", "Chrome", "User Data");
        var keep = new[]
        {
            Make(Path.Combine(edge, "Default", "Cookies"), Now.AddDays(-30)),
            Make(Path.Combine(edge, "Default", "Login Data"), Now.AddDays(-30)),
            Make(Path.Combine(edge, "Default", "History"), Now.AddDays(-30)),
            Make(Path.Combine(edge, "Default", "Bookmarks"), Now.AddDays(-30)),
            Make(Path.Combine(edge, "Default", "Local Storage", "leveldb", "000003.log"), Now.AddDays(-30)),
            Make(Path.Combine(edge, "System Profile", "Cache", "Cache_Data", "f_000001"), Now.AddDays(-30)),
            Make(Path.Combine(edge, "Default", "Cache", "Cache_Data", "f_new"), Now.AddHours(-3)),
        };
        var take = new[]
        {
            Make(Path.Combine(edge, "Default", "Cache", "Cache_Data", "f_000001"), Now.AddDays(-30)),
            Make(Path.Combine(edge, "Default", "Code Cache", "js", "abc_0"), Now.AddDays(-30)),
            Make(Path.Combine(chrome, "Profile 2", "GPUCache", "data_0"), Now.AddDays(-30)),
            Make(Path.Combine(_local, "Mozilla", "Firefox", "Profiles", "x1.default-release", "cache2", "entries", "AB12"), Now.AddDays(-30)),
        };

        var cat = JunkRules.BrowserCache(_local, Now, NotInUse());

        Assert.Equal(JunkRules.BrowserCacheId, cat.Id);
        Assert.Equal(take.Order(), cat.Files.Select(f => f.Path).Order());
        Assert.DoesNotContain(cat.Files, f => keep.Contains(f.Path));
    }

    [Theory]
    [InlineData("Default", true)]
    [InlineData("Profile 1", true)]
    [InlineData("Profile 12", true)]
    [InlineData("System Profile", false)]
    [InlineData("Guest Profile", false)]
    [InlineData("Profile ", false)]
    [InlineData("Profile x", false)]
    [InlineData("Crashpad", false)]
    public void Recognizes_browser_profiles(string name, bool expected) =>
        Assert.Equal(expected, JunkRules.IsChromiumProfile(Path.Combine(_local, name)));

    [Fact]
    public void Shader_cache_and_error_reports_take_old_files()
    {
        var programData = Path.Combine(_root, "ProgramData");
        var shader = Make(Path.Combine(_local, "NVIDIA", "DXCache", "a.bin"), Now.AddDays(-5));
        var d3d = Make(Path.Combine(_local, "D3DSCache", "x", "y.idx"), Now.AddDays(-5));
        var userReport = Make(Path.Combine(_local, "Microsoft", "Windows", "WER", "ReportArchive", "r1", "Report.wer"), Now.AddDays(-5));
        var machineReport = Make(Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue", "r2", "Report.wer"), Now.AddDays(-5));
        Make(Path.Combine(programData, "Microsoft", "Windows", "WER", "Temp", "keep.tmp"), Now.AddDays(-5));

        Assert.Equal(new[] { shader, d3d }.Order(), JunkRules.ShaderCache(_local, Now, NotInUse()).Files.Select(f => f.Path).Order());
        Assert.Equal(new[] { userReport, machineReport }.Order(), JunkRules.ErrorReports(_local, programData, Now, NotInUse()).Files.Select(f => f.Path).Order());
    }

    [Fact]
    public void Windows_temp_waits_seven_days_and_update_downloads_ten()
    {
        var windows = Path.Combine(_root, "Windows");
        var temp8 = Make(Path.Combine(windows, "Temp", "old.tmp"), Now.AddDays(-8));
        Make(Path.Combine(windows, "Temp", "week.tmp"), Now.AddDays(-5));
        var update11 = Make(Path.Combine(windows, "SoftwareDistribution", "Download", "abc", "x.cab"), Now.AddDays(-11));
        Make(Path.Combine(windows, "SoftwareDistribution", "Download", "abc", "y.cab"), Now.AddDays(-8));
        Make(Path.Combine(windows, "SoftwareDistribution", "DataStore", "DataStore.edb"), Now.AddDays(-60));

        // Even a caller asking for a shorter age gets the safer minimum.
        var shortAge = new JunkScanOptions { IsInUse = _ => false, MinAge = TimeSpan.FromHours(1) };

        Assert.Equal([temp8], JunkRules.WindowsTemp(windows, Now, shortAge).Files.Select(f => f.Path));
        Assert.Equal([update11], JunkRules.WindowsUpdateDownloads(windows, Now, shortAge).Files.Select(f => f.Path));
    }

    [Fact]
    public void File_cap_counts_for_the_whole_category()
    {
        for (var i = 0; i < 5; i++)
        {
            Make(Path.Combine(_local, "NVIDIA", "DXCache", $"a{i}"), Now.AddDays(-5));
            Make(Path.Combine(_local, "AMD", "DxCache", $"b{i}"), Now.AddDays(-5));
        }

        var cat = JunkRules.ShaderCache(_local, Now, new JunkScanOptions { IsInUse = _ => false, MaxFiles = 7 });

        Assert.Equal(7, cat.Files.Count);
    }

    [Fact]
    public void Missing_browsers_give_an_empty_category()
    {
        Assert.Empty(JunkRules.BrowserCacheFolders(Path.Combine(_root, "nope")));
        Assert.Empty(JunkRules.BrowserCache(Path.Combine(_root, "nope"), Now).Files);
    }

    [Fact]
    public void Windows_junk_can_be_cleaned_and_undone_through_the_guard()
    {
        var windows = Path.Combine(_root, "Windows");
        var file = Make(Path.Combine(windows, "Temp", "setup.log"), Now.AddDays(-30), bytes: 50);
        var style = OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Unix;
        var guard = new PathGuard(style, [windows], [], [Path.Combine(windows, "Temp")]);

        using var journal = new SqliteJournal(Path.Combine(_root, "journal.db"));
        var executor = new SafeExecutor(journal, new QuarantineStore(Path.Combine(_root, "quarantine")), guard);
        var result = executor.Execute(new ActionPlan("Dọn rác", ActionOrigin.Rule, JunkRules.WindowsTemp(windows, Now, NotInUse()).Files));

        Assert.Equal(1, result.Done);
        Assert.False(File.Exists(file));
        executor.UndoBatch(result.BatchId);
        Assert.True(File.Exists(file));
    }

    private static JunkScanOptions NotInUse() => new() { IsInUse = _ => false };

    private static string Make(string path, DateTime whenUtc, int bytes = 4)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetCreationTimeUtc(path, whenUtc);
        File.SetLastWriteTimeUtc(path, whenUtc);
        return path;
    }
}
