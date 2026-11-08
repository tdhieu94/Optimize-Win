using System.Globalization;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using OW.Contracts;
using OW.Core.Health;

namespace OW.App;

/// <summary>
/// The quiet check that Windows runs at sign-in and once a week ("OptimizeWin.exe --check").
/// It only reads disk information and, if something needs attention, shows one Windows notification.
/// It never cleans or changes anything.
/// </summary>
internal static class HealthCheck
{
    public const string Argument = "--check";

    /// <summary>The same warning is not repeated more often than this (unless it gets worse).</summary>
    private static readonly TimeSpan RepeatAfter = TimeSpan.FromDays(3);

    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptimizeWin");
    private static readonly string MemoryPath = Path.Combine(Folder, "alerts.json");
    private static readonly string LogPath = Path.Combine(Folder, "check.log");

    public static IReadOnlyList<HealthAlert> Evaluate(IReadOnlyList<DiskInfo> disks)
    {
        var alerts = HealthRules.Evaluate(
            disks.Select(d => new DiskReading(
                d.Model,
                d.Verdict,
                d.Reasons,
                d.WearPercentUsed,
                d.TemperatureC,
                d.Volumes.Select(v => new VolumeReading(v.DriveLetter, v.SizeBytes, v.FreeBytes)).ToList())),
            Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").ToList();

        // Lets CI exercise the notification path on a machine with nothing wrong.
        if (Environment.GetEnvironmentVariable("OW_TEST_ALERT") == "1")
        {
            alerts.Insert(0, new HealthAlert("test-alert", AlertLevel.Critical, "Cảnh báo thử", "Đây là cảnh báo thử của Optimize Win.", "home"));
        }

        return alerts;
    }

    /// <summary>The window showed these alerts to Hieu, so the background check need not announce them again soon.</summary>
    public static void MarkSeen(IReadOnlyList<HealthAlert> alerts)
    {
        try
        {
            var previous = AlertMemory.Load(MemoryPath);
            AlertMemory.Save(MemoryPath, AlertMemory.Next(previous, alerts, alerts, DateTime.UtcNow));
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex);
        }
    }

    /// <summary>Runs the background check. Returns true when the main window should open instead (notifications unavailable).</summary>
    public static async Task<bool> RunAsync()
    {
        var status = await ServiceStatus.QueryAsync();
        if (status.Level != StatusLevel.Success)
        {
            Log($"Service not reachable: {status.Title}");
            return false;
        }

        var now = DateTime.UtcNow;
        var alerts = Evaluate(status.Disks);
        var previous = AlertMemory.Load(MemoryPath);
        var announce = HealthRules.ToAnnounce(alerts, previous, now, RepeatAfter);
        Log($"{status.Disks.Count} disks, {alerts.Count} alerts, {announce.Count} to announce" + (alerts.Count > 0 ? ": " + string.Join(", ", alerts.Select(a => a.Id)) : string.Empty));
        if (announce.Count == 0)
        {
            AlertMemory.Save(MemoryPath, AlertMemory.Next(previous, alerts, [], now));
            return false;
        }

        var shown = TryNotify(announce);
        AlertMemory.Save(MemoryPath, AlertMemory.Next(previous, alerts, announce, now));
        Log(shown ? "Notification shown" : "Notifications unavailable, opening the window");
        return !shown;
    }

    private static bool TryNotify(IReadOnlyList<HealthAlert> announce)
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                Log("Windows says app notifications are not supported here");
                return false;
            }

            AppNotificationManager.Default.Register();
            var first = announce[0];
            var more = announce.Count > 1 ? $" (và {announce.Count - 1} cảnh báo khác)" : string.Empty;
            var builder = new AppNotificationBuilder()
                .AddArgument("page", "home")
                .AddText(first.Title + more)
                .AddText(first.Message)
                .AddButton(new AppNotificationButton("Mở Optimize Win").AddArgument("page", "home"));
            if (announce.Any(a => a.Level == AlertLevel.Critical))
            {
                builder.SetScenario(AppNotificationScenario.Reminder);
            }

            var notification = builder.BuildNotification();
            AppNotificationManager.Default.Show(notification);
            if (notification.Id == 0)
            {
                Log("Windows did not accept the notification (notifications may be turned off for OW)");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log("Notification failed: " + ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 64 * 1024)
            {
                File.Delete(LogPath);
            }

            File.AppendAllText(LogPath, string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
