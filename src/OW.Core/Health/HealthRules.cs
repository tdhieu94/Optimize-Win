using System.Globalization;

namespace OW.Core.Health;

public enum AlertLevel
{
    Warning,
    Critical,
}

/// <summary>One drive letter (volume) as the alert rules see it.</summary>
public sealed record VolumeReading(string DriveLetter, long SizeBytes, long FreeBytes);

/// <summary>One physical disk as the alert rules see it. <paramref name="Verdict"/> is "Good", "Attention", "Danger" or "Unknown".</summary>
public sealed record DiskReading(
    string Model,
    string Verdict,
    IReadOnlyList<string> Reasons,
    int? WearPercentUsed,
    int? TemperatureC,
    IReadOnlyList<VolumeReading> Volumes);

/// <summary>Something Hieu should know about. <paramref name="Id"/> is stable so the same alert is not shown again and again.</summary>
public sealed record HealthAlert(string Id, AlertLevel Level, string Title, string Message, string PageKey);

/// <summary>
/// Read-only checks that turn disk readings into plain-Vietnamese warnings. They never change anything:
/// OW only tells, Hieu decides.
/// </summary>
public static class HealthRules
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public static IReadOnlyList<HealthAlert> Evaluate(IEnumerable<DiskReading> disks, string systemDrive = "C:")
    {
        var alerts = new List<HealthAlert>();
        var system = systemDrive.TrimEnd('\\', ':').ToUpperInvariant();

        foreach (var d in disks)
        {
            var key = Slug(d.Model);
            switch (d.Verdict)
            {
                case "Danger":
                    alerts.Add(new HealthAlert(
                        $"disk-danger-{key}",
                        AlertLevel.Critical,
                        $"Ổ {d.Model} có dấu hiệu hỏng",
                        "Hãy sao lưu dữ liệu quan trọng trên ổ này ngay. " + string.Join(" ", d.Reasons),
                        "disks"));
                    break;
                case "Attention":
                    alerts.Add(new HealthAlert(
                        $"disk-attention-{key}",
                        AlertLevel.Warning,
                        $"Ổ {d.Model} cần chú ý",
                        "Nên sao lưu dữ liệu quan trọng. " + string.Join(" ", d.Reasons),
                        "disks"));
                    break;
                default:
                    // Only when the overall verdict did not already say so.
                    if (d.WearPercentUsed is >= 90)
                    {
                        alerts.Add(new HealthAlert($"disk-wear-{key}", AlertLevel.Critical, $"Ổ {d.Model} sắp hết tuổi thọ ghi",
                            $"Đã dùng {d.WearPercentUsed}% tuổi thọ ghi. Hãy sao lưu dữ liệu và tính chuyện thay ổ.", "disks"));
                    }
                    else if (d.WearPercentUsed is >= 75)
                    {
                        alerts.Add(new HealthAlert($"disk-wear-{key}", AlertLevel.Warning, $"Ổ {d.Model} đã dùng nhiều tuổi thọ ghi",
                            $"Đã dùng {d.WearPercentUsed}% tuổi thọ ghi. Ổ vẫn chạy, nhưng nên sao lưu đều đặn.", "disks"));
                    }

                    if (d.TemperatureC is >= 70)
                    {
                        alerts.Add(new HealthAlert($"disk-hot-{key}", AlertLevel.Warning, $"Ổ {d.Model} đang nóng",
                            $"Nhiệt độ {d.TemperatureC}°C. Kiểm tra quạt và chỗ đặt máy cho thoáng.", "disks"));
                    }

                    break;
            }

            foreach (var v in d.Volumes)
            {
                if (v.SizeBytes <= 0)
                {
                    continue;
                }

                var letter = v.DriveLetter.TrimEnd('\\', ':').ToUpperInvariant();
                var isSystem = letter == system;
                var freeShare = (double)v.FreeBytes / v.SizeBytes;
                var free = string.Create(Vi, $"{v.FreeBytes / (double)Gb:0.#} GB");
                var total = string.Create(Vi, $"{v.SizeBytes / (double)Gb:0.#} GB");

                if (freeShare < 0.05 || (isSystem && v.FreeBytes < 5 * Gb))
                {
                    alerts.Add(new HealthAlert($"space-{letter}", AlertLevel.Critical, $"Ổ {letter}: gần hết chỗ",
                        $"Chỉ còn trống {free} trên {total}." + (isSystem ? " Windows có thể chạy chậm hoặc không cập nhật được." : string.Empty) + " Vào Dọn rác hoặc Dung lượng để lấy lại chỗ.", "clean"));
                }
                else if (isSystem && (freeShare < 0.10 || v.FreeBytes < 15 * Gb))
                {
                    alerts.Add(new HealthAlert($"space-{letter}", AlertLevel.Warning, $"Ổ {letter}: sắp đầy",
                        $"Còn trống {free} trên {total}. Nên giữ trống ít nhất 10% để Windows chạy mượt. Vào Dọn rác hoặc Dung lượng để lấy lại chỗ.", "clean"));
                }
            }
        }

        return alerts.OrderByDescending(a => a.Level).ThenBy(a => a.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Which alerts to announce now: new ones, ones that got worse, or ones last announced more than <paramref name="repeatAfter"/> ago.
    /// </summary>
    public static IReadOnlyList<HealthAlert> ToAnnounce(IReadOnlyList<HealthAlert> alerts, IReadOnlyDictionary<string, (AlertLevel Level, DateTime AtUtc)> lastShown, DateTime nowUtc, TimeSpan repeatAfter) =>
        alerts.Where(a => !lastShown.TryGetValue(a.Id, out var seen) || a.Level > seen.Level || nowUtc - seen.AtUtc >= repeatAfter).ToList();

    private static string Slug(string model)
    {
        var s = new string(model.Where(char.IsAsciiLetterOrDigit).ToArray());
        return s.Length == 0 ? "unknown" : s;
    }
}
