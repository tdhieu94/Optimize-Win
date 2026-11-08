using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OW.App;

public sealed partial class MainWindow : Window
{
    private readonly AppPages _pages = new();

    public MainWindow()
    {
        InitializeComponent();
        TryUseMica();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 780));

        foreach (var page in _pages.Pages)
        {
            Nav.MenuItems.Add(new NavigationViewItem
            {
                Content = page.Title,
                Tag = page.Key,
                Icon = new FontIcon { Glyph = page.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons") },
            });
        }

        _pages.NavigateRequested += Select;
        _pages.RetryRequested += () => _ = CheckServiceAsync();
        _pages.UpdateAvailable += version =>
        {
            UpdateBanner.Message = $"Optimize Win {version} đã sẵn sàng.";
            UpdateBanner.IsOpen = true;
        };
        Select("home");
        _ = CheckServiceAsync();
    }

    /// <summary>
    /// Mica (the translucent Windows 11 background) is not available everywhere, e.g. in Windows Sandbox,
    /// where declaring it in XAML made the window fail to load. Use it only where Windows supports it.
    /// </summary>
    private void TryUseMica()
    {
        try
        {
            if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
            {
                SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            }
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex);
        }
    }

    private void Select(string key)
    {
        var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == key);
        if (item is not null && !ReferenceEquals(Nav.SelectedItem, item))
        {
            Nav.SelectedItem = item; // Raises SelectionChanged, which shows the page.
        }
        else
        {
            ShowPage(key);
        }
    }

    private void OnUpdateBannerClick(object sender, RoutedEventArgs e)
    {
        UpdateBanner.IsOpen = false;
        Select("home");
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string key })
        {
            ShowPage(key);
        }
    }

    private void ShowPage(string key)
    {
        PageHost.Content = _pages.Get(key).Content;
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private async Task CheckServiceAsync()
    {
        Show(ServiceStatus.Checking);
        Show(await ServiceStatus.QueryAsync());
    }

    private void Show(ServiceStatus status)
    {
        _pages.Show(status);
        ServiceBanner.IsOpen = status.Level is StatusLevel.Warning or StatusLevel.Error;
        ServiceBanner.Severity = status.Level == StatusLevel.Error ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        ServiceBanner.Title = status.Title;
        ServiceBanner.Message = status.Message;
    }
}
