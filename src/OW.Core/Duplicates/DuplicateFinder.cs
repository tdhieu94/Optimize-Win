using System.Security.Cryptography;

namespace OW.Core.Duplicates;

/// <summary>One copy of a duplicated file, as seen during the scan.</summary>
public sealed record DuplicateFile(string Path, long SizeBytes, DateTime LastWriteUtc);

/// <summary>Files with exactly the same content (same size and same SHA-256).</summary>
public sealed record DuplicateGroup(string Sha256, long SizeBytes, IReadOnlyList<DuplicateFile> Files)
{
    /// <summary>Space that would come back if only one copy were kept.</summary>
    public long WastedBytes => SizeBytes * (Files.Count - 1);
}

public sealed record DuplicateScanResult(IReadOnlyList<DuplicateGroup> Groups, long FilesSeen, long SkippedProgramFolders, TimeSpan Duration);

public sealed record DuplicateScanOptions
{
    /// <summary>Smaller files are ignored: they free little space and are often parts of programs.</summary>
    public long MinSizeBytes { get; init; } = 1024 * 1024;

    /// <summary>Only these kinds of files are compared: photos, videos, music, documents and archives.</summary>
    public IReadOnlySet<string> Extensions { get; init; } = DuplicateFinder.PersonalFileExtensions;

    /// <summary>Folders with these names (anywhere) are never entered.</summary>
    public IReadOnlySet<string> SkippedFolderNames { get; init; } = DuplicateFinder.DefaultSkippedFolderNames;

    /// <summary>Full folder paths that are never entered (Windows, Program Files, AppData of each profile...).</summary>
    public IReadOnlyList<string> SkippedFolders { get; init; } = [];

    public int MaxGroups { get; init; } = 2000;
}

/// <summary>Thread-safe progress counters that the UI polls while a scan runs.</summary>
public sealed class DuplicateScanProgress
{
    private long _files;
    private long _hashedBytes;
    private volatile string _stage = "Đang liệt kê file";
    private volatile string _current = string.Empty;

    public long Files => Interlocked.Read(ref _files);

    public long HashedBytes => Interlocked.Read(ref _hashedBytes);

    public string Stage => _stage;

    public string Current => _current;

    internal void AddFile() => Interlocked.Increment(ref _files);

    internal void AddHashed(long bytes) => Interlocked.Add(ref _hashedBytes, bytes);

    internal void Set(string stage, string current)
    {
        _stage = stage;
        _current = current;
    }
}

/// <summary>
/// Read-only search for duplicate personal files. Files are grouped by size, then by a hash of their
/// first and last 64 KB, and only then fully hashed with SHA-256, so most files are never read in full.
/// </summary>
/// <remarks>
/// Safety: it never follows links, never reads cloud-only files (that would download them), skips system
/// and hidden files, and skips any folder that holds a program (.exe/.dll) together with everything inside it,
/// because copies inside a program or game folder are usually needed by that program.
/// </remarks>
public static class DuplicateFinder
{
    private const int EdgeBytes = 64 * 1024;

    // Cloud placeholders (OneDrive "online-only"): reading them would download the whole file.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;

    public static readonly IReadOnlySet<string> PersonalFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Photos
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".heic", ".heif", ".webp", ".tif", ".tiff", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".raf", ".orf", ".psd",
        // Videos
        ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".m4v", ".mts", ".m2ts", ".3gp", ".flv", ".webm", ".mpg", ".mpeg",
        // Music
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".wma", ".ogg",
        // Documents
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".txt", ".rtf", ".epub",
        // Archives and disc images
        ".zip", ".rar", ".7z", ".iso",
    };

    public static readonly IReadOnlySet<string> DefaultSkippedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "AppData", "$Recycle.Bin", "System Volume Information",
        "Recovery", "$WINDOWS.~BT", "$WinREAgent", "Windows.old", "OptimizeWin.Quarantine",
        ".git", "node_modules", "steamapps", "SteamLibrary", "Epic Games",
    };

    private static readonly string[] ProgramExtensions = [".exe", ".dll"];

    public static DuplicateScanResult Find(IEnumerable<string> roots, DuplicateScanOptions? options = null, CancellationToken cancellationToken = default, DuplicateScanProgress? progress = null)
    {
        var o = options ?? new DuplicateScanOptions();
        progress ??= new DuplicateScanProgress();
        var started = DateTime.UtcNow;

        var bySize = new Dictionary<long, List<FileInfo>>();
        long seen = 0, programFolders = 0;
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var f in Enumerate(root, o, cancellationToken, progress, ref programFolders))
            {
                seen++;
                if (!bySize.TryGetValue(f.Length, out var list))
                {
                    bySize[f.Length] = list = [];
                }

                list.Add(f);
            }
        }

        var groups = new List<DuplicateGroup>();
        foreach (var (size, sameSize) in bySize.Where(kv => kv.Value.Count > 1).OrderByDescending(kv => kv.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Cheap pass: first and last 64 KB. Only files that still match are read in full.
            foreach (var (_, candidates) in GroupBy(sameSize, f => EdgeHash(f, progress), cancellationToken).Where(g => g.Files.Count > 1))
            {
                foreach (var (hash, same) in GroupBy(candidates, f => FullHash(f, progress, cancellationToken), cancellationToken).Where(g => g.Files.Count > 1))
                {
                    groups.Add(new DuplicateGroup(
                        hash,
                        size,
                        same.OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.FullName.Length).Select(f => new DuplicateFile(f.FullName, f.Length, f.LastWriteTimeUtc)).ToList()));
                }
            }
        }

        var top = groups.OrderByDescending(g => g.WastedBytes).Take(o.MaxGroups).ToList();
        return new DuplicateScanResult(top, seen, programFolders, DateTime.UtcNow - started);
    }

    /// <summary>SHA-256 of a whole file, lowercase hex. Null if it cannot be read.</summary>
    public static string? Sha256Of(string path, CancellationToken cancellationToken = default) =>
        FullHash(new FileInfo(path), null, cancellationToken);

    /// <summary>True if the file is only a cloud placeholder or otherwise should not be read.</summary>
    public static bool IsCloudOnly(FileAttributes a) => (a & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0;

    private static IEnumerable<FileInfo> Enumerate(string root, DuplicateScanOptions o, CancellationToken ct, DuplicateScanProgress progress, ref long programFolders)
    {
        var result = new List<FileInfo>();
        DirectoryInfo rootInfo;
        try
        {
            rootInfo = new DirectoryInfo(root);
            if (!rootInfo.Exists || (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return result;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return result;
        }

        var skipped = o.SkippedFolders.Select(s => s.TrimEnd('\\', '/')).ToList();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootInfo);
        var enumOptions = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 };

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            progress.Set("Đang liệt kê file", dir.FullName);

            List<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos("*", enumOptions).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // A folder holding a program: leave it and everything under it alone.
            if (dir != rootInfo && entries.Any(e => e is FileInfo && ProgramExtensions.Contains(e.Extension, StringComparer.OrdinalIgnoreCase)))
            {
                programFolders++;
                continue;
            }

            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (e is DirectoryInfo sub)
                {
                    if ((sub.Attributes & (FileAttributes.System | FileAttributes.Hidden)) == 0
                        && !o.SkippedFolderNames.Contains(sub.Name) && !skipped.Any(s => string.Equals(s, sub.FullName.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)))
                    {
                        pending.Push(sub);
                    }

                    continue;
                }

                if (e is not FileInfo f
                    || (f.Attributes & (FileAttributes.System | FileAttributes.Hidden)) != 0
                    || IsCloudOnly(f.Attributes)
                    || f.Length < o.MinSizeBytes
                    || !o.Extensions.Contains(f.Extension))
                {
                    continue;
                }

                progress.AddFile();
                result.Add(f);
            }
        }

        return result;
    }

    private static List<(string Key, List<FileInfo> Files)> GroupBy(List<FileInfo> files, Func<FileInfo, string?> key, CancellationToken ct)
    {
        var map = new Dictionary<string, List<FileInfo>>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (key(f) is not { } k)
            {
                continue; // Unreadable: never report it as a duplicate.
            }

            if (!map.TryGetValue(k, out var list))
            {
                map[k] = list = [];
            }

            list.Add(f);
        }

        return map.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    private static string? EdgeHash(FileInfo f, DuplicateScanProgress? progress)
    {
        try
        {
            progress?.Set("Đang so sánh file", f.FullName);
            using var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan);
            var head = new byte[(int)Math.Min(EdgeBytes, stream.Length)];
            stream.ReadExactly(head);
            var tail = Array.Empty<byte>();
            if (stream.Length > EdgeBytes)
            {
                tail = new byte[(int)Math.Min(EdgeBytes, stream.Length - EdgeBytes)];
                stream.Seek(-tail.Length, SeekOrigin.End);
                stream.ReadExactly(tail);
            }

            progress?.AddHashed(head.Length + tail.Length);
            return Convert.ToHexStringLower(SHA256.HashData([.. head, .. tail]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FullHash(FileInfo f, DuplicateScanProgress? progress, CancellationToken ct)
    {
        try
        {
            progress?.Set("Đang kiểm tra nội dung", f.FullName);
            using var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.AppendData(buffer, 0, read);
                progress?.AddHashed(read);
            }

            return Convert.ToHexStringLower(sha.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
