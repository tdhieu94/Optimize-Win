using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OW.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        TryUseMica();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 640));
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

    private void OnRetryClick(object sender, RoutedEventArgs e) => _ = CheckServiceAsync();

    private async Task CheckServiceAsync()
    {
        RetryButton.IsEnabled = false;
        Show(ServiceStatus.Checking);
        Show(await ServiceStatus.QueryAsync());
        RetryButton.IsEnabled = true;
    }

    private void Show(ServiceStatus status)
    {
        StatusBar.Severity = status.Level switch
        {
            StatusLevel.Success => InfoBarSeverity.Success,
            StatusLevel.Warning => InfoBarSeverity.Warning,
            StatusLevel.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        StatusBar.Title = status.Title;
        StatusBar.Message = status.Message;
        if (!string.IsNullOrEmpty(status.History))
        {
            HistoryText.Text = status.History;
        }

        if (status.Level != StatusLevel.Info)
        {
            DiskCards.Fill(DisksPanel, status.Disks);
        }
    }
}
