using OW.Core.Duplicates;
using OW.Core.Execution;
using OW.Core.Journal;
using OW.Core.Safety;

namespace OW.Core.Tests;

public sealed class DuplicateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ow-dup-" + Guid.NewGuid().ToString("N"));
    private static readonly DuplicateScanOptions Small = new() { MinSizeBytes = 1 };

    public DuplicateTests() => Directory.CreateDirectory(_root);

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
    public void Finds_files_with_the_same_content()
    {
        var a = Make(Path.Combine("Photos", "a.jpg"), "same picture");
        var b = Make(Path.Combine("Backup", "copy of a.jpg"), "same picture");
        Make(Path.Combine("Photos", "c.jpg"), "same pictur!"); // same size, different content
        Make(Path.Combine("Photos", "d.jpg"), "other");

        var result = DuplicateFinder.Find([_root], Small);

        var g = Assert.Single(result.Groups);
        Assert.Equal(new[] { a, b }.Order(), g.Files.Select(f => f.Path).Order());
        Assert.Equal(12, g.SizeBytes);
        Assert.Equal(12, g.WastedBytes);
        Assert.Equal(DuplicateFinder.Sha256Of(a), g.Sha256);
    }

    [Fact]
    public void Same_start_and_end_but_different_middle_is_not_a_duplicate()
    {
        var head = new string('x', 70_000);
        Make("a.mp4", head + "A" + head);
        Make("b.mp4", head + "B" + head);

        Assert.Empty(DuplicateFinder.Find([_root], Small).Groups);
    }

    [Fact]
    public void Ignores_program_folders_and_everything_inside_them()
    {
        Make(Path.Combine("Games", "MyGame", "game.exe"), "exe");
        Make(Path.Combine("Games", "MyGame", "data", "intro.mp4"), "video");
        Make(Path.Combine("Games", "Other", "intro.mp4"), "video");
        Make(Path.Combine("Videos", "intro.mp4"), "video");

        var result = DuplicateFinder.Find([_root], Small);

        var g = Assert.Single(result.Groups);
        Assert.DoesNotContain(g.Files, f => f.Path.Contains("MyGame", StringComparison.Ordinal));
        Assert.Equal(2, g.Files.Count);
        Assert.Equal(1, result.SkippedProgramFolders);
    }

    [Fact]
    public void Ignores_system_folder_names_other_file_types_and_small_files()
    {
        Make(Path.Combine("AppData", "x.jpg"), "dup");
        Make(Path.Combine("Windows", "x.jpg"), "dup");
        Make(Path.Combine("node_modules", "x.jpg"), "dup");
        Make(Path.Combine("a", "x.dat"), "dup");
        Make(Path.Combine("b", "x.dat"), "dup");
        Make(Path.Combine("a", "tiny.jpg"), "t");
        Make(Path.Combine("b", "tiny.jpg"), "t");

        Assert.Empty(DuplicateFinder.Find([_root], new DuplicateScanOptions { MinSizeBytes = 2 }).Groups);
    }

    [Fact]
    public void Skips_listed_folders()
    {
        Make(Path.Combine("keep", "x.jpg"), "dup");
        Make(Path.Combine("skip", "x.jpg"), "dup");
        Make(Path.Combine("other", "x.jpg"), "dup");

        var result = DuplicateFinder.Find([_root], Small with { SkippedFolders = [Path.Combine(_root, "skip")] });

        Assert.DoesNotContain(Assert.Single(result.Groups).Files, f => f.Path.Contains("skip", StringComparison.Ordinal));
    }

    [Fact]
    public void Biggest_waste_comes_first_and_the_group_cap_holds()
    {
        Make("s1.jpg", "aa");
        Make("s2.jpg", "aa");
        Make("b1.jpg", "bbbbbbbb");
        Make("b2.jpg", "bbbbbbbb");
        Make("b3.jpg", "bbbbbbbb");

        var result = DuplicateFinder.Find([_root], Small with { MaxGroups = 1 });

        Assert.Equal(16, Assert.Single(result.Groups).WastedBytes);
    }

    [Fact]
    public void Does_not_follow_folder_links()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ow-dup-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllText(Path.Combine(outside, "x.jpg"), "dup");
            Make("x.jpg", "dup");
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }

            Assert.Empty(DuplicateFinder.Find([_root], Small).Groups);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Cloud_only_files_are_recognized() =>
        Assert.True(DuplicateFinder.IsCloudOnly((FileAttributes)0x400000 | FileAttributes.Archive));

    [Fact]
    public void Removal_keeps_at_least_one_copy()
    {
        var (g, a, b) = Pair();

        var all = DuplicateRemoval.Plan([g], [a, b], p => DuplicateFinder.Sha256Of(p));
        Assert.NotNull(all.Error);
        Assert.Empty(all.Actions);

        var one = DuplicateRemoval.Plan([g], [b], p => DuplicateFinder.Sha256Of(p));
        Assert.Null(one.Error);
        Assert.Equal([b], one.Actions.Select(x => x.Path));
    }

    [Fact]
    public void Removal_refuses_paths_the_scan_did_not_report()
    {
        var (g, _, _) = Pair();
        var stranger = Make("stranger.jpg", "same picture");

        Assert.NotNull(DuplicateRemoval.Plan([g], [stranger], p => DuplicateFinder.Sha256Of(p)).Error);
        Assert.NotNull(DuplicateRemoval.Plan([g], [], p => DuplicateFinder.Sha256Of(p)).Error);
    }

    [Fact]
    public void Removal_skips_a_group_whose_kept_copy_changed_or_vanished()
    {
        var (g, a, b) = Pair();
        File.WriteAllText(a, "edited since");

        var plan = DuplicateRemoval.Plan([g], [b], p => DuplicateFinder.Sha256Of(p));
        Assert.Empty(plan.Actions);
        Assert.Equal(1, plan.Skipped);

        File.Delete(a);
        Assert.Empty(DuplicateRemoval.Plan([g], [b], p => DuplicateFinder.Sha256Of(p)).Actions);
    }

    [Fact]
    public void Removal_skips_a_copy_that_changed_since_the_scan()
    {
        var (g, _, b) = Pair();
        File.WriteAllText(b, "edited since");

        var plan = DuplicateRemoval.Plan([g], [b], p => DuplicateFinder.Sha256Of(p));
        Assert.Empty(plan.Actions);
        Assert.Equal(1, plan.Skipped);
    }

    [Fact]
    public void Removed_copy_goes_to_quarantine_and_comes_back_on_undo()
    {
        var (g, a, b) = Pair();
        var plan = DuplicateRemoval.Plan([g], [b], p => DuplicateFinder.Sha256Of(p));
        var style = OperatingSystem.IsWindows() ? PathStyle.Windows : PathStyle.Unix;
        using var journal = new SqliteJournal(Path.Combine(_root, "journal.db"));
        var executor = new SafeExecutor(journal, new QuarantineStore(Path.Combine(_root, "q")), new PathGuard(style, [Path.Combine(_root, "sys")], [Path.Combine(_root, "Documents")]));

        var result = executor.Execute(new ActionPlan("Bỏ file trùng", ActionOrigin.UserSelected, plan.Actions));

        Assert.Equal(1, result.Done);
        Assert.True(File.Exists(a));
        Assert.False(File.Exists(b));
        executor.UndoBatch(result.BatchId);
        Assert.True(File.Exists(b));
    }

    [Fact]
    public void Quarantine_refuses_a_backup_path_outside_its_folder()
    {
        var store = new QuarantineStore(Path.Combine(_root, "q"));
        var file = Make("x.jpg", "data");

        Assert.Throws<InvalidOperationException>(() => store.MoveIn(file, Path.Combine(_root, "elsewhere", "x.owq"), 4));
        Assert.True(File.Exists(file));
        Assert.True(store.IsInsideRoot(store.GetBackupPath(Guid.NewGuid(), Guid.NewGuid(), file)));
    }

    private (DuplicateGroup Group, string A, string B) Pair()
    {
        var a = Make(Path.Combine("Photos", "a.jpg"), "same picture");
        var b = Make(Path.Combine("Backup", "a.jpg"), "same picture");
        var g = Assert.Single(DuplicateFinder.Find([Path.Combine(_root, "Photos"), Path.Combine(_root, "Backup")], Small).Groups);
        return (g, a, b);
    }

    private string Make(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
