using Microsoft.Win32;
using OW.Contracts;
using OW.Core.Cleanup;
using OW.Core.Execution;
using OW.Core.Safety;

namespace OW.Service;

/// <summary>
/// Finds and cleans junk for the user on the other end of the pipe. Folders come from that user's profile,
/// never from the UI. Cleaning only ever goes through <see cref="SafeExecutor"/> (quarantine + journal + undo).
/// </summary>
public sealed class JunkService(SafeExecutor executor, TimeProvider time, ILogger<JunkService> logger)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (Guid Id, DateTimeOffset At, IReadOnlyList<JunkCategory> Categories)> _lastScanByUser = [];

    public JunkScanSummary Scan(string? userSid)
    {
        var profile = ProfileDirectory(userSid);
        if (profile is null)
        {
            return new JunkScanSummary(Guid.Empty, [], "Không xác định được tài khoản Windows đang dùng OW nên chưa quét.");
        }

        var local = Path.Combine(profile, "AppData", "Local");
        var now = time.GetUtcNow().UtcDateTime;
        var categories = new List<JunkCategory>
        {
            JunkRules.UserTemp(Path.Combine(local, "Temp"), now),
            JunkRules.CrashDumps(Path.Combine(local, "CrashDumps"), now),
        };

        var id = Guid.NewGuid();
        lock (_gate)
        {
            _lastScanByUser[userSid!] = (id, time.GetUtcNow(), categories);
        }

        logger.LogInformation("Junk scan for {Sid}: {Files} files, {Bytes} bytes", userSid, categories.Sum(c => c.Files.Count), categories.Sum(c => c.Bytes));
        return new JunkScanSummary(
            id,
            categories.Select(c => new JunkCategoryInfo(c.Id, c.Title, c.Description, c.Files.Count, c.Bytes)).ToList(),
            null);
    }

    public CleanSummary Clean(string? userSid, Guid scanId, IReadOnlyList<string> categoryIds)
    {
        IReadOnlyList<JunkCategory> categories;
        lock (_gate)
        {
            if (userSid is null || !_lastScanByUser.TryGetValue(userSid, out var last) || last.Id != scanId)
            {
                return new CleanSummary(Guid.Empty, 0, 0, 0, 0, "Kết quả quét đã cũ. Hãy bấm \"Quét\" lại rồi dọn.");
            }

            if (time.GetUtcNow() - last.At > TimeSpan.FromHours(1))
            {
                return new CleanSummary(Guid.Empty, 0, 0, 0, 0, "Kết quả quét đã quá 1 giờ. Hãy bấm \"Quét\" lại rồi dọn.");
            }

            categories = last.Categories;
            _lastScanByUser.Remove(userSid);
        }

        var chosen = categories.Where(c => categoryIds.Contains(c.Id)).ToList();
        if (chosen.Count == 0)
        {
            return new CleanSummary(Guid.Empty, 0, 0, 0, 0, "Chưa chọn mục nào để dọn.");
        }

        var plan = new ActionPlan(
            "Dọn rác: " + string.Join(", ", chosen.Select(c => c.Title)),
            ActionOrigin.Rule,
            chosen.SelectMany(c => c.Files).ToList());
        var result = executor.Execute(plan);
        logger.LogInformation("Junk clean batch {Batch}: {Done} done, {Skipped} skipped, {Failed} failed", result.BatchId, result.Done, result.Skipped, result.Failed);

        var message = result.Failed > 0
            ? $"Đã dọn {result.Done} file. {result.Failed} file không dọn được (có thể đang được dùng); chúng vẫn ở chỗ cũ."
            : $"Đã dọn {result.Done} file.";
        if (result.Skipped > 0)
        {
            message += $" Bỏ qua {result.Skipped} file vì đã thay đổi hoặc không còn.";
        }

        return new CleanSummary(result.BatchId, result.Done, result.Skipped, result.Failed, result.BytesFreed, message);
    }

    /// <summary>Profile folder of a Windows account, from the registry list Windows keeps of local profiles.</summary>
    private static string? ProfileDirectory(string? userSid)
    {
        if (string.IsNullOrEmpty(userSid) || !OperatingSystem.IsWindows())
        {
            return null;
        }

        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + userSid);
        var path = key?.GetValue("ProfileImagePath") as string;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        path = Environment.ExpandEnvironmentVariables(path);
        return Directory.Exists(path) ? path : null;
    }
}
