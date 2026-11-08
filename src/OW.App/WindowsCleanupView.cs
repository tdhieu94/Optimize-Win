using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OW.Client;
using OW.Contracts;

namespace OW.App;

/// <summary>
/// "Dọn sâu Windows" on the cleanup page: asks Windows how much its own files take (WinSxS, Windows.old, restore points)
/// and runs Windows' own component cleanup on request. OW never deletes Windows files itself.
/// </summary>
internal sealed class WindowsCleanupView
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    private readonly Button _analyze = new() { Content = "Phân tích" };
    private readonly Button _clean = new() { Content = "Dọn kho thành phần", IsEnabled = false };
    private readonly ProgressBar _progress = new() { Visibility = Visibility.Collapsed, Minimum = 0, Maximum = 100 };
    private readonly TextBlock _stage = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly StackPanel _report = new() { Spacing = 10 };
    private bool _polling;
    private bool _loaded;

    public WindowsCleanupView()
    {
        _analyze.Click += (_, _) => _ = StartAsync(cleanup: false);
        _clean.Click += (_, _) => _ = ConfirmCleanupAsync();
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var s) && s is Style style)
        {
            _clean.Style = style;
        }

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        bar.Children.Add(_analyze);
        bar.Children.Add(_clean);

        Root.Children.Add(new TextBlock { Text = "Dọn sâu Windows", FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 0) });
        Root.Children.Add(new TextBlock
        {
            Text = "Windows giữ lại bản cũ của chính nó sau mỗi lần cập nhật. Bấm \"Phân tích\" để xem chúng chiếm bao nhiêu chỗ. " +
                   "Khi dọn, OW nhờ công cụ chính thức của Windows (DISM) làm, OW không tự xoá file nào của Windows.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });
        Root.Children.Add(bar);
        Root.Children.Add(_progress);
        Root.Children.Add(_stage);
        Root.Children.Add(_result);
        Root.Children.Add(_report);
        Root.Loaded += (_, _) =>
        {
            if (!_loaded)
            {
                _loaded = true;
                _ = PollAsync();
            }
        };
    }

    public StackPanel Root { get; } = new() { Spacing = 8 };

    private async Task StartAsync(bool cleanup)
    {
        _analyze.IsEnabled = false;
        _clean.IsEnabled = false;
        _result.Text = string.Empty;
        try
        {
            await using var client = await OwServiceClient.ConnectAsync(TimeSpan.FromSeconds(3));
            var started = cleanup ? await client.Service.StartComponentCleanupAsync() : await client.Service.StartWindowsAnalysisAsync();
            if (!started)
            {
                _stage.Text = "Đang có một việc khác chạy, hãy đợi nó xong.";
            }
        }
        catch (TimeoutException)
        {
            _stage.Text = "Chưa kết nối được dịch vụ OW. Bấm \"Kiểm tra lại\" ở trang Tổng quan rồi thử lại.";
            _analyze.IsEnabled = true;
            return;
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex);
            _stage.Text = "Không bắt đầu được: " + ex.Message;
            _analyze.IsEnabled = true;
            return;
        }

        await PollAsync();
    }

    private async Task PollAsync()
    {
        if (_polling)
        {
            return;
        }

        _polling = true;
        try
        {
            while (true)
            {
                WindowsCleanupStatus status;
                try
                {
                    await using var client = await OwServiceClient.ConnectAsync(TimeSpan.FromSeconds(3));
                    status = await client.Service.GetWindowsCleanupStatusAsync();
                }
                catch (Exception ex) when (ex is TimeoutException or IOException)
                {
                    // The service may be restarting; try again shortly while work is running, otherwise stop.
                    if (_progress.Visibility == Visibility.Visible)
                    {
                        await Task.Delay(5000);
                        continue;
                    }

                    _analyze.IsEnabled = true;
                    return;
                }

                Show(status);
                if (!status.Running)
                {
                    return;
                }

                await Task.Delay(2000);
            }
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex);
            _stage.Text = "Không đọc được tiến độ: " + ex.Message;
            _analyze.IsEnabled = true;
        }
        finally
        {
            _polling = false;
        }
    }

    private void Show(WindowsCleanupStatus status)
    {
        _progress.Visibility = status.Running ? Visibility.Visible : Visibility.Collapsed;
        _progress.IsIndeterminate = status.Percent is null;
        _progress.Value = status.Percent ?? 0;
        _stage.Text = status.Running
            ? status.Stage + string.Create(Vi, $" ({TimeSpan.FromSeconds(status.Seconds):mm\\:ss}{(status.Percent is { } p ? $", {p:0}%" : string.Empty)})")
            : string.Empty;
        _result.Text = status.LastResult ?? string.Empty;
        if (status.LastResultOk || status.LastResult is null)
        {
            _result.ClearValue(TextBlock.ForegroundProperty);
        }
        else
        {
            _result.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.DarkOrange);
        }

        _analyze.IsEnabled = !status.Running;
        _analyze.Content = status.Report is null ? "Phân tích" : "Phân tích lại";
        _clean.IsEnabled = !status.Running && status.Report is { ComponentStoreBytes: not null };
        if (status.Report is { } r)
        {
            ShowReport(r);
        }
    }

    private void ShowReport(WindowsCleanupReport r)
    {
        _report.Children.Clear();

        string store;
        if (r.ComponentStoreBytes is { } size)
        {
            store = $"Kích thước thật: {Size(size)}. Phần lớn dùng chung với Windows nên không dọn được.";
            if (r.CleanableBytes is { } cleanable)
            {
                store += $"\nBản sao lưu cũ và bộ nhớ đệm: {Size(cleanable)}. Dọn thường lấy lại được một phần trong số này.";
            }

            store += r.CleanupRecommended switch
            {
                true => "\nWindows khuyên nên dọn.",
                false => "\nWindows cho biết chưa cần dọn (có thể vẫn dọn, nhưng sẽ được ít).",
                null => string.Empty,
            };
            if (r.LastCleanup is { } last)
            {
                store += $"\nLần dọn gần nhất: {last}.";
            }
        }
        else
        {
            store = r.ComponentStoreError ?? "Windows không cho biết kích thước kho thành phần.";
        }

        _report.Children.Add(Card("Kho thành phần Windows (WinSxS)", store, null));

        if (r.WindowsOldBytes is { } old)
        {
            var open = new Button { Content = "Mở Cài đặt lưu trữ của Windows" };
            open.Click += (_, _) => _ = Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:storagesense"));
            _report.Children.Add(Card(
                "Bản Windows cũ (Windows.old)",
                $"Chiếm {Size(old)}. Thư mục này để quay lại bản Windows trước sau khi nâng cấp. Nếu máy đang chạy ổn và bạn không định quay lại, " +
                "hãy xoá nó bằng Cài đặt của Windows: Hệ thống > Lưu trữ > File tạm thời > tích \"Cài đặt Windows trước đây\" > Xoá file.",
                open));
        }
        else
        {
            _report.Children.Add(Card("Bản Windows cũ (Windows.old)", "Không có. Không cần làm gì.", null));
        }

        var points = r.RestorePointCount == 0
            ? "Chưa có điểm khôi phục nào."
            : $"{r.RestorePointCount} điểm khôi phục" + (r.RestorePointBytes is { } used ? $", dùng {Size(used)}" : string.Empty) + ".";
        _report.Children.Add(Card("Điểm khôi phục hệ thống", points + " OW không xoá chúng vì chúng giúp cứu máy khi có sự cố; Windows tự xoá điểm cũ khi cần chỗ.", null));

        _report.Children.Add(new TextBlock
        {
            Text = $"Ổ hệ thống còn trống {Size(r.SystemDriveFreeBytes)} (đo lúc {r.MeasuredUtc.ToLocalTime().ToString("HH:mm dd/MM", Vi)}).",
            Opacity = 0.7,
        });
    }

    private async Task ConfirmCleanupAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = _clean.XamlRoot,
            Title = "Dọn kho thành phần Windows?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "Windows sẽ tự xoá bản cũ của các thành phần đã được cập nhật. Đây là công cụ chính thức của Windows và an toàn với máy.\n\n" +
                       "• Việc này KHÔNG hoàn tác được trong OW (Windows không giữ lại bản cũ nữa). Các bản cập nhật vẫn gỡ được như bình thường.\n" +
                       "• Có thể mất 10–40 phút. Bạn vẫn dùng máy bình thường được, nhưng đừng tắt máy cho đến khi xong.\n" +
                       "• Có thể Windows cần khởi động lại sau khi dọn.",
            },
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonText = "Dọn",
            CloseButtonText = "Thôi",
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await StartAsync(cleanup: true);
        }
    }

    private static Border Card(string title, string text, UIElement? action)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });
        if (action is not null)
        {
            panel.Children.Add(action);
        }

        return AppPages.Card(panel);
    }

    private static string Size(long bytes) => bytes >= 1024L * 1024 * 1024
        ? string.Create(Vi, $"{bytes / 1073741824.0:0.##} GB")
        : string.Create(Vi, $"{bytes / 1048576.0:0} MB");
}
