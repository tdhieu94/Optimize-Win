using OW.Core.Updates;

namespace OW.Core.Tests;

public class UpdateRulesTests
{
    private const string Url = "https://github.com/tdhieu94/Optimize-Win/releases/download/v0.5.12/";

    [Theory]
    [InlineData("v0.5.12", "0.5.12")]
    [InlineData("0.5.12", "0.5.12")]
    [InlineData("V1.2.3", "1.2.3")]
    [InlineData("0.5.12+abc", "0.5.12")]
    [InlineData("0.5.12.4", "0.5.12")]
    [InlineData("0.5", null)]
    [InlineData("latest", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Parses_versions(string? tag, string? expected) =>
        Assert.Equal(expected, UpdateRules.ParseVersion(tag)?.ToString());

    [Theory]
    [InlineData("0.4.0", "0.5.1", true)]
    [InlineData("0.5.9", "0.5.10", true)]
    [InlineData("0.5.10", "0.5.10", false)]
    [InlineData("0.5.11", "0.5.10", false)]
    [InlineData("1.0.0", "0.9.99", false)]
    public void Only_newer_versions_count(string current, string candidate, bool newer) =>
        Assert.Equal(newer, UpdateRules.IsNewer(Version.Parse(current), Version.Parse(candidate)));

    [Fact]
    public void Reads_a_github_release_and_picks_the_package()
    {
        var json = """
        {
          "tag_name": "v0.5.12", "draft": false, "prerelease": false, "body": "Sửa lỗi",
          "assets": [
            { "name": "OptimizeWin-0.5.12.zip", "browser_download_url": "https://github.com/tdhieu94/Optimize-Win/releases/download/v0.5.12/OptimizeWin-0.5.12.zip", "size": 1000, "digest": "sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789" },
            { "name": "OptimizeWin-0.5.12.zip.sha256", "browser_download_url": "https://github.com/tdhieu94/Optimize-Win/releases/download/v0.5.12/OptimizeWin-0.5.12.zip.sha256", "size": 90 }
          ]
        }
        """;

        var release = UpdateRules.ParseRelease(json)!;
        var package = UpdateRules.PickPackage(release)!;

        Assert.Equal(new Version(0, 5, 12), release.Version);
        Assert.Equal("Sửa lỗi", release.Notes);
        Assert.Equal("OptimizeWin-0.5.12.zip", package.Name);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", UpdateRules.NormalizeSha256(package.Digest));
        Assert.NotNull(UpdateRules.PickChecksum(release, package));
    }

    [Theory]
    [InlineData("""{ "tag_name": "v0.5.12", "draft": true, "assets": [] }""")]
    [InlineData("""{ "tag_name": "v0.5.12", "prerelease": true, "assets": [] }""")]
    [InlineData("""{ "tag_name": "nightly", "assets": [] }""")]
    [InlineData("""[]""")]
    public void Ignores_drafts_prereleases_and_odd_tags(string json) =>
        Assert.Null(UpdateRules.ParseRelease(json));

    [Theory]
    [InlineData("https://evil.example.com/OptimizeWin-0.5.12.zip")]
    [InlineData("http://github.com/tdhieu94/Optimize-Win/releases/download/v0.5.12/OptimizeWin-0.5.12.zip")]
    [InlineData("https://github.com/someone-else/Optimize-Win/releases/download/v0.5.12/OptimizeWin-0.5.12.zip")]
    [InlineData("https://github.com/tdhieu94/Optimize-Win/releases/download/../../x/OptimizeWin-0.5.12.zip")]
    public void Refuses_packages_from_anywhere_else(string url)
    {
        var release = new ReleaseInfo(new Version(0, 5, 12), "v0.5.12", "", [new ReleaseAsset("OptimizeWin-0.5.12.zip", url, 1000, null)]);
        Assert.Null(UpdateRules.PickPackage(release));
    }

    [Fact]
    public void Refuses_wrong_name_empty_or_huge_packages()
    {
        static ReleaseInfo With(string name, long size) =>
            new(new Version(0, 5, 12), "v0.5.12", "", [new ReleaseAsset(name, Url + name, size, null)]);

        Assert.Null(UpdateRules.PickPackage(With("OptimizeWin-0.5.11.zip", 1000)));
        Assert.Null(UpdateRules.PickPackage(With("OptimizeWin-0.5.12.zip", 0)));
        Assert.Null(UpdateRules.PickPackage(With("OptimizeWin-0.5.12.zip", UpdateRules.MaxPackageBytes + 1)));
        Assert.NotNull(UpdateRules.PickPackage(With("OptimizeWin-0.5.12.zip", 1000)));
    }

    [Theory]
    [InlineData("sha256:0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  OptimizeWin-0.5.12.zip\n", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:1234", null)]
    [InlineData("zz23456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", null)]
    [InlineData(null, null)]
    public void Normalizes_checksums(string? text, string? expected) =>
        Assert.Equal(expected, UpdateRules.NormalizeSha256(text));

    [Fact]
    public void Hashes_files_and_checks_package_layout()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ow-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Service"));
        try
        {
            var f = Path.Combine(dir, "a.txt");
            File.WriteAllText(f, "abc");
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", UpdateRules.Sha256Of(f));

            Assert.NotNull(UpdateRules.CheckPackageLayout(dir));
            foreach (var name in new[] { "OptimizeWin.exe", Path.Combine("Service", "OptimizeWin.Service.exe"), "install.ps1", "update.ps1" })
            {
                File.WriteAllText(Path.Combine(dir, name), "x");
            }

            Assert.Null(UpdateRules.CheckPackageLayout(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
