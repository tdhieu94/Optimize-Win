using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OW.Client;
using OW.Contracts;
using OW.Core.Startup;

namespace OW.App;

/// <summary>
/// "Khởi động cùng Windows" section: lists what starts at sign-in, explains it, and turns items on or off
/// through the service (Windows' own switch, journaled, undoable). Windows' own entries have no button.
/// </summary>
internal sealed class StartupView
{
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };

    public StartupView()
    {
        var refresh = new Button { Content = "Đọc lại danh sách" };
        refresh.Click += (_, _) => _ = LoadAsync();

        Root.Children.Add(new TextBlock
        {
            Text = "Các chương trình tự mở khi bạn đăng nhập Windows. Càng nhiều thì máy khởi động càng chậm. " +
                   "Bấm \"Tắt\" để chương trình không tự mở nữa: OW dùng đúng công tắc của Windows (giống Task Manager), không xóa gì, " +
                   "và bạn bật lại được bất cứ lúc nào. Mục của Windows không có nút tắt.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });
        Root.Children.Add(refresh);
        Root.Children.Add(_summary);
        Root.Children.Add(_result);
        Root.Children.Add(_list);
        _ = LoadAsync();
    }

    public StackPanel Root { get; } = new() { Spacing = 8 };

    /// <summary>Raised after each read with (total, enabled) so the overview page can show a summary.</summary>
    public event Action<int, int>? Counted;

    /// <summary>Raised after a switch was changed, so the history page can refresh.</summary>
    public event Action? Changed;

    private async Task LoadAsync()
    {
        _summary.Text = "Đang đọc...";
        _list.Children.Clear();
        try
        {
            var entries = await Task.Run(StartupReader.Read);
            var on = entries.Count(e => e.IsEnabled);
            _summary.Text = entries.Count == 0
                ? "Không có chương trình nào tự khởi động."
                : $"{entries.Count} chương trình, {on} đang bật, {entries.Count - on} đã tắt.";
            foreach (var e in entries)
            {
                _list.Children.Add(Card(e));
            }

            Counted?.Invoke(entries.Count, on);
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex);
            _summary.Text = $"Không đọc được danh sách: {ex.Message}";
        }
    }

    private async Task ToggleAsync(StartupEntry e, Button button)
    {
        var enable = !e.IsEnabled;
        if (!enable)
        {
            var extra = e.Advice == StartupAdvice.KeepHardware
                ? "\n\nLưu ý: đây là phần mềm đi kèm phần cứng (âm thanh, đồ họa, bàn di chuột...). Tắt đi có thể làm mất một số phím tắt hoặc tính năng của thiết bị."
                : string.Empty;
            var dialog = new ContentDialog
            {
                XamlRoot = button.XamlRoot,
                Title = $"Tắt \"{e.Name}\"?",
                Content = "Lần đăng nhập Windows sau, chương trình này sẽ không tự mở nữa. Bạn vẫn mở nó bằng tay được như bình thường, và bật lại ở đây bất cứ lúc nào." + extra,
                DefaultButton = ContentDialogButton.Close,
                PrimaryButtonText = "Tắt",
                CloseButtonText = "Thôi",
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }

        button.IsEnabled = false;
        try
        {
            await using var client = await OwServiceClient.ConnectAsync(TimeSpan.FromSeconds(3));
            var result = await client.Service.SetStartupEnabledAsync(new StartupChangeRequest(e.IsMachineWide, e.ApprovalGroup, e.ApprovalName, enable));
            _result.Text = result.Message;
            if (result.Changed)
            {
                Changed?.Invoke();
            }
        }
        catch (TimeoutException)
        {
            _result.Text = "Chưa kết nối được dịch vụ OW. Bấm \"Kiểm tra lại\" ở trang Tổng quan rồi thử lại.";
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex);
            _result.Text = $"Không đổi được: {ex.Message}";
        }

        await LoadAsync();
    }

    private Border Card(StartupEntry e)
    {
        var panel = new StackPanel { Spacing = 4 };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(new TextBlock { Text = e.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(Badge(e.IsEnabled ? "Đang bật" : "Đã tắt", e.IsEnabled ? Colors.SteelBlue : Colors.Gray));
        var (adviceText, adviceColor) = e.Advice switch
        {
            StartupAdvice.KeepWindows => ("Của Windows, nên giữ", Colors.ForestGreen),
            StartupAdvice.KeepHardware => ("Phần cứng/driver, nên giữ", Colors.ForestGreen),
            StartupAdvice.Broken => ("Chương trình không còn", Colors.DarkOrange),
            _ => (null, Colors.Transparent),
        };
        if (adviceText is not null)
        {
            header.Children.Add(Badge(adviceText, adviceColor));
        }

        // Windows' own entries can be turned back on if something else turned them off, but never off here.
        if (!string.IsNullOrEmpty(e.ApprovalGroup) && (e.Advice != StartupAdvice.KeepWindows || !e.IsEnabled))
        {
            var toggle = new Button { Content = e.IsEnabled ? "Tắt" : "Bật", VerticalAlignment = VerticalAlignment.Center };
            toggle.Click += (_, _) => _ = ToggleAsync(e, toggle);
            header.Children.Add(toggle);
        }

        panel.Children.Add(header);

        var where = e.Location switch
        {
            StartupLocation.UserRegistry => "Chỉ tài khoản này",
            StartupLocation.MachineRegistry => "Mọi tài khoản",
            StartupLocation.UserStartupFolder => "Thư mục Startup của bạn",
            _ => "Thư mục Startup chung",
        };
        var line = string.IsNullOrEmpty(e.Publisher) ? where : $"{e.Publisher} · {where}";
        panel.Children.Add(new TextBlock { Text = line, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = e.Command,
            FontSize = 12,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        });

        return new Border
        {
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Child = panel,
        };
    }

    private static Border Badge(string text, Windows.UI.Color color) => new()
    {
        Background = new SolidColorBrush(color),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(8, 2, 8, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = new SolidColorBrush(Colors.White), FontSize = 12 },
    };
}
