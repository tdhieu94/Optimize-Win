namespace OW.Core.Disks;

/// <summary>Raw readings for one physical disk, as reported by Windows. Any value may be missing.</summary>
public sealed record DiskReading(
    string Model,
    bool IsSsd,
    string? WindowsHealthStatus,
    int? TemperatureC,
    int? WearPercentUsed,
    long? PowerOnHours,
    long? ReadErrorsUncorrected,
    long? WriteErrorsUncorrected);

public enum HealthVerdict
{
    /// <summary>Not enough data to judge (e.g. a USB enclosure that hides SMART).</summary>
    Unknown,
    Good,
    Attention,
    Danger,
}

public sealed record DiskAssessment(HealthVerdict Verdict, IReadOnlyList<string> Reasons);

/// <summary>
/// Turns raw disk readings into a plain-language verdict. It never guesses: missing data stays "unknown".
/// Thresholds are deliberately conservative so a warning is worth acting on.
/// </summary>
public static class DiskHealthEvaluator
{
    public const int WearAttentionPercent = 70;
    public const int WearDangerPercent = 90;
    public const int SsdHotC = 70;
    public const int HddHotC = 55;

    public static DiskAssessment Evaluate(DiskReading r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var verdict = HealthVerdict.Good;
        var reasons = new List<string>();

        void Raise(HealthVerdict level, string reason)
        {
            if (level > verdict)
            {
                verdict = level;
            }

            reasons.Add(reason);
        }

        switch (r.WindowsHealthStatus)
        {
            case "Unhealthy":
                Raise(HealthVerdict.Danger, "Windows báo ổ này không khỏe. Hãy sao lưu dữ liệu quan trọng ngay.");
                break;
            case "Warning":
                Raise(HealthVerdict.Attention, "Windows đưa ra cảnh báo cho ổ này.");
                break;
        }

        if (r.WearPercentUsed is int wear)
        {
            if (wear >= WearDangerPercent)
            {
                Raise(HealthVerdict.Danger, $"Ổ đã dùng {wear}% tuổi thọ ghi. Nên chuẩn bị thay ổ và sao lưu dữ liệu.");
            }
            else if (wear >= WearAttentionPercent)
            {
                Raise(HealthVerdict.Attention, $"Ổ đã dùng {wear}% tuổi thọ ghi.");
            }
        }

        if (r.TemperatureC is int temp)
        {
            var hot = r.IsSsd ? SsdHotC : HddHotC;
            if (temp >= hot)
            {
                Raise(HealthVerdict.Attention, $"Ổ đang nóng ({temp}°C). Kiểm tra quạt và độ thông thoáng của máy.");
            }
        }

        var uncorrected = (r.ReadErrorsUncorrected ?? 0) + (r.WriteErrorsUncorrected ?? 0);
        if (uncorrected > 0)
        {
            Raise(HealthVerdict.Attention, $"Ổ đã gặp {uncorrected} lỗi đọc/ghi không sửa được. Nên sao lưu dữ liệu quan trọng.");
        }

        var hasAnyData = r.WindowsHealthStatus is "Healthy" or "Warning" or "Unhealthy"
            || r.WearPercentUsed.HasValue
            || r.TemperatureC.HasValue;

        if (!hasAnyData)
        {
            return new DiskAssessment(HealthVerdict.Unknown, ["Windows không cung cấp thông tin sức khỏe cho ổ này (thường gặp với ổ gắn qua USB)."]);
        }

        if (verdict == HealthVerdict.Good)
        {
            reasons.Add("Không thấy dấu hiệu bất thường.");
        }

        return new DiskAssessment(verdict, reasons);
    }
}
