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
/// "Dung lượng" section, built in code so the main and the fallback window share it.
/// Read-only: it shows which folders use the most space; it has no delete button.
/// </summary>
internal sealed class SpaceView
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    private readonly ComboBox _drives = new() { MinWidth = 120, PlaceholderText = "Chọn ổ" };
    private readonly Button _analyze = new() { Content = "Phân tích" };
    private readonly Button _stop = new() { Content = "Dừng", Visibility = Visibility.Collapsed };
    private bool _stopRequested;
    private readonly ProgressRing _busy = new() { IsActive = false, Width = 20, Height = 20, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly Button _up = new() { Content = "↑ Lên thư mục trên", Visibility = Visibility.Collapsed };
    private readonly TextBlock _where = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
    private readonly StackPanel _rows = new() { Spacing = 4 };
    private readonly StackPanel _largest = new() { Spacing = 2 };
    private readonly TextBlock _largestTitle = new() { Text = "File lớn nhất", FontSize = 16, FontWeight = FontWeights.SemiBold, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
    private readonly Stack<SpaceItem> _path = new();

    public SpaceView()
    {
        _analyze.Click += (_, _) => _ = AnalyzeAsync();
        _stop.Click += (_, _) =>
        {
            _stopRequested = true;
            _stop.IsEnabled = false;
        };
        _up.Click += (_, _) =>
        {
            if (_path.Count > 1)
            {
                _path.Pop();
                ShowFolder();
            }
        };

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        bar.Children.Add(_drives);
        bar.Children.Add(_analyze);
        bar.Children.Add(_stop);
        bar.Children.Add(_busy);

        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        nav.Children.Add(_up);
        nav.Children.Add(_where);

        Root.Children.Add(new TextBlock
        {
            Text = "Xem thư mục nào chiếm nhiều chỗ nhất. Chỉ đọc, không xóa gì. Bấm vào một thư mục để xem bên trong.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });
        Root.Children.Add(bar);
        Root.Children.Add(_status);
        Root.Children.Add(nav);
        Root.Children.Add(_rows);
        Root.Children.Add(_largestTitle);
        Root.Children.Add(_largest);
        SetDrives([]);
    }

    public StackPanel Root { get; } = new() { Spacing = 8 };

    /// <summary>Fills the drive picker from the disks the service reported.</summary>
    public void SetDrives(IReadOnlyList<DiskInfo> disks)
    {
        var letters = disks.SelectMany(d => d.Volumes)
            .Select(v => v.DriveLetter.TrimEnd(':'))
            .Where(l => l.Length == 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var previous = _drives.SelectedItem as string;
        _drives.Items.Clear();
        foreach (var l in letters)
        {
            _drives.Items.Add($"{l}:");
        }

        if (letters.Count > 0)
        {
            _drives.SelectedItem = previous is not null && _drives.Items.Contains(previous) ? previous : _drives.Items[0];
        }

        _analyze.IsEnabled = letters.Count > 0 && !_busy.IsActive;
    }

    private async Task AnalyzeAsync()
    {
        if (_drives.SelectedItem is not string drive)
        {
            return;
        }

        SetBusy(true);
        _status.Text = $"Đang bắt đầu phân tích ổ {drive} ... Ổ đầy có thể mất vài phút, bạn cứ để cửa sổ mở.";
        _rows.Children.Clear();
        _largest.Children.Clear();
        _largestTitle.Visibility = Visibility.Collapsed;
        _up.Visibility = Visibility.Collapsed;
        _where.Text = string.Empty;

        try
        {
            await using var client = await OwServiceClient.ConnectAsync(TimeSpan.FromSeconds(3));
            if (!await client.Service.StartSpaceScanAsync(drive[..1]))
            {
                _status.Text = "Đang có một lần phân tích khác chạy. Đợi nó xong hoặc bấm Dừng rồi thử lại.";
            }

            while (true)
            {
                var st = await client.Service.GetSpaceScanStatusAsync();
                if (_stopRequested)
                {
                    _stopRequested = false;
                    await client.Service.CancelSpaceScanAsync();
                }

                if (!st.Running)
                {
                    var report = st.HasReport ? await client.Service.GetSpaceReportAsync() : null;
                    if (report is not null)
                    {
                        Show(report);
                    }
                    else
                    {
                        _status.Text = st.Error ?? "Không có kết quả.";
                    }

                    break;
                }

                _status.Text = string.Create(
                    Vi,
                    $"Đang phân tích ổ {st.DriveLetter}: ... đã đếm {st.Files:N0} file ({Size(st.Bytes)}) trong {st.Folders:N0} thư mục, {st.Seconds:0} giây.\nĐang xem: {st.CurrentFolder}");
                await Task.Delay(1000);
            }
        }
        catch (TimeoutException)
        {
            _status.Text = "Chưa kết nối được dịch vụ OW. Bấm \"Kiểm tra lại\" ở trên rồi thử lại.";
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex);
            _status.Text = $"Không phân tích được ổ {drive}: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy.IsActive = busy;
        _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _analyze.IsEnabled = !busy;
        _drives.IsEnabled = !busy;
        _stop.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _stop.IsEnabled = busy;
        _stopRequested = false;
    }

    private void Show(SpaceReport r)
    {
        var used = r.DriveSizeBytes - r.DriveFreeBytes;
        var text = string.Create(
            Vi,
            $"Ổ {r.DriveLetter}: đang dùng {Size(used)} / {Size(r.DriveSizeBytes)}. Đã đếm {r.Root.FileCount:N0} file trong {r.FolderCount:N0} thư mục ({r.Seconds:0} giây).");
        if (r.InaccessibleFolders > 0)
        {
            text += string.Create(Vi, $" Có {r.InaccessibleFolders:N0} thư mục Windows không cho đọc nên không tính vào.");
        }

        var unseen = used - r.Root.SizeBytes;
        if (unseen > 1024L * 1024 * 1024)
        {
            text += $" Khoảng {Size(unseen)} không thấy trong thư mục nào (thường là file hệ thống như bộ nhớ ảo, ngủ đông, điểm khôi phục).";
        }
        else if (r.Root.SizeBytes > used + (1024L * 1024 * 1024))
        {
            text += $"\nLưu ý: tổng đếm được ({Size(r.Root.SizeBytes)}) lớn hơn phần ổ đang dùng. Windows cho nhiều tên file cùng trỏ tới một dữ liệu (nhất là trong thư mục Windows), nên có file bị đếm nhiều lần. Trong Windows Sandbox, file hệ thống còn được mượn từ máy thật. Con số của từng thư mục vẫn đúng để so sánh thư mục nào lớn hơn.";
        }

        _status.Text = text;

        _path.Clear();
        _path.Push(r.Root);
        ShowFolder();

        _largest.Children.Clear();
        foreach (var f in r.LargestFiles.Take(20))
        {
            _largest.Children.Add(new TextBlock
            {
                Text = $"{Size(f.SizeBytes),10}   {f.Path}",
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
            });
        }

        _largestTitle.Visibility = r.LargestFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowFolder()
    {
        var folder = _path.Peek();
        _up.Visibility = _path.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _where.Text = $"{folder.Path ?? folder.Name}  ·  {Size(folder.SizeBytes)}";
        _rows.Children.Clear();

        if (folder.Children.Count == 0)
        {
            _rows.Children.Add(new TextBlock { Text = "Thư mục này nhỏ nên không liệt kê chi tiết.", Opacity = 0.7 });
            return;
        }

        foreach (var item in folder.Children)
        {
            _rows.Children.Add(Row(item, folder.SizeBytes));
        }
    }

    private FrameworkElement Row(SpaceItem item, long parentSize)
    {
        var share = parentSize > 0 ? 100.0 * item.SizeBytes / parentSize : 0;
        var canOpen = item.IsFolder && item.Children.Count > 0;
        var icon = item.Path is null ? "…" : item.IsFolder ? "📁" : "📄";

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });

        var name = new TextBlock
        {
            Text = $"{icon}  {item.Name}" + (canOpen ? "  ›" : string.Empty),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = share, VerticalAlignment = VerticalAlignment.Center };
        var size = new TextBlock { Text = Size(item.SizeBytes), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(bar, 1);
        Grid.SetColumn(size, 2);
        grid.Children.Add(name);
        grid.Children.Add(bar);
        grid.Children.Add(size);

        if (!canOpen)
        {
            return new Border { Padding = new Thickness(12, 6, 12, 6), Child = grid };
        }

        var button = new Button
        {
            Content = grid,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 6, 12, 6),
        };
        button.Click += (_, _) =>
        {
            _path.Push(item);
            ShowFolder();
        };
        return button;
    }

    private static string Size(long bytes)
    {
        const double Kb = 1024, Mb = Kb * 1024, Gb = Mb * 1024;
        return bytes switch
        {
            >= (long)Gb => string.Create(Vi, $"{bytes / Gb:0.0} GB"),
            >= (long)Mb => string.Create(Vi, $"{bytes / Mb:0} MB"),
            >= (long)Kb => string.Create(Vi, $"{bytes / Kb:0} KB"),
            _ => $"{bytes} B",
        };
    }
}
