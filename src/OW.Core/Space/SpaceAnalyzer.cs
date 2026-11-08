using System.Diagnostics;

namespace OW.Core.Space;

/// <summary>One folder or file in a space report. <see cref="Path"/> is null for the "other items" summary row.</summary>
public sealed record SpaceNode(
    string Name,
    string? Path,
    bool IsFolder,
    long SizeBytes,
    long FileCount,
    IReadOnlyList<SpaceNode> Children);

public sealed record SpaceScan(
    SpaceNode Root,
    IReadOnlyList<SpaceNode> LargestFiles,
    long FolderCount,
    long InaccessibleFolders,
    long SkippedLinks,
    TimeSpan Duration);

/// <summary>Live counters a scan updates while it runs, so the UI can show progress. Safe to read from another thread.</summary>
public sealed class SpaceScanProgress
{
    private long _files;
    private long _bytes;
    private long _folders;
    private string _currentFolder = string.Empty;

    public long Files => Interlocked.Read(ref _files);

    public long Bytes => Interlocked.Read(ref _bytes);

    public long Folders => Interlocked.Read(ref _folders);

    public string CurrentFolder => Volatile.Read(ref _currentFolder);

    internal void EnterFolder(string path)
    {
        Interlocked.Increment(ref _folders);
        Volatile.Write(ref _currentFolder, path);
    }

    internal void AddFile(long length)
    {
        Interlocked.Increment(ref _files);
        Interlocked.Add(ref _bytes, length);
    }
}

public sealed record SpaceScanOptions
{
    /// <summary>A folder's contents are listed only if it holds at least this share of the whole scan...</summary>
    public double ExpandShare { get; init; } = 0.002;

    /// <summary>...or at least this many bytes.</summary>
    public long ExpandMinBytes { get; init; } = 200L * 1024 * 1024;

    /// <summary>How many items to list inside one folder; the rest are summed into one row.</summary>
    public int MaxChildren { get; init; } = 25;

    public int MaxDepth { get; init; } = 12;

    /// <summary>Hard cap on listed items so the answer to the UI stays small.</summary>
    public int MaxNodes { get; init; } = 20_000;

    public int LargestFilesCount { get; init; } = 30;
}

/// <summary>
/// Measures how much space each folder uses. Strictly read-only: it only lists folders and reads file sizes.
/// </summary>
/// <remarks>
/// Links (symlinks, junctions) are not followed, so nothing is counted twice and the walk cannot loop.
/// Cloud-only files (OneDrive "online only") are counted as 0 because they use no space on the disk.
/// Folders that cannot be opened are counted and skipped.
/// </remarks>
public static class SpaceAnalyzer
{
    private const FileAttributes CloudOnly = FileAttributes.Offline | (FileAttributes)0x00040000 /* RECALL_ON_OPEN */ | (FileAttributes)0x00400000 /* RECALL_ON_DATA_ACCESS */;

    private static readonly EnumerationOptions ListOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = 0,
    };

    public static SpaceScan Scan(string rootPath, SpaceScanOptions? options = null, CancellationToken cancellationToken = default, SpaceScanProgress? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        options ??= new SpaceScanOptions();
        var stopwatch = Stopwatch.StartNew();
        var root = new DirectoryInfo(rootPath);
        if (!root.Exists)
        {
            throw new DirectoryNotFoundException(rootPath);
        }

        var walker = new Walker(options.LargestFilesCount, options.MaxChildren, progress ?? new SpaceScanProgress(), cancellationToken);
        var tree = walker.Measure(root, isRoot: true);

        var budget = new NodeBudget(options.MaxNodes);
        var threshold = Math.Max(options.ExpandMinBytes, (long)(tree.Size * options.ExpandShare));
        var result = Shape(tree, root.FullName, depth: 0, options, threshold, budget, forceExpand: true);

        var largest = walker.Largest.UnorderedItems
            .Select(x => x.Element)
            .OrderByDescending(f => f.SizeBytes)
            .ToList();

        return new SpaceScan(result, largest, walker.Folders, walker.Inaccessible, walker.Links, stopwatch.Elapsed);
    }

    private static SpaceNode Shape(Measured m, string path, int depth, SpaceScanOptions o, long threshold, NodeBudget budget, bool forceExpand = false)
    {
        if (!m.IsFolder)
        {
            return new SpaceNode(m.Name, path, false, m.Size, 1, []);
        }

        var expand = (forceExpand || m.Size >= threshold) && depth < o.MaxDepth && (m.Items is { Count: > 0 } || m.OtherCount > 0) && budget.Left > 0;
        if (!expand)
        {
            return new SpaceNode(m.Name, path, true, m.Size, m.Files, []);
        }

        var ordered = (m.Items ?? []).OrderByDescending(c => c.Size).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var take = Math.Min(Math.Min(o.MaxChildren, ordered.Count), budget.Left);
        budget.Left -= take;

        var children = new List<SpaceNode>(take + 1);
        foreach (var c in ordered.Take(take))
        {
            children.Add(Shape(c, System.IO.Path.Combine(path, c.Name), depth + 1, o, threshold, budget));
        }

        var rest = ordered.Skip(take).ToList();
        var restItems = rest.Count + m.OtherCount;
        if (restItems > 0)
        {
            children.Add(new SpaceNode(
                $"{restItems} mục khác",
                null,
                false,
                rest.Sum(c => c.Size) + m.OtherSize,
                rest.Sum(c => c.IsFolder ? c.Files : 1) + m.OtherCount,
                []));
        }

        return new SpaceNode(m.Name, path, true, m.Size, m.Files, children);
    }

    private sealed class NodeBudget(int left)
    {
        public int Left { get; set; } = left;
    }

    /// <summary>Full measured tree. Only folders keep their contents; small files are folded into the folder size.</summary>
    /// <remarks>
    /// To keep memory low on drives with millions of files, a folder keeps all its sub-folders but only its
    /// biggest files; the other files are summed in <see cref="OtherSize"/> / <see cref="OtherCount"/>.
    /// </remarks>
    private sealed record Measured(string Name, bool IsFolder, long Size, long Files, List<Measured>? Items, long OtherSize = 0, long OtherCount = 0);

    private sealed class Walker(int largestCount, int keepFiles, SpaceScanProgress progress, CancellationToken token)
    {
        public PriorityQueue<SpaceNode, long> Largest { get; } = new();

        public long Folders { get; private set; }

        public long Inaccessible { get; private set; }

        public long Links { get; private set; }

        public Measured Measure(DirectoryInfo dir, bool isRoot = false)
        {
            token.ThrowIfCancellationRequested();
            Folders++;
            progress.EnterFolder(dir.FullName);
            var name = isRoot ? dir.FullName : dir.Name;

            List<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos("*", ListOptions).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                Inaccessible++;
                return new Measured(name, true, 0, 0, null);
            }

            var items = new List<Measured>();
            var fileItems = new List<Measured>();
            long size = 0, files = 0;
            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0 && e is DirectoryInfo)
                {
                    Links++;
                    continue;
                }

                if (e is DirectoryInfo sub)
                {
                    var child = Measure(sub);
                    items.Add(child);
                    size += child.Size;
                    files += child.Files;
                }
                else if (e is FileInfo f)
                {
                    long length;
                    try
                    {
                        length = (f.Attributes & CloudOnly) != 0 ? 0 : f.Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    size += length;
                    progress.AddFile(length);
                    files++;
                    fileItems.Add(new Measured(f.Name, false, length, 1, null));
                    Track(f, length);
                }
            }

            long otherSize = 0, otherCount = 0;
            if (fileItems.Count > keepFiles)
            {
                fileItems.Sort((a, b) => b.Size.CompareTo(a.Size));
                foreach (var dropped in fileItems.Skip(keepFiles))
                {
                    otherSize += dropped.Size;
                    otherCount++;
                }

                fileItems.RemoveRange(keepFiles, fileItems.Count - keepFiles);
            }

            items.AddRange(fileItems);
            items.TrimExcess();
            return new Measured(name, true, size, files, items, otherSize, otherCount);
        }

        private void Track(FileInfo f, long length)
        {
            if (largestCount <= 0 || length <= 0)
            {
                return;
            }

            if (Largest.Count < largestCount)
            {
                Largest.Enqueue(new SpaceNode(f.Name, f.FullName, false, length, 1, []), length);
            }
            else if (Largest.TryPeek(out _, out var smallest) && length > smallest)
            {
                Largest.DequeueEnqueue(new SpaceNode(f.Name, f.FullName, false, length, 1, []), length);
            }
        }
    }
}
