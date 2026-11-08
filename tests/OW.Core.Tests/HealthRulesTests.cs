using OW.Core.Health;

namespace OW.Core.Tests;

public class HealthRulesTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private static DiskReading Disk(string verdict = "Good", int? wear = 0, int? temp = 40, params VolumeReading[] volumes) =>
        new("Samsung 870 EVO", verdict, ["Lý do."], wear, temp, volumes);

    [Fact]
    public void Healthy_machine_like_hieus_has_no_alerts()
    {
        var alerts = HealthRules.Evaluate(
        [
            Disk(volumes: new VolumeReading("C:", (long)(474.7 * Gb), (long)(54.6 * Gb))),
            Disk(volumes: new VolumeReading("F:", 465 * Gb, 400 * Gb)),
            Disk(wear: null, temp: null, volumes: new VolumeReading("E:", 465 * Gb, 253 * Gb)),
        ]);

        Assert.Empty(alerts);
    }

    [Theory]
    [InlineData(475, 60, null)]
    [InlineData(475, 40, AlertLevel.Warning)]    // 8.4% free
    [InlineData(475, 20, AlertLevel.Critical)]   // 4.2% free
    [InlineData(120, 14, AlertLevel.Warning)]    // 11.7% free but under 15 GB
    [InlineData(60, 4, AlertLevel.Critical)]     // 6.7% free but under 5 GB
    public void System_drive_space(long sizeGb, long freeGb, AlertLevel? expected)
    {
        var alerts = HealthRules.Evaluate([Disk(volumes: new VolumeReading("C:", sizeGb * Gb, freeGb * Gb))]);
        Assert.Equal(expected, alerts.SingleOrDefault()?.Level);
        if (expected is not null)
        {
            Assert.Equal("space-C", alerts[0].Id);
            Assert.Equal("clean", alerts[0].PageKey);
        }
    }

    [Fact]
    public void Data_drives_only_warn_when_almost_full()
    {
        Assert.Empty(HealthRules.Evaluate([Disk(volumes: new VolumeReading("F:", 465 * Gb, 30 * Gb))]));
        Assert.Equal(AlertLevel.Critical, HealthRules.Evaluate([Disk(volumes: new VolumeReading("F:", 465 * Gb, 10 * Gb))]).Single().Level);
    }

    [Theory]
    [InlineData("Danger", AlertLevel.Critical)]
    [InlineData("Attention", AlertLevel.Warning)]
    public void Disk_verdicts_become_alerts(string verdict, AlertLevel level)
    {
        var a = HealthRules.Evaluate([Disk(verdict, wear: 95, temp: 80)]).Single();
        Assert.Equal(level, a.Level);
        Assert.Equal("disks", a.PageKey);
        Assert.Contains("Lý do.", a.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Wear_and_heat_on_a_good_disk()
    {
        Assert.Equal(AlertLevel.Warning, HealthRules.Evaluate([Disk(wear: 80)]).Single().Level);
        Assert.Equal(AlertLevel.Critical, HealthRules.Evaluate([Disk(wear: 92)]).Single().Level);
        Assert.Equal("disk-hot-Samsung870EVO", HealthRules.Evaluate([Disk(temp: 72)]).Single().Id);
    }

    [Fact]
    public void Critical_comes_first()
    {
        var alerts = HealthRules.Evaluate([Disk(temp: 75, volumes: new VolumeReading("C:", 475 * Gb, 2 * Gb))]);
        Assert.Equal(AlertLevel.Critical, alerts[0].Level);
    }

    [Fact]
    public void Announces_new_worse_or_old_alerts_only()
    {
        var a = new HealthAlert("space-C", AlertLevel.Warning, "t", "m", "clean");
        var day = TimeSpan.FromDays(3);

        Assert.Single(HealthRules.ToAnnounce([a], new Dictionary<string, (AlertLevel, DateTime)>(), Now, day));
        Assert.Empty(HealthRules.ToAnnounce([a], new Dictionary<string, (AlertLevel, DateTime)> { ["space-C"] = (AlertLevel.Warning, Now.AddDays(-1)) }, Now, day));
        Assert.Single(HealthRules.ToAnnounce([a], new Dictionary<string, (AlertLevel, DateTime)> { ["space-C"] = (AlertLevel.Warning, Now.AddDays(-4)) }, Now, day));
        Assert.Single(HealthRules.ToAnnounce([a with { Level = AlertLevel.Critical }], new Dictionary<string, (AlertLevel, DateTime)> { ["space-C"] = (AlertLevel.Warning, Now.AddHours(-1)) }, Now, day));
    }
}

public class AlertMemoryTests
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);

    private static HealthAlert A(string id, AlertLevel level = AlertLevel.Warning) => new(id, level, "t", "m", "clean");

    [Fact]
    public void Saves_and_loads_announced_alerts()
    {
        var path = Path.Combine(Path.GetTempPath(), "ow-alerts-" + Guid.NewGuid().ToString("N"), "alerts.json");
        try
        {
            Assert.Empty(AlertMemory.Load(path));
            AlertMemory.Save(path, AlertMemory.Next(new Dictionary<string, (AlertLevel, DateTime)>(), [A("space-C")], [A("space-C")], Now));
            var loaded = AlertMemory.Load(path);
            Assert.Equal((AlertLevel.Warning, Now), loaded["space-C"]);
            Assert.Equal(DateTimeKind.Utc, loaded["space-C"].AtUtc.Kind);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void Broken_file_is_ignored()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{not json");
            Assert.Empty(AlertMemory.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Resolved_alerts_are_forgotten_and_unannounced_keep_their_time()
    {
        var previous = new Dictionary<string, (AlertLevel, DateTime)>
        {
            ["space-C"] = (AlertLevel.Warning, Now.AddDays(-1)),
            ["disk-hot-X"] = (AlertLevel.Warning, Now.AddDays(-2)),
        };

        var next = AlertMemory.Next(previous, [A("space-C"), A("space-F", AlertLevel.Critical)], [A("space-F", AlertLevel.Critical)], Now);

        Assert.Equal(2, next.Count);
        Assert.Contains(("space-C", AlertLevel.Warning, Now.AddDays(-1)), next);
        Assert.Contains(("space-F", AlertLevel.Critical, Now), next);
    }
}
