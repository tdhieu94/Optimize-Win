using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OW.Contracts;

namespace OW.App;

/// <summary>Builds the "Ổ đĩa" cards in code so the same view works in the main and the fallback window.</summary>
internal static class DiskCards
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public static void Fill(Panel host, IReadOnlyList<DiskInfo> disks)
    {
        host.Children.Clear();
        if (disks.Count == 0)
        {
            host.Children.Add(new TextBlock { Text = "Chưa đọc được thông tin ổ đĩa.", Opacity = 0.7 });
            return;
        }

        foreach (var disk in disks)
        {
            host.Children.Add(Card(disk));
        }
    }

    private static Border Card(DiskInfo d)
    {
        var (verdictText, color) = d.Verdict switch
        {
            "Good" => ("Tốt", Colors.ForestGreen),
            "Attention" => ("Cần chú ý", Colors.DarkOrange),
            "Danger" => ("Nguy hiểm", Colors.Firebrick),
            _ => ("Không rõ", Colors.Gray),
        };

        var panel = new StackPanel { Spacing = 6 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        header.Children.Add(new TextBlock
        {
            Text = $"{d.Model}",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        header.Children.Add(new Border
        {
            Background = new SolidColorBrush(color),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            Child = new TextBlock { Text = verdictText, Foreground = new SolidColorBrush(Colors.White), FontSize = 12 },
        });
        panel.Children.Add(header);

        if (d.Number >= 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Ổ số {d.Number} · {Kind(d)} · {Size(d.SizeBytes)}",
                Opacity = 0.7,
            });
        }

        var facts = new List<string>();
        if (d.TemperatureC is int t)
        {
            facts.Add($"Nhiệt độ {t}°C");
        }

        if (d.WearPercentUsed is int w)
        {
            facts.Add($"Đã dùng {w}% tuổi thọ ghi");
        }

        if (d.PowerOnHours is long h)
        {
            facts.Add(string.Create(Vi, $"Đã chạy {h:N0} giờ"));
        }

        if (facts.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = string.Join(" · ", facts), TextWrapping = TextWrapping.Wrap });
        }

        foreach (var reason in d.Reasons)
        {
            panel.Children.Add(new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(color) });
        }

        foreach (var v in d.Volumes)
        {
            var used = v.SizeBytes - v.FreeBytes;
            var label = string.IsNullOrWhiteSpace(v.Label) ? string.Empty : $" ({v.Label})";
            panel.Children.Add(new TextBlock
            {
                Text = $"{v.DriveLetter}{label} · còn trống {Size(v.FreeBytes)} / {Size(v.SizeBytes)}",
                Margin = new Thickness(0, 6, 0, 0),
            });
            panel.Children.Add(new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = v.SizeBytes > 0 ? 100.0 * used / v.SizeBytes : 0,
                Height = 8,
            });
        }

        return new Border
        {
            BorderBrush = new SolidColorBrush(Gray(0x30)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = panel,
        };
    }

    private static Windows.UI.Color Gray(byte alpha) => Windows.UI.Color.FromArgb(alpha, 0x80, 0x80, 0x80);

    private static string Size(long bytes)
    {
        const double Gb = 1024d * 1024 * 1024;
        return bytes >= 1024 * Gb
            ? string.Create(Vi, $"{bytes / (1024 * Gb):0.##} TB")
            : string.Create(Vi, $"{bytes / Gb:0.#} GB");
    }

    /// <summary>"SSD NVMe", "SSD, cắm qua USB", "Ổ cứng, cắm qua USB" (Windows may not know the type of a USB drive).</summary>
    private static string Kind(DiskInfo d)
    {
        var kind = d.MediaType is "SSD" or "HDD" or "SCM" ? d.MediaType : "Ổ cứng";
        return d.BusType switch
        {
            "USB" => $"{kind}, cắm qua USB",
            "Khác" or "" => kind,
            _ => $"{kind} {d.BusType}",
        };
    }
}
