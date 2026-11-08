using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OW.Client;
using OW.Contracts;

namespace OW.App;

/// <summary>
/// "Dọn rác" and "Lịch sử dọn dẹp" sections, built in code so both windows share them.
/// Cleaning moves files into quarantine through the service; every clean can be undone from the history list.
/// </summary>
internal sealed class CleanupView
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    private readonly Button _scan = new() { Content = "Quét rác" };
    private readonly Button _clean = new() { Content = "Dọn các mục đã chọn", IsEnabled = false, Style = AccentStyle() };
    private readonly ProgressRing _busy = new() { IsActive = false, Width = 20, Height = 20, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly StackPanel _categories = new() { Spacing = 6 };
    private readonly StackPanel _history = new() { Spacing = 6 };
    private readonly TextBlock _historyStatus = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly List<(CheckBox Box, JunkCategoryInfo Info)> _boxes = [];
    private Guid _scanId;

    public CleanupView()
    {
        _scan.Click += (_, _) => _ = ScanAsync();
        _clean.Click += (_, _) => _ = CleanAsync();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        bar.Children.Add(_scan);
        bar.Children.Add(_clean);
        bar.Children.Add(_busy);

        CleanRoot.Children.Add(new TextBlock
        {
            Text = "Tìm file rác an toàn để dọn. File không bị xóa ngay mà được chuyển vào vùng cách ly 7 ngày: trong thời gian đó bạn có thể bấm \"Hoàn tác\" ở trang Lịch sử để lấy lại. Sau 7 ngày chỗ trống mới được giải phóng hẳn.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });
        CleanRoot.Children.Add(bar);
        CleanRoot.Children.Add(_categories);
        CleanRoot.Children.Add(_status);

        HistoryRoot.Children.Add(_historyStatus);
        HistoryRoot.Children.Add(_history);
        ShowHistory([]);
    }

    public StackPanel CleanRoot { get; } = new() { Spacing = 8 };

    public StackPanel HistoryRoot { get; } = new() { Spacing = 8 };

    public void ShowHistory(IReadOnlyList<BatchSummary> batches)
    {
        _history.Children.Clear();
        if (batches.Count == 0)
        {
            _history.Children.Add(new TextBlock { Text = "Chưa có lần dọn nào.", Opacity = 0.7 });
            return;
        }

        foreach (var b in batches)
        {
            _history.Children.Add(HistoryRow(b));
        }
    }

    private FrameworkElement HistoryRow(BatchSummary b)
    {
        var state = b.State switch
        {
            "Completed" => "Xong",
            "CompletedWithProblems" => "Xong, có file không dọn được",
            "Undone" => "Đã hoàn tác",
            "PartiallyUndone" => "Đã hoàn tác một phần",
            "Running" => "Đang chạy",
            _ => b.State,
        };

        var text = new TextBlock
        {
            Text = b.Title.Contains("khởi động:", StringComparison.Ordinal)
                ? string.Create(Vi, $"{b.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm} · {b.Title}\n{state}")
                : string.Create(Vi, $"{b.CreatedUtc.ToLocalTime():dd/MM/yyyy HH:mm} · {b.Title}\n{b.Done} file · {Size(b.Bytes)} · {state}"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);

        if (b.State is "Completed" or "CompletedWithProblems" or "PartiallyUndone" && b.Done > b.Undone)
        {
            var undo = new Button { Content = "Hoàn tác", VerticalAlignment = VerticalAlignment.Center };
            undo.Click += (_, _) => _ = UndoAsync(b, undo);
            Grid.SetColumn(undo, 1);
            grid.Children.Add(undo);
        }

        return new Border
        {
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10),
            Child = grid,
        };
    }

    private async Task ScanAsync()
    {
        SetBusy(true);
        _status.Text = "Đang tìm file rác...";
        _categories.Children.Clear();
        _boxes.Clear();
        _clean.IsEnabled = false;
        try
        {
            await using var client = await OwServiceClient.ConnectAsync(TimeSpan.FromSeconds(3));
            var result = await client.Service.ScanJunkAsync();
            _scanId = result.ScanId;
            foreach (var c in result.Categories)
            {
                var box = new CheckBox
                {
                    IsChecked = c.FileCount > 0,
                    IsEnabled = c.FileCount > 0,
                    Content = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = string.Create(Vi, $"{c.Title}: {c.FileCount:N0} file · {Size(c.Bytes)}"),
                                FontWeight = FontWeights.SemiBold,
                                TextWrapping = TextWrapping.Wrap,
                            },
                            new TextBlock { Text = c.Description, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                        },
                    },
                };
                box.Checked += (_, _) => UpdateCleanButton();
                box.Unchecked += (_, _) => UpdateCleanButton();
                _boxes.Add((box, c));
                _categories.Children.Add(box);
            }

            var total = result.Categories.Sum(c => c.Bytes);
            _status.Text = result.Note
                ?? (total == 0
                    ? "Không tìm thấy file rác nào cần dọn. Máy đang sạch."
                    : $"Tìm thấy tổng cộng {Size(total)}. Chọn mục muốn dọn rồi bấm \"Dọn các mục đã chọn\".");
            UpdateCleanButton();
        }
        catch (TimeoutException)
        {
            _status.Text = "Chưa kết nối được dịch vụ OW. Bấm \"Kiểm tra lại\" ở trên rồi thử lại.";
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex);
            _status.Text = $"Không quét được: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task CleanAsync()
    {
        var chosen = _boxes.Where(x => x.Box.IsChecked == true && x.Info.FileCount > 0).Select(x => x.Info).ToList();
        if (chosen.Count == 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = _clean.XamlRoot,
            Title = "Dọn rác?",
            Content = string.Create(
                Vi,
                $"{chosen.Sum(c => c.FileCount):N0} file ({Size(chosen.Sum(c => c.Bytes))}) sẽ được chuyển vào vùng cách ly.\n\nTrong 7 ngày bạn có thể bấm \"Hoàn tác\" ở trang Lịch sử để lấy lại toàn bộ."),
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonText = "Dọn",
            CloseButtonText = "Thôi",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        _clean.IsEnabled = false;
        _status.Text = "Đang dọn...";
        try
        {
            await using var client = await OwServiceClient.ConnectAsync(TimeSpan.FromSeconds(3));
            var result = await client.Service.CleanJunkAsync(_scanId, chosen.Select(c => c.Id).ToList());
            _status.Text = result.BatchId == Guid.Empty
                ? result.Message
                : $"{result.Message} ({Size(result.Bytes)}). Muốn lấy lại thì bấm \"Hoàn tác\" ở trang Lịch sử.";
            _categories.Children.Clear();
            _boxes.Clear();
            ShowHistory(await client.Service.GetHistoryAsync(20));
        }
        catch (TimeoutException)
        {
            _status.Text = "Chưa kết nối được dịch vụ OW. Bấm \"Kiểm tra lại\" ở trên rồi thử lại.";
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex);
            _status.Text = $"Không dọn được: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task UndoAsync(BatchSummary b, Button button)
    {
        button.IsEnabled = false;
        try
        {
            await using var client = await OwServiceClient.ConnectAsync(TimeSpan.FromSeconds(3));
            var result = await client.Service.UndoBatchAsync(b.Id);
            var text = $"Đã khôi phục {result.Restored} file.";
            if (result.Failed > 0)
            {
                text += $" {result.Failed} file chưa khôi phục được.";
            }

            if (result.Messages.Count > 0)
            {
                text += "\n" + string.Join("\n", result.Messages.Take(5));
            }

            _historyStatus.Text = text;
            ShowHistory(await client.Service.GetHistoryAsync(20));
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex);
            _historyStatus.Text = $"Không hoàn tác được: {ex.Message}";
            button.IsEnabled = true;
        }
    }

    private void UpdateCleanButton() =>
        _clean.IsEnabled = !_busy.IsActive && _boxes.Any(x => x.Box.IsChecked == true && x.Info.FileCount > 0);

    private void SetBusy(bool busy)
    {
        _busy.IsActive = busy;
        _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _scan.IsEnabled = !busy;
    }

    private static Style? AccentStyle() =>
        Application.Current.Resources.TryGetValue("AccentButtonStyle", out var s) ? s as Style : null;

    private static string Size(long bytes)
    {
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        return bytes switch
        {
            >= (long)Gb => string.Create(Vi, $"{bytes / Gb:0.0} GB"),
            >= (long)Mb => string.Create(Vi, $"{bytes / Mb:0.0} MB"),
            >= (long)Kb => string.Create(Vi, $"{bytes / Kb:0} KB"),
            _ => $"{bytes} B",
        };
    }
}
