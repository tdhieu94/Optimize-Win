using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OW.App;

/// <summary>
/// A plain window built in code, with no XAML file and no NavigationView.
/// Used only if the main window fails to load, so the user still sees every page.
/// </summary>
internal sealed class FallbackWindow : Window
{
    private readonly AppPages _pages = new();
    private readonly ContentControl _host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, MaxWidth = 960, Margin = new Thickness(24) };
    private readonly ScrollViewer _scroller = new();

    public FallbackWindow()
    {
        Title = "Optimize Win";

        var menu = new StackPanel { Spacing = 4, Margin = new Thickness(12), Width = 180 };
        menu.Children.Add(new TextBlock
        {
            Text = "Giao diện đơn giản (giao diện đầy đủ chưa tải được, lỗi đã được ghi lại).",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.7,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
        });
        foreach (var page in _pages.Pages)
        {
            var b = new Button { Content = page.Title, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
            var key = page.Key;
            b.Click += (_, _) => ShowPage(key);
            menu.Children.Add(b);
        }

        _scroller.Content = _host;
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_scroller, 1);
        root.Children.Add(menu);
        root.Children.Add(_scroller);
        Content = root;

        _pages.NavigateRequested += ShowPage;
        _pages.RetryRequested += () => _ = CheckAsync();
        ShowPage("home");

        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        _ = CheckAsync();
    }

    private void ShowPage(string key)
    {
        _host.Content = _pages.Get(key).Content;
        _scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private async Task CheckAsync()
    {
        _pages.Show(ServiceStatus.Checking);
        _pages.Show(await ServiceStatus.QueryAsync());
    }
}
