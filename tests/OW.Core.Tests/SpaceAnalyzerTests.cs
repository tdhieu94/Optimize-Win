using OW.Core.Space;

namespace OW.Core.Tests;

public sealed class SpaceAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ow-space-" + Guid.NewGuid().ToString("N"));

    public SpaceAnalyzerTests() => Directory.CreateDirectory(_root);

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
    public void Adds_up_folder_sizes_and_sorts_biggest_first()
    {
        Make("big/a.bin", 5000);
        Make("big/inner/b.bin", 3000);
        Make("small/c.txt", 100);
        Make("top.txt", 10);

        var scan = SpaceAnalyzer.Scan(_root, AlwaysExpand());

        Assert.Equal(8110, scan.Root.SizeBytes);
        Assert.Equal(4, scan.Root.FileCount);
        Assert.Equal(["big", "small", "top.txt"], scan.Root.Children.Select(c => c.Name));
        var big = scan.Root.Children[0];
        Assert.True(big.IsFolder);
        Assert.Equal(8000, big.SizeBytes);
        Assert.Equal(Path.Combine(_root, "big"), big.Path);
        Assert.Equal(3000, big.Children.Single(c => c.Name == "inner").SizeBytes);
    }

    [Fact]
    public void Lists_the_largest_files_anywhere()
    {
        Make("x/1.bin", 10);
        Make("y/z/2.bin", 300);
        Make("3.bin", 200);

        var scan = SpaceAnalyzer.Scan(_root, AlwaysExpand() with { LargestFilesCount = 2 });

        Assert.Equal(["2.bin", "3.bin"], scan.LargestFiles.Select(f => f.Name));
        Assert.Equal(Path.Combine(_root, "y", "z", "2.bin"), scan.LargestFiles[0].Path);
    }

    [Fact]
    public void Folds_extra_items_into_one_row_without_losing_bytes()
    {
        for (var i = 0; i < 10; i++)
        {
            Make($"f{i}.bin", 100 + i);
        }

        Make("sub/s.bin", 1);

        var scan = SpaceAnalyzer.Scan(_root, AlwaysExpand() with { MaxChildren = 3 });

        Assert.Equal(4, scan.Root.Children.Count);
        var other = scan.Root.Children[^1];
        Assert.Null(other.Path);
        Assert.Equal("8 mục khác", other.Name);
        Assert.Equal(scan.Root.SizeBytes, scan.Root.Children.Sum(c => c.SizeBytes));
        Assert.Equal(11, scan.Root.Children.Sum(c => c.FileCount));
    }

    [Fact]
    public void Small_folders_are_not_opened_up()
    {
        Make("big/a.bin", 10_000);
        Make("tiny/b.bin", 10);
        Make("tiny/c.bin", 10);

        var scan = SpaceAnalyzer.Scan(_root, new SpaceScanOptions { ExpandShare = 0.5, ExpandMinBytes = 0 });

        var tiny = scan.Root.Children.Single(c => c.Name == "tiny");
        Assert.Equal(20, tiny.SizeBytes);
        Assert.Equal(2, tiny.FileCount);
        Assert.Empty(tiny.Children);
        Assert.NotEmpty(scan.Root.Children.Single(c => c.Name == "big").Children);
    }

    [Fact]
    public void Stops_listing_at_the_item_cap()
    {
        for (var i = 0; i < 5; i++)
        {
            Make($"d{i}/a.bin", 100);
        }

        var scan = SpaceAnalyzer.Scan(_root, AlwaysExpand() with { MaxNodes = 3 });

        Assert.Equal(500, scan.Root.SizeBytes);
        Assert.Equal(500, scan.Root.Children.Sum(c => c.SizeBytes));
        Assert.All(scan.Root.Children.Where(c => c.IsFolder), c => Assert.Empty(c.Children));
    }

    [Fact]
    public void Does_not_follow_folder_links()
    {
        var outside = Path.Combine(Path.GetTempPath(), "ow-space-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            File.WriteAllBytes(Path.Combine(outside, "huge.bin"), new byte[4000]);
            Make("real.bin", 10);
            try
            {
                Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return; // Creating symlinks needs extra rights on some Windows machines.
            }

            var scan = SpaceAnalyzer.Scan(_root, AlwaysExpand());

            Assert.Equal(10, scan.Root.SizeBytes);
            Assert.Equal(1, scan.SkippedLinks);
            Assert.DoesNotContain(scan.LargestFiles, f => f.Name == "huge.bin");
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Never_changes_anything()
    {
        Make("a/b.bin", 50);
        var before = Snapshot();

        SpaceAnalyzer.Scan(_root, AlwaysExpand());

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Can_be_cancelled()
    {
        Make("a/b.bin", 1);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SpaceAnalyzer.Scan(_root, null, cts.Token));
    }

    [Fact]
    public void Missing_folder_is_reported()
    {
        Assert.Throws<DirectoryNotFoundException>(() => SpaceAnalyzer.Scan(Path.Combine(_root, "nope")));
    }

    private static SpaceScanOptions AlwaysExpand() => new() { ExpandShare = 0, ExpandMinBytes = 0 };

    private void Make(string relative, int bytes)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private string Snapshot() => string.Join(
        "\n",
        Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(p => $"{p}|{(File.Exists(p) ? new FileInfo(p).Length : -1)}|{File.GetLastWriteTimeUtc(p):O}"));
}
