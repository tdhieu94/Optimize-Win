using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using OW.Contracts;
using OW.Core.Updates;

namespace OW.Service;

/// <summary>
/// Self-update from the releases of OW's own GitHub repository.
/// Download → SHA-256 check → layout check → run update.ps1 (which backs up the current install,
/// runs install.ps1 and rolls back if that fails). Nothing is installed unless every check passes.
/// </summary>
public sealed class UpdateService(ServicePaths paths, ILogger<UpdateService> logger)
{
    private static readonly HttpClient Http = CreateHttp();
    private readonly SemaphoreSlim _one = new(1, 1);
    private ReleaseInfo? _latest;

    public static string CurrentVersionText =>
        typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public async Task<UpdateInfo> CheckAsync(CancellationToken cancellationToken)
    {
        var current = UpdateRules.ParseVersion(CurrentVersionText) ?? new Version(0, 0, 0);
        try
        {
            var json = await Http.GetStringAsync(UpdateRules.LatestReleaseApi, cancellationToken).ConfigureAwait(false);
            var release = UpdateRules.ParseRelease(json);
            if (release is null || UpdateRules.PickPackage(release) is null)
            {
                return new UpdateInfo(current.ToString(), null, false, null, null);
            }

            _latest = release;
            var available = UpdateRules.IsNewer(current, release.Version);
            return new UpdateInfo(current.ToString(), release.Version.ToString(), available, release.Notes, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new UpdateInfo(current.ToString(), null, false, null, "Chưa có bản phát hành nào trên GitHub.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogWarning(ex, "Update check failed");
            return new UpdateInfo(current.ToString(), null, false, null, "Không kiểm tra được bản mới (có thể máy đang không có mạng).");
        }
    }

    public async Task<UpdateStartResult> InstallAsync(string version, CancellationToken cancellationToken)
    {
        if (!await _one.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new UpdateStartResult(false, "Đang cập nhật rồi, vui lòng đợi.");
        }

        try
        {
            var release = _latest;
            var current = UpdateRules.ParseVersion(CurrentVersionText) ?? new Version(0, 0, 0);
            if (release is null || release.Version.ToString() != version || !UpdateRules.IsNewer(current, release.Version))
            {
                return new UpdateStartResult(false, "Thông tin bản mới đã cũ. Hãy bấm \"Kiểm tra bản mới\" lại.");
            }

            var package = UpdateRules.PickPackage(release);
            if (package is null)
            {
                return new UpdateStartResult(false, "Bản phát hành không có gói cài đặt hợp lệ.");
            }

            var expected = UpdateRules.NormalizeSha256(package.Digest);
            if (expected is null && UpdateRules.PickChecksum(release, package) is { } sumAsset)
            {
                expected = UpdateRules.NormalizeSha256(await Http.GetStringAsync(sumAsset.Url, cancellationToken).ConfigureAwait(false));
            }

            if (expected is null)
            {
                return new UpdateStartResult(false, "Bản phát hành không có mã kiểm tra (SHA-256) nên không cài để đảm bảo an toàn.");
            }

            var work = Path.Combine(paths.UpdatesDirectory, release.Version.ToString());
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }

            Directory.CreateDirectory(work);
            var zip = Path.Combine(work, package.Name);
            logger.LogInformation("Downloading update {Version} from {Url}", release.Version, package.Url);
            await DownloadAsync(package.Url, zip, cancellationToken).ConfigureAwait(false);

            var actual = UpdateRules.Sha256Of(zip);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(zip);
                logger.LogWarning("Update {Version} rejected: SHA-256 {Actual} != {Expected}", release.Version, actual, expected);
                return new UpdateStartResult(false, "File tải về không khớp mã kiểm tra nên đã bị bỏ. Không có gì thay đổi trên máy.");
            }

            var unpacked = Path.Combine(work, "package");
            ZipFile.ExtractToDirectory(zip, unpacked); // Refuses entries that would land outside the folder.
            if (UpdateRules.CheckPackageLayout(unpacked) is { } problem)
            {
                return new UpdateStartResult(false, problem);
            }

            var script = Path.Combine(unpacked, "update.ps1");
            var log = Path.Combine(paths.UpdatesDirectory, "update.log");
            var backup = Path.Combine(paths.UpdatesDirectory, "backup");
            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = unpacked,
            };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Backup", backup, "-Log", log, "-Version", release.Version.ToString() })
            {
                psi.ArgumentList.Add(arg);
            }

            logger.LogInformation("Starting update to {Version}", release.Version);
            Process.Start(psi);
            return new UpdateStartResult(true, $"Đang cài bản {release.Version}. Optimize Win sẽ tự đóng rồi mở lại sau khoảng 1 phút.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "Update failed before install");
            return new UpdateStartResult(false, $"Không cập nhật được: {ex.Message}. Không có gì thay đổi trên máy.");
        }
        finally
        {
            _one.Release();
        }
    }

    private static async Task DownloadAsync(string url, string target, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > UpdateRules.MaxPackageBytes)
        {
            throw new IOException("Gói cập nhật quá lớn.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var file = File.Create(target);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > UpdateRules.MaxPackageBytes)
            {
                throw new IOException("Gói cập nhật quá lớn.");
            }

            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OptimizeWin-Updater", CurrentVersionText));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
