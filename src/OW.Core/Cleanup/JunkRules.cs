using OW.Core.Execution;

namespace OW.Core.Cleanup;

/// <summary>A group of junk files the user can choose to clean, e.g. "temporary files".</summary>
public sealed record JunkCategory(string Id, string Title, string Description, IReadOnlyList<QuarantineFileAction> Files)
{
    public long Bytes => Files.Sum(f => f.ExpectedSizeBytes);
}

public sealed record JunkScanOptions
{
    /// <summary>Files changed or created more recently than this are left alone (a program may still need them).</summary>
    public TimeSpan MinAge { get; init; } = TimeSpan.FromDays(2);

    /// <summary>Safety cap on how many files one category may propose.</summary>
    public int MaxFiles { get; init; } = 50_000;

    /// <summary>Returns true if the file is open in another program. Such files are skipped.</summary>
    public Func<string, bool> IsInUse { get; init; } = FileInUse;

    /// <summary>Default check: a file another program has open cannot be opened exclusively.</summary>
    public static bool FileInUse(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}

/// <summary>
/// Conservative cleanup rules. They only look inside folders that hold throw-away files and
/// only propose files that are old and not in use. They never delete: they build a plan that
/// <see cref="SafeExecutor"/> carries out by moving files into quarantine (undoable for 7 days).
/// </summary>
public static class JunkRules
{
    public const string UserTempId = "user-temp";
    public const string CrashDumpsId = "crash-dumps";

    /// <summary>The user's temporary folder (%LOCALAPPDATA%\Temp).</summary>
    public static JunkCategory UserTemp(string tempDirectory, DateTime nowUtc, JunkScanOptions? options = null) =>
        new(
            UserTempId,
            "File tạm của bạn",
            "File tạm mà các chương trình tạo ra rồi bỏ lại (thư mục Temp). Chỉ lấy file cũ hơn 2 ngày và không có chương trình nào đang mở.",
            OldFiles(tempDirectory, nowUtc, options ?? new JunkScanOptions()));

    /// <summary>Crash dumps left by programs that closed unexpectedly (%LOCALAPPDATA%\CrashDumps).</summary>
    public static JunkCategory CrashDumps(string crashDumpDirectory, DateTime nowUtc, JunkScanOptions? options = null) =>
        new(
            CrashDumpsId,
            "Báo cáo lỗi của chương trình",
            "File ghi lại lúc một chương trình bị tắt đột ngột (CrashDumps). Chỉ dùng khi gửi cho nhà sản xuất để sửa lỗi.",
            OldFiles(crashDumpDirectory, nowUtc, options ?? new JunkScanOptions()));

    internal static IReadOnlyList<QuarantineFileAction> OldFiles(string root, DateTime nowUtc, JunkScanOptions o)
    {
        var result = new List<QuarantineFileAction>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return result;
        }

        var rootInfo = new DirectoryInfo(root);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            return result; // Never follow a link, not even at the top.
        }

        var cutoff = nowUtc - o.MinAge;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootInfo);
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 };

        while (pending.Count > 0 && result.Count < o.MaxFiles)
        {
            var dir = pending.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
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
                    pending.Push(sub);
                    continue;
                }

                if (e is not FileInfo f)
                {
                    continue;
                }

                if ((f.Attributes & FileAttributes.System) != 0)
                {
                    continue;
                }

                // Copying a file keeps its old "modified" date, so a fresh copy must also be old by creation date.
                if (f.LastWriteTimeUtc > cutoff || f.CreationTimeUtc > cutoff)
                {
                    continue;
                }

                if (o.IsInUse(f.FullName))
                {
                    continue;
                }

                result.Add(new QuarantineFileAction(f.FullName, f.Length, f.LastWriteTimeUtc));
                if (result.Count >= o.MaxFiles)
                {
                    break;
                }
            }
        }

        return result;
    }
}
