using System.Diagnostics;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OW.Client;
using OW.Contracts;

namespace OW.App;

/// <summary>
/// "Cập nhật" card: checks GitHub for a newer OW (through the service) and installs it with one click.
/// The service does the download and the SHA-256 check; the app only asks and then reopens itself.
/// </summary>
internal sealed class UpdateView
{
    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _notes = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, MaxLines = 6 };
    private readonly Button _check = new() { Content = "Kiểm tra bản mới" };
    private readonly Button _install = new() { Content = "Cập nhật ngay", Visibility = Visibility.Collapsed };
    private readonly ProgressRing _busy = new() { IsActive = false, Width = 20, Height = 20, Visibility = Visibility.Collapsed };
    private string? _available;

    public UpdateView()
    {
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style s)
        {
            _install.Style = s;
        }

        _check.Click += (_, _) => _ = CheckAsync(userAsked: true);
        _install.Click += (_, _) => _ = InstallAsync();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        buttons.Children.Add(_install);
        buttons.Children.Add(_check);
        buttons.Children.Add(_busy);

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Cập nhật", FontSize = 16, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(_text);
        panel.Children.Add(_notes);
        panel.Children.Add(buttons);
        Root = AppPages.Card(panel);
        _text.Text = "Đang kiểm tra bản mới...";
    }

    public Border Root { get; }

    /// <summary>Raised when a newer version is found, with its version text.</summary>
    public event Action<string>? Available;

    public async Task CheckAsync(bool userAsked)
    {
        SetBusy(true);
        try
        {
            await using var client = await OwServiceClient.ConnectAsync(TimeSpan.FromSeconds(3));
            var info = await client.Service.CheckForUpdateAsync();
            if (info.Available && info.LatestVersion is not null)
            {
                _available = info.LatestVersion;
                _text.Text = $"Có bản mới {info.LatestVersion} (bạn đang dùng {info.CurrentVersion}). Mọi dữ liệu và lịch sử dọn dẹp được giữ nguyên khi cập nhật.";
                _notes.Text = string.IsNullOrWhiteSpace(info.Notes) ? string.Empty : "Thay đổi: " + info.Notes.Trim();
                _install.Visibility = Visibility.Visible;
                Available?.Invoke(info.LatestVersion);
            }
            else
            {
                _available = null;
                _install.Visibility = Visibility.Collapsed;
                _notes.Text = string.Empty;
                _text.Text = info.Error is not null && userAsked
                    ? $"Bạn đang dùng bản {info.CurrentVersion}. {info.Error}"
                    : $"Bạn đang dùng bản mới nhất ({info.CurrentVersion}).";
            }
        }
        catch (TimeoutException)
        {
            _text.Text = "Chưa kết nối được dịch vụ OW nên chưa kiểm tra được bản mới.";
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex);
            _text.Text = $"Không kiểm tra được bản mới: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task InstallAsync()
    {
        if (_available is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = _install.XamlRoot,
            Title = $"Cập nhật lên bản {_available}?",
            Content = "Optimize Win sẽ tự đóng, cài bản mới rồi tự mở lại sau khoảng 1 phút. Nếu cài lỗi, bản cũ được khôi phục tự động. Dữ liệu và lịch sử dọn dẹp được giữ nguyên.",
            PrimaryButtonText = "Cập nhật",
            CloseButtonText = "Để sau",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        _install.IsEnabled = false;
        _text.Text = "Đang tải bản mới và kiểm tra...";
        try
        {
            await using var client = await OwServiceClient.ConnectAsync(TimeSpan.FromSeconds(3));
            var result = await client.Service.InstallUpdateAsync(_available);
            _text.Text = result.Message;
            if (result.Started)
            {
                ReopenAfterUpdate(_available);
            }
            else
            {
                _install.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex);
            _text.Text = $"Không cập nhật được: {ex.Message}";
            _install.IsEnabled = true;
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// The installer closes this app. A small hidden PowerShell (running as the user) waits until the new
    /// version is installed, then opens the app again; after 5 minutes it opens whatever is installed.
    /// </summary>
    private static void ReopenAfterUpdate(string version)
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OptimizeWin");
        var script =
            $"$d='{dir.Replace("'", "''")}'; $v='{version}'; $end=(Get-Date).AddMinutes(5); " +
            "while ((Get-Date) -lt $end) { Start-Sleep -Seconds 3; " +
            "try { if ((Get-Content (Join-Path $d 'installed.txt') -ErrorAction Stop).Trim() -eq $v) { break } } catch { } }; " +
            "Start-Sleep -Seconds 5; Start-Process (Join-Path $d 'OptimizeWin.exe')";
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-WindowStyle", "Hidden", "-Command", script })
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy.IsActive = busy;
        _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _check.IsEnabled = !busy;
    }
}
