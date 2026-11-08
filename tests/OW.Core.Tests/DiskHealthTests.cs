using OW.Core.Disks;

namespace OW.Core.Tests;

public class DiskHealthTests
{
    private static DiskReading Healthy(bool ssd = true) => new("Samsung SSD 980 500GB", ssd, "Healthy", 38, 3, 4200, 0, 0);

    [Fact]
    public void Healthy_disk_is_good()
    {
        var a = DiskHealthEvaluator.Evaluate(Healthy());
        Assert.Equal(HealthVerdict.Good, a.Verdict);
        Assert.Single(a.Reasons);
    }

    [Theory]
    [InlineData(69, HealthVerdict.Good)]
    [InlineData(70, HealthVerdict.Attention)]
    [InlineData(89, HealthVerdict.Attention)]
    [InlineData(90, HealthVerdict.Danger)]
    [InlineData(100, HealthVerdict.Danger)]
    public void Wear_thresholds(int wear, HealthVerdict expected) =>
        Assert.Equal(expected, DiskHealthEvaluator.Evaluate(Healthy() with { WearPercentUsed = wear }).Verdict);

    [Fact]
    public void Windows_unhealthy_wins_over_everything()
    {
        var a = DiskHealthEvaluator.Evaluate(Healthy() with { WindowsHealthStatus = "Unhealthy" });
        Assert.Equal(HealthVerdict.Danger, a.Verdict);
    }

    [Fact]
    public void Windows_warning_needs_attention() =>
        Assert.Equal(HealthVerdict.Attention, DiskHealthEvaluator.Evaluate(Healthy() with { WindowsHealthStatus = "Warning" }).Verdict);

    [Theory]
    [InlineData(true, 69, HealthVerdict.Good)]
    [InlineData(true, 70, HealthVerdict.Attention)]
    [InlineData(false, 54, HealthVerdict.Good)]
    [InlineData(false, 55, HealthVerdict.Attention)]
    public void Temperature_limits_depend_on_disk_type(bool ssd, int temp, HealthVerdict expected) =>
        Assert.Equal(expected, DiskHealthEvaluator.Evaluate(Healthy(ssd) with { TemperatureC = temp }).Verdict);

    [Fact]
    public void Uncorrected_errors_need_attention()
    {
        var a = DiskHealthEvaluator.Evaluate(Healthy() with { ReadErrorsUncorrected = 2, WriteErrorsUncorrected = 1 });
        Assert.Equal(HealthVerdict.Attention, a.Verdict);
        Assert.Contains(a.Reasons, r => r.Contains('3'));
    }

    [Fact]
    public void Several_problems_are_all_listed_and_the_worst_wins()
    {
        var a = DiskHealthEvaluator.Evaluate(Healthy() with { TemperatureC = 75, WearPercentUsed = 95 });
        Assert.Equal(HealthVerdict.Danger, a.Verdict);
        Assert.Equal(2, a.Reasons.Count);
    }

    [Fact]
    public void No_data_means_unknown_not_good()
    {
        var a = DiskHealthEvaluator.Evaluate(new DiskReading("USB disk", false, null, null, null, null, null, null));
        Assert.Equal(HealthVerdict.Unknown, a.Verdict);
    }

    [Fact]
    public void Health_status_alone_is_enough_to_judge()
    {
        var a = DiskHealthEvaluator.Evaluate(new DiskReading("Disk", true, "Healthy", null, null, null, null, null));
        Assert.Equal(HealthVerdict.Good, a.Verdict);
    }
}
