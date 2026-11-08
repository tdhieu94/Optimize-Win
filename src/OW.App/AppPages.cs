using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OW.Contracts;

namespace OW.App;

/// <summary>One page of the app: its menu entry and its content.</summary>
internal sealed record AppPage(string Key, string Title, string Glyph, FrameworkElement Content);

/// <summary>
/// All pages of the app, built in code so the main window (NavigationView) and the fallback window
/// (plain buttons) show exactly the same content. Each feature lives on its own page instead of one long scroll.
/// </summary>
internal sealed class AppPages
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    private readonly SpaceView _space = new();
    private readonly StartupView _startup = new();
    private readonly CleanupView _cleanup = new();
    private readonly DuplicatesView _duplicates = new();
    private readonly StackPanel _disks = new() { Spacing = 12 };
    private readonly UpdateView _update = new();
    private bool _checkedForUpdate;

    private readonly TextBlock _statusTitle = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _statusMessage = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly Border _statusDot = new() { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _retry = new() { Content = "Kiểm tra lại" };

    private readonly Tile _diskTile;
    private readonly Tile _startupTile;
    private readonly Tile _historyTile;

    public AppPages()
    {
        _retry.Click += (_, _) => RetryRequested?.Invoke();
        _diskTile = new Tile("", "Sức khỏe ổ đĩa", "Đang đọc...", "Xem ổ đĩa", () => NavigateRequested?.Invoke("disks"));
        var spaceTile = new Tile("", "Dung lượng", "Xem thư mục nào chiếm nhiều chỗ nhất", "Phân tích", () => NavigateRequested?.Invoke("space"));
        _startupTile = new Tile("", "Khởi động cùng Windows", "Đang đọc...", "Xem danh sách", () => NavigateRequested?.Invoke("startup"));
        var cleanTile = new Tile("", "Dọn rác", "Tìm file tạm và file rác an toàn để dọn", "Quét rác", () => NavigateRequested?.Invoke("clean"));
        _historyTile = new Tile("", "Lịch sử & hoàn tác", "Chưa có lần dọn nào", "Xem lịch sử", () => NavigateRequested?.Invoke("history"));

        _duplicates.Removed += () => RetryRequested?.Invoke();
        _startup.Changed += () => RetryRequested?.Invoke();
        var dupTile = new Tile("\uE8C8", "File trùng nhau", "Tìm ảnh, video, tài liệu bị lưu nhiều bản", "Tìm file trùng", () => NavigateRequested?.Invoke("duplicates"));

        _startup.Counted += (total, on) => _startupTile.Value = total == 0
            ? "Không có chương trình nào tự khởi động"
            : $"{on} chương trình đang bật, {total - on} đã tắt";

        Pages =
        [
            new("home", "Tổng quan", "", Home(spaceTile, cleanTile, dupTile)),
            new("disks", "Ổ đĩa", "", Page("Ổ đĩa", "Sức khỏe và dung lượng của từng ổ cứng, đọc từ Windows. Chỉ xem, không thay đổi gì.", _disks)),
            new("space", "Dung lượng", "", Page("Dung lượng", null, _space.Root)),
            new("startup", "Khởi động", "", Page("Khởi động cùng Windows", null, _startup.Root)),
            new("clean", "Dọn rác", "", Page("Dọn rác", null, _cleanup.CleanRoot)),
            new("duplicates", "File trùng", "\uE8C8", Page("File trùng nhau", null, _duplicates.Root)),
            new("history", "Lịch sử", "", Page("Lịch sử dọn dẹp", "Mỗi lần dọn đều được ghi lại. Bấm \"Hoàn tác\" để lấy lại file trong vòng 7 ngày.", _cleanup.HistoryRoot)),
        ];

        _disks.Children.Add(new TextBlock { Text = "Đang đọc thông tin ổ đĩa...", Opacity = 0.7 });
    }

    public IReadOnlyList<AppPage> Pages { get; }

    /// <summary>A tile or link asks to open another page (by key).</summary>
    public event Action<string>? NavigateRequested;

    public event Action? RetryRequested;

    /// <summary>A newer version was found (version text), so the window can show a banner on every page.</summary>
    public event Action<string>? UpdateAvailable
    {
        add => _update.Available += value;
        remove => _update.Available -= value;
    }

    public AppPage Get(string key) => Pages.FirstOrDefault(p => p.Key == key) ?? Pages[0];

    public void Show(ServiceStatus status)
    {
        _statusTitle.Text = status.Title;
        _statusMessage.Text = status.Message;
        _statusDot.Background = new SolidColorBrush(status.Level switch
        {
            StatusLevel.Success => Colors.ForestGreen,
            StatusLevel.Warning => Colors.DarkOrange,
            StatusLevel.Error => Colors.Firebrick,
            _ => Colors.SteelBlue,
        });
        _retry.IsEnabled = status.Level != StatusLevel.Info;

        if (status.Level == StatusLevel.Info)
        {
            return;
        }

        if (status.Level == StatusLevel.Success && !_checkedForUpdate)
        {
            _checkedForUpdate = true;
            _ = _update.CheckAsync(userAsked: false);
        }

        DiskCards.Fill(_disks, status.Disks);
        _space.SetDrives(status.Disks);
        _duplicates.SetDrives(status.Disks);
        _diskTile.Value = DiskSummary(status.Disks);
        _diskTile.Accent = status.Disks.Any(d => d.Verdict == "Danger") ? Colors.Firebrick
            : status.Disks.Any(d => d.Verdict == "Attention") ? Colors.DarkOrange
            : (Windows.UI.Color?)null;

        if (status.Batches is not null)
        {
            _cleanup.ShowHistory(status.Batches);
            var last = status.Batches.FirstOrDefault();
            _historyTile.Value = last is null
                ? "Chưa có lần dọn nào"
                : string.Create(Vi, $"Lần gần nhất: {last.CreatedUtc.ToLocalTime():dd/MM HH:mm}, {last.Done} file");
        }
    }

    private static string DiskSummary(IReadOnlyList<DiskInfo> disks)
    {
        var real = disks.Where(d => d.Number >= 0).ToList();
        if (disks.Count == 0)
        {
            return "Chưa đọc được thông tin ổ đĩa";
        }

        if (real.Count == 0)
        {
            return "Windows không cho biết sức khỏe ổ (máy ảo / Sandbox)";
        }

        var danger = real.Count(d => d.Verdict == "Danger");
        var attention = real.Count(d => d.Verdict == "Attention");
        if (danger > 0)
        {
            return $"{danger} ổ cần sao lưu dữ liệu ngay!";
        }

        if (attention > 0)
        {
            return $"{attention} trên {real.Count} ổ cần chú ý";
        }

        return real.All(d => d.Verdict == "Good") ? $"{real.Count} ổ, tất cả đều tốt" : $"{real.Count} ổ đĩa";
    }

    private FrameworkElement Home(Tile spaceTile, Tile cleanTile, Tile dupTile)
    {
        var status = new Grid { ColumnSpacing = 12 };
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { Spacing = 2 };
        texts.Children.Add(_statusTitle);
        texts.Children.Add(_statusMessage);
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(_retry, 2);
        _retry.VerticalAlignment = VerticalAlignment.Center;
        status.Children.Add(_statusDot);
        status.Children.Add(texts);
        status.Children.Add(_retry);

        var tiles = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = 290,
            ItemHeight = 190,
        };
        foreach (var t in new[] { _diskTile, spaceTile, _startupTile, cleanTile, dupTile, _historyTile })
        {
            tiles.Children.Add(t.Root);
        }

        var panel = new StackPanel { Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = "Tổng quan", FontSize = 28, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(Card(status));
        panel.Children.Add(tiles);
        panel.Children.Add(_update.Root);
        panel.Children.Add(new TextBlock
        {
            Text = "Mọi thao tác dọn đều đi qua vùng cách ly và có thể hoàn tác trong 7 ngày.",
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    private static FrameworkElement Page(string title, string? description, FrameworkElement body)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 28, FontWeight = FontWeights.SemiBold });
        if (description is not null)
        {
            panel.Children.Add(new TextBlock { Text = description, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        }

        panel.Children.Add(body);
        return panel;
    }

    internal static Border Card(UIElement child) => new()
    {
        BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x0C, 0x80, 0x80, 0x80)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16),
        Child = child,
    };

    /// <summary>A clickable summary card on the overview page.</summary>
    private sealed class Tile
    {
        private readonly TextBlock _value = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8, MaxLines = 2 };
        private readonly FontIcon _icon;

        public Tile(string glyph, string title, string value, string action, Action onClick)
        {
            _icon = new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 24, HorizontalAlignment = HorizontalAlignment.Left };
            _value.Text = value;
            var button = new Button { Content = action, Margin = new Thickness(0, 4, 0, 0) };
            button.Click += (_, _) => onClick();

            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(_icon);
            panel.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(_value);
            panel.Children.Add(button);

            var card = Card(panel);
            card.Margin = new Thickness(0, 0, 12, 12);
            Root = card;
        }

        public Border Root { get; }

        public string Value
        {
            set => _value.Text = value;
        }

        public Windows.UI.Color? Accent
        {
            set
            {
                if (value is { } c)
                {
                    _icon.Foreground = new SolidColorBrush(c);
                    _value.Foreground = new SolidColorBrush(c);
                    _value.Opacity = 1;
                }
                else
                {
                    _icon.ClearValue(IconElement.ForegroundProperty);
                    _value.ClearValue(TextBlock.ForegroundProperty);
                    _value.Opacity = 0.8;
                }
            }
        }
    }
}
