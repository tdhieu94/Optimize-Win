using System.Security.Cryptography;
using System.Text.Json;

namespace OW.Core.Updates;

/// <summary>One downloadable file of a GitHub release.</summary>
public sealed record ReleaseAsset(string Name, string Url, long Size, string? Digest);

/// <summary>The parts of a GitHub release that the updater needs.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Notes, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// Platform-independent rules for self-update, kept apart from the network code so they can be tested.
/// Updates come only from the releases of OW's own GitHub repository, over HTTPS, and are checked
/// against the SHA-256 that GitHub reports before anything is installed.
/// </summary>
public static class UpdateRules
{
    public const string Owner = "tdhieu94";
    public const string Repo = "Optimize-Win";
    public const long MaxPackageBytes = 400L * 1024 * 1024;

    public static string LatestReleaseApi => $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    /// <summary>Only files under this prefix are ever downloaded.</summary>
    public static string DownloadPrefix => $"https://github.com/{Owner}/{Repo}/releases/download/";

    /// <summary>"v0.5.12" or "0.5.12" → 0.5.12. Anything else → null.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V'))
        {
            t = t[1..];
        }

        var plus = t.IndexOfAny(['+', '-']);
        if (plus >= 0)
        {
            t = t[..plus];
        }

        return Version.TryParse(t, out var v) && v.Build >= 0 ? new Version(v.Major, v.Minor, v.Build) : null;
    }

    public static bool IsNewer(Version current, Version candidate) =>
        new Version(candidate.Major, candidate.Minor, Math.Max(candidate.Build, 0)) >
        new Version(current.Major, current.Minor, Math.Max(current.Build, 0));

    /// <summary>Reads the GitHub "latest release" JSON. Returns null if it is not a usable release.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var version = ParseVersion(tag);
        if (version is null)
        {
            return null;
        }

        var notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? string.Empty : string.Empty;
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                var size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                var digest = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                if (name is not null && url is not null)
                {
                    assets.Add(new ReleaseAsset(name, url, size, digest));
                }
            }
        }

        return new ReleaseInfo(version, tag!, notes, assets);
    }

    /// <summary>The install package of a release: "OptimizeWin-&lt;version&gt;.zip", hosted on this repository only.</summary>
    public static ReleaseAsset? PickPackage(ReleaseInfo release)
    {
        var expected = $"OptimizeWin-{release.Version}.zip";
        var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, expected, StringComparison.OrdinalIgnoreCase));
        if (asset is null || !IsTrustedUrl(asset.Url) || asset.Size <= 0 || asset.Size > MaxPackageBytes)
        {
            return null;
        }

        return asset;
    }

    /// <summary>The ".sha256" file published next to the package (used when GitHub gives no digest).</summary>
    public static ReleaseAsset? PickChecksum(ReleaseInfo release, ReleaseAsset package)
    {
        var asset = release.Assets.FirstOrDefault(a => string.Equals(a.Name, package.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
        return asset is not null && IsTrustedUrl(asset.Url) ? asset : null;
    }

    public static bool IsTrustedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Scheme == Uri.UriSchemeHttps
        && url.StartsWith(DownloadPrefix, StringComparison.Ordinal)
        && !url.Contains("..", StringComparison.Ordinal);

    /// <summary>Accepts "sha256:abc..." (GitHub digest) or a ".sha256" file's text ("abc...  name"). Returns lowercase hex or null.</summary>
    public static string? NormalizeSha256(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        if (t.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
        {
            t = t[7..];
        }

        var first = t.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return first.Length == 64 && first.All(Uri.IsHexDigit) ? first.ToLowerInvariant() : null;
    }

    public static string Sha256Of(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>The unpacked update must look like a full OW install before it is allowed to run.</summary>
    public static string? CheckPackageLayout(string folder)
    {
        string[] required = ["OptimizeWin.exe", Path.Combine("Service", "OptimizeWin.Service.exe"), "install.ps1", "update.ps1"];
        var missing = required.Where(r => !File.Exists(Path.Combine(folder, r))).ToList();
        return missing.Count == 0 ? null : "Gói cập nhật thiếu: " + string.Join(", ", missing);
    }
}
