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

    public const string BrowserCacheId = "browser-cache";
    public const string ShaderCacheId = "shader-cache";
    public const string ErrorReportsId = "error-reports";
    public const string WindowsTempId = "windows-temp";
    public const string WindowsUpdateId = "windows-update-download";

    /// <summary>Browser profiles whose cache is cleaned, relative to %LOCALAPPDATA% (Chromium "User Data" folders).</summary>
    private static readonly string[] ChromiumUserData =
    [
        Path.Combine("Microsoft", "Edge", "User Data"),
        Path.Combine("Google", "Chrome", "User Data"),
        Path.Combine("BraveSoftware", "Brave-Browser", "User Data"),
        Path.Combine("CocCoc", "Browser", "User Data"),
    ];

    /// <summary>Cache folders inside one Chromium profile. Only caches: never cookies, history, passwords or settings.</summary>
    private static readonly string[] ChromiumCacheFolders =
    [
        Path.Combine("Cache", "Cache_Data"),
        "Code Cache",
        "GPUCache",
    ];

    /// <summary>Saved web pages and pictures of Edge, Chrome, Brave, Cốc Cốc and Firefox. Browsers download them again when needed.</summary>
    public static JunkCategory BrowserCache(string localAppData, DateTime nowUtc, JunkScanOptions? options = null) =>
        FromFolders(
            BrowserCacheId,
            "Bộ nhớ đệm trình duyệt",
            "Ảnh và trang web mà Edge, Chrome, Cốc Cốc, Brave, Firefox lưu tạm để mở nhanh hơn. Không đụng tới mật khẩu, lịch sử, dấu trang hay việc đăng nhập. Nên tắt trình duyệt trước khi dọn để dọn được nhiều hơn.",
            BrowserCacheFolders(localAppData),
            nowUtc,
            options ?? new JunkScanOptions());

    /// <summary>Shader caches of DirectX and the graphics drivers. They are rebuilt automatically.</summary>
    public static JunkCategory ShaderCache(string localAppData, DateTime nowUtc, JunkScanOptions? options = null) =>
        FromFolders(
            ShaderCacheId,
            "Bộ nhớ đệm đồ họa",
            "File mà card đồ họa (NVIDIA, AMD, DirectX) lưu tạm để vẽ hình nhanh hơn. Sẽ tự tạo lại; lần đầu mở game sau khi dọn có thể chậm hơn một chút.",
            ShaderCacheFolders(localAppData),
            nowUtc,
            options ?? new JunkScanOptions());

    /// <summary>Windows Error Reporting archives and queues, both the user's and the machine's.</summary>
    public static JunkCategory ErrorReports(string localAppData, string programData, DateTime nowUtc, JunkScanOptions? options = null) =>
        FromFolders(
            ErrorReportsId,
            "Báo cáo lỗi của Windows",
            "Báo cáo mà Windows tạo ra khi có chương trình bị lỗi, để gửi cho Microsoft. Không cần cho máy chạy.",
            ErrorReportFolders(localAppData, programData),
            nowUtc,
            options ?? new JunkScanOptions());

    /// <summary>The machine's temporary folder (C:\Windows\Temp). Only files older than 7 days.</summary>
    public static JunkCategory WindowsTemp(string windowsDirectory, DateTime nowUtc, JunkScanOptions? options = null) =>
        FromFolders(
            WindowsTempId,
            "File tạm của Windows",
            "File tạm mà Windows và các bộ cài để lại trong C:\\Windows\\Temp. Chỉ lấy file cũ hơn 7 ngày và không bị dùng.",
            [Path.Combine(windowsDirectory, "Temp")],
            nowUtc,
            AtLeast(options, TimeSpan.FromDays(7)));

    /// <summary>Update files Windows Update already downloaded (C:\Windows\SoftwareDistribution\Download). Only files older than 10 days.</summary>
    public static JunkCategory WindowsUpdateDownloads(string windowsDirectory, DateTime nowUtc, JunkScanOptions? options = null) =>
        FromFolders(
            WindowsUpdateId,
            "File tải về của Windows Update",
            "Bản cập nhật Windows đã tải về và cài xong từ lâu. Chỉ lấy file cũ hơn 10 ngày. Nếu Windows còn cần, nó sẽ tự tải lại.",
            [Path.Combine(windowsDirectory, "SoftwareDistribution", "Download")],
            nowUtc,
            AtLeast(options, TimeSpan.FromDays(10)));

    public static IReadOnlyList<string> BrowserCacheFolders(string localAppData)
    {
        var result = new List<string>();
        foreach (var userData in ChromiumUserData.Select(r => Path.Combine(localAppData, r)))
        {
            foreach (var profile in SubFolders(userData).Where(IsChromiumProfile))
            {
                result.AddRange(ChromiumCacheFolders.Select(c => Path.Combine(profile, c)));
            }
        }

        foreach (var profile in SubFolders(Path.Combine(localAppData, "Mozilla", "Firefox", "Profiles")))
        {
            result.Add(Path.Combine(profile, "cache2"));
        }

        return result;
    }

    public static IReadOnlyList<string> ShaderCacheFolders(string localAppData) =>
    [
        Path.Combine(localAppData, "D3DSCache"),
        Path.Combine(localAppData, "NVIDIA", "DXCache"),
        Path.Combine(localAppData, "NVIDIA", "GLCache"),
        Path.Combine(localAppData, "AMD", "DxCache"),
        Path.Combine(localAppData, "AMD", "GLCache"),
    ];

    public static IReadOnlyList<string> ErrorReportFolders(string localAppData, string programData) =>
    [
        Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportArchive"),
        Path.Combine(localAppData, "Microsoft", "Windows", "WER", "ReportQueue"),
        Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
        Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"),
    ];

    /// <summary>"Default", "Profile 1", "Profile 2"... are browser profiles; "System Profile" and "Guest Profile" have no user cache worth cleaning.</summary>
    internal static bool IsChromiumProfile(string folder)
    {
        var name = Path.GetFileName(folder);
        return name == "Default" || (name.StartsWith("Profile ", StringComparison.Ordinal) && name.Length > 8 && name[8..].All(char.IsAsciiDigit));
    }

    /// <summary>Old, unused files of several folders, as one category. The file cap counts for the whole category.</summary>
    public static JunkCategory FromFolders(string id, string title, string description, IEnumerable<string> folders, DateTime nowUtc, JunkScanOptions options)
    {
        var files = new List<QuarantineFileAction>();
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (files.Count >= options.MaxFiles)
            {
                break;
            }

            files.AddRange(OldFiles(folder, nowUtc, options with { MaxFiles = options.MaxFiles - files.Count }));
        }

        return new JunkCategory(id, title, description, files);
    }

    private static JunkScanOptions AtLeast(JunkScanOptions? options, TimeSpan minAge)
    {
        var o = options ?? new JunkScanOptions();
        return o.MinAge >= minAge ? o : o with { MinAge = minAge };
    }

    private static IEnumerable<string> SubFolders(string folder)
    {
        try
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists || (dir.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return [];
            }

            return dir.EnumerateDirectories()
                .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

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
