using System.Globalization;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OW.Client;

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
        StatusBar.Severity = InfoBarSeverity.Informational;
        StatusBar.Title = "Đang kiểm tra dịch vụ OW...";
        StatusBar.Message = string.Empty;

        try
        {
            await using var client = await OwServiceClient.ConnectAsync(TimeSpan.FromSeconds(3));
            var info = await client.Service.GetInfoAsync();
            var history = await client.Service.GetHistoryAsync(20);

            StatusBar.Severity = InfoBarSeverity.Success;
            StatusBar.Title = "Dịch vụ OW đang chạy";
            StatusBar.Message = $"Phiên bản {info.Version}. File bị dọn sẽ được giữ {info.QuarantineRetentionDays} ngày để bạn khôi phục nếu cần.";

            if (history.Count == 0)
            {
                HistoryText.Text = "Chưa có lần dọn nào.";
            }
            else
            {
                var sb = new StringBuilder();
                foreach (var b in history)
                {
                    sb.AppendLine(string.Create(
                        CultureInfo.GetCultureInfo("vi-VN"),
                        $"{b.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm} · {b.Title} · {b.Done} file · {b.Bytes / 1048576.0:0.0} MB"));
                }

                HistoryText.Text = sb.ToString();
            }
        }
        catch (TimeoutException)
        {
            StatusBar.Severity = InfoBarSeverity.Warning;
            StatusBar.Title = "Chưa kết nối được dịch vụ OW";
            StatusBar.Message = "Dịch vụ có thể chưa được cài hoặc chưa chạy. Hãy cài lại OW bằng file cài đặt rồi bấm \"Kiểm tra lại\".";
        }
        catch (Exception ex)
        {
            StatusBar.Severity = InfoBarSeverity.Error;
            StatusBar.Title = "Có lỗi khi nói chuyện với dịch vụ OW";
            StatusBar.Message = ex.Message;
        }
        finally
        {
            RetryButton.IsEnabled = true;
        }
    }
}
