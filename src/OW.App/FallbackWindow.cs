using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OW.App;

/// <summary>
/// A plain window built in code, with no XAML file and no theme resources.
/// Used only if the main window fails to load, so the user still sees something useful.
/// </summary>
internal sealed class FallbackWindow : Window
{
    private readonly TextBlock _title = new() { FontSize = 18, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _history = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _retry = new() { Content = "Kiểm tra lại" };
    private readonly StackPanel _disks = new() { Spacing = 12 };
    private readonly SpaceView _space = new();

    public FallbackWindow()
    {
        Title = "Optimize Win";
        _retry.Click += (_, _) => _ = CheckAsync();

        var panel = new StackPanel { Margin = new Thickness(32), Spacing = 12, MaxWidth = 720 };
        panel.Children.Add(new TextBlock { Text = "Optimize Win", FontSize = 28 });
        panel.Children.Add(new TextBlock
        {
            Text = "Giao diện đầy đủ chưa tải được trên máy này nên đang dùng giao diện đơn giản. Lỗi đã được ghi lại để sửa.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });
        panel.Children.Add(_title);
        panel.Children.Add(_message);
        panel.Children.Add(_retry);
        panel.Children.Add(new TextBlock { Text = "Ổ đĩa", FontSize = 18, Margin = new Thickness(0, 16, 0, 0) });
        panel.Children.Add(_disks);
        panel.Children.Add(new TextBlock { Text = "Dung lượng", FontSize = 18, Margin = new Thickness(0, 16, 0, 0) });
        panel.Children.Add(_space.Root);
        panel.Children.Add(new TextBlock { Text = "Lịch sử dọn dẹp", FontSize = 18, Margin = new Thickness(0, 16, 0, 0) });
        panel.Children.Add(_history);
        Content = new ScrollViewer { Content = panel };

        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 640));
        _ = CheckAsync();
    }

    private async Task CheckAsync()
    {
        _retry.IsEnabled = false;
        Show(ServiceStatus.Checking);
        Show(await ServiceStatus.QueryAsync());
        _retry.IsEnabled = true;
    }

    private void Show(ServiceStatus status)
    {
        _title.Text = status.Title;
        _title.Foreground = new SolidColorBrush(status.Level switch
        {
            StatusLevel.Success => Microsoft.UI.Colors.ForestGreen,
            StatusLevel.Warning => Microsoft.UI.Colors.DarkOrange,
            StatusLevel.Error => Microsoft.UI.Colors.Firebrick,
            _ => Microsoft.UI.Colors.SteelBlue,
        });
        _message.Text = status.Message;
        _history.Text = string.IsNullOrEmpty(status.History) ? "Chưa có lần dọn nào." : status.History;
        if (status.Level != StatusLevel.Info)
        {
            DiskCards.Fill(_disks, status.Disks);
            _space.SetDrives(status.Disks);
        }
    }
}
