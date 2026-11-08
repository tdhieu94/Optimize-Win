using System.Globalization;
using System.Text.RegularExpressions;

namespace OW.Core.WindowsCleanup;

/// <summary>What <c>DISM /Online /English /Cleanup-Image /AnalyzeComponentStore</c> reported.</summary>
public sealed record ComponentStoreAnalysis(
    long? ActualSizeBytes,
    long? SharedWithWindowsBytes,
    long? BackupsAndDisabledBytes,
    long? CacheAndTemporaryBytes,
    string? LastCleanup,
    int? ReclaimablePackages,
    bool? CleanupRecommended)
{
    /// <summary>The part that cleanup can work on (old backups and cache). Windows usually frees only part of it.</summary>
    public long? CleanableBytes => BackupsAndDisabledBytes is null && CacheAndTemporaryBytes is null
        ? null
        : (BackupsAndDisabledBytes ?? 0) + (CacheAndTemporaryBytes ?? 0);
}

/// <summary>Outcome of Windows' own component cleanup (DISM /StartComponentCleanup).</summary>
/// <param name="FreedBytes">Free space gained on the system drive (measured before and after; other programs can change it too).</param>
public sealed record ComponentCleanupOutcome(bool Success, bool RestartNeeded, long FreedBytes, string Message);

/// <summary>Runs Windows' own component cleanup. Implemented by the service with dism.exe; faked in tests.</summary>
public interface IComponentStoreCleaner
{
    ComponentCleanupOutcome Cleanup(CancellationToken cancellationToken);
}

public static partial class ComponentStore
{
    /// <summary>The only arguments OW ever passes to DISM. No /ResetBase: installed updates can still be uninstalled.</summary>
    public static readonly IReadOnlyList<string> AnalyzeArguments = ["/Online", "/English", "/Cleanup-Image", "/AnalyzeComponentStore"];

    public static readonly IReadOnlyList<string> CleanupArguments = ["/Online", "/English", "/Cleanup-Image", "/StartComponentCleanup"];

    public static ComponentStoreAnalysis Parse(string output)
    {
        string? Value(string label)
        {
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                var colon = line.IndexOf(':');
                if (colon > 0 && string.Equals(line[..colon].Trim(), label, StringComparison.OrdinalIgnoreCase))
                {
                    return line[(colon + 1)..].Trim();
                }
            }

            return null;
        }

        var packages = Value("Number of Reclaimable Packages");
        var recommended = Value("Component Store Cleanup Recommended");
        return new ComponentStoreAnalysis(
            ParseSize(Value("Actual Size of Component Store")),
            ParseSize(Value("Shared with Windows")),
            ParseSize(Value("Backups and Disabled Features")),
            ParseSize(Value("Cache and Temporary Data")),
            Value("Date of Last Cleanup") is { Length: > 0 } last ? last : null,
            int.TryParse(packages, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null,
            recommended is null ? null : recommended.StartsWith("Yes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>"7.80 GB", "200.00 MB", "1,5 GB", "512 bytes" → bytes. Null when unreadable.</summary>
    public static long? ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = SizeRegex().Match(text.Trim());
        if (!m.Success)
        {
            return null;
        }

        var number = m.Groups[1].Value.Replace(',', '.');
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var factor = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            "TB" => 1024d * 1024 * 1024 * 1024,
            _ => 1d,
        };
        return (long)Math.Round(value * factor);
    }

    /// <summary>DISM progress looks like "[====  25.0%   ]"; returns the last percentage in <paramref name="text"/>.</summary>
    public static double? LastPercent(string text)
    {
        var matches = PercentRegex().Matches(text);
        return matches.Count == 0
            ? null
            : double.Parse(matches[^1].Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>Turns DISM's exit code into a plain-Vietnamese result.</summary>
    public static (bool Success, bool RestartNeeded, string Message) Explain(int exitCode, string output)
    {
        const int RestartRequired = 3010;
        const int PendingRestart = unchecked((int)0x800F082F);
        const int PendingRestart2 = unchecked((int)0x800F0806);
        return exitCode switch
        {
            0 => (true, false, "Windows đã dọn xong kho thành phần."),
            RestartRequired => (true, true, "Windows đã dọn xong. Hãy khởi động lại máy để hoàn tất."),
            PendingRestart or PendingRestart2 => (false, true, "Windows đang chờ khởi động lại sau một bản cập nhật. Hãy khởi động lại máy rồi thử lại."),
            _ when output.Contains("pending", StringComparison.OrdinalIgnoreCase) && output.Contains("restart", StringComparison.OrdinalIgnoreCase)
                => (false, true, "Windows đang chờ khởi động lại. Hãy khởi động lại máy rồi thử lại."),
            _ => (false, false, string.Create(CultureInfo.InvariantCulture, $"Windows báo lỗi khi dọn (mã 0x{exitCode:X8}). Không có gì bị hỏng; có thể thử lại sau khi khởi động lại máy.")),
        };
    }

    [GeneratedRegex(@"^([0-9]+(?:[.,][0-9]+)?)\s*(bytes?|KB|MB|GB|TB)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"([0-9]{1,3}(?:\.[0-9])?)%")]
    private static partial Regex PercentRegex();
}
