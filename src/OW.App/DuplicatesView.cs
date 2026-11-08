using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OW.Client;
using OW.Contracts;

namespace OW.App;

/// <summary>
/// "File trùng" page: finds files with exactly the same content and lets the user pick which copies to drop.
/// Nothing is ticked by default; every group must keep at least one copy; dropped copies go to quarantine (undoable).
/// </summary>
internal sealed class DuplicatesView
{
    private const int PageSize = 100;
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    private readonly StackPanel _driveBoxes = new() { Orientation = Orientation.Horizontal, Spacing = 12 };
    private readonly Button _scan = new() { Content = "Tìm file trùng" };
    private readonly Button _stop = new() { Content = "Dừng", Visibility = Visibility.Collapsed };
    private readonly ProgressRing _busy = new() { IsActive = false, Width = 20, Height = 20, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 12, Visibility = Visibility.Collapsed };
    private readonly Button _remove = new() { Content = "Bỏ các bản đã chọn", IsEnabled = false };
    private readonly Button _auto = new() { Content = "Chọn giúp tôi (giữ bản cũ nhất)" };
    private readonly Button _none = new() { Content = "Bỏ chọn hết" };
    private readonly TextBlock _selection = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };
    private readonly StackPanel _groups = new() { Spacing = 12 };
    private readonly Button _more = new() { Content = "Hiện thêm", Visibility = Visibility.Collapsed };
    private readonly List<(DuplicateGroupInfo Group, List<(DuplicateFileInfo File, CheckBox Box)> Boxes)> _shown = [];
    private DuplicateReport? _report;
    private bool _stopRequested;
    private bool _adjusting;

    public DuplicatesView()
    {
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) && style is Style s)
        {
            _remove.Style = s;
        }

        _scan.Click += (_, _) => _ = ScanAsync();
        _stop.Click += (_, _) =>
        {
            _stopRequested = true;
            _stop.IsEnabled = false;
        };
        _auto.Click += (_, _) => SelectAll(keepOldest: true);
        _none.Click += (_, _) => SelectAll(keepOldest: false);
        _remove.Click += (_, _) => _ = RemoveAsync();
        _more.Click += (_, _) => ShowMore();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        bar.Children.Add(_scan);
        bar.Children.Add(_stop);
        bar.Children.Add(_busy);

        _actions.Children.Add(_remove);
        _actions.Children.Add(_auto);
        _actions.Children.Add(_none);
        _actions.Children.Add(_selection);

        Root.Children.Add(new TextBlock
        {
            Text = "Tìm ảnh, video, nhạc, tài liệu và file nén giống hệt nhau (so từng byte nội dung, không chỉ so tên). " +
                   "Không tìm trong thư mục Windows, chương trình, game và dữ liệu ứng dụng. Không có file nào được chọn sẵn: " +
                   "bạn tự đánh dấu bản muốn bỏ, mỗi nhóm luôn giữ lại ít nhất 1 bản. Bản bị bỏ vào vùng cách ly 7 ngày và hoàn tác được ở trang Lịch sử.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });
        Root.Children.Add(new TextBlock { Text = "Tìm trên các ổ:", Margin = new Thickness(0, 4, 0, 0) });
        Root.Children.Add(_driveBoxes);
        Root.Children.Add(bar);
        Root.Children.Add(_status);
        Root.Children.Add(_actions);
        Root.Children.Add(_groups);
        Root.Children.Add(_more);
        SetDrives([]);
    }

    public StackPanel Root { get; } = new() { Spacing = 8 };

    /// <summary>Raised after copies were moved to quarantine, so the history page can refresh.</summary>
    public event Action? Removed;

    public void SetDrives(IReadOnlyList<DiskInfo> disks)
    {
        var letters = disks.SelectMany(d => d.Volumes)
            .Select(v => v.DriveLetter.TrimEnd(':'))
            .Where(l => l.Length == 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unticked = _driveBoxes.Children.OfType<CheckBox>().Where(b => b.IsChecked != true).Select(b => (string)b.Tag).ToHashSet();
        _driveBoxes.Children.Clear();
        foreach (var l in letters)
        {
            _driveBoxes.Children.Add(new CheckBox { Content = $"{l}:", Tag = l, IsChecked = !unticked.Contains(l), MinWidth = 60 });
        }

        _scan.IsEnabled = letters.Count > 0 && !_busy.IsActive;
    }

    private async Task ScanAsync()
    {
        var drives = _driveBoxes.Children.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
        if (drives.Count == 0)
        {
            _status.Text = "Hãy đánh dấu ít nhất một ổ.";
            return;
        }

        Clear();
        SetBusy(true);
        _status.Text = "Đang bắt đầu tìm... Ổ nhiều ảnh, video có thể mất vài phút, bạn cứ để cửa sổ mở.";
        try
        {
            await using var client = await OwServiceClient.ConnectAsync(TimeSpan.FromSeconds(3));
            if (!await client.Service.StartDuplicateScanAsync(drives))
            {
                _status.Text = "Đang có một lần tìm khác chạy. Đợi nó xong hoặc bấm Dừng rồi thử lại.";
            }

            while (true)
            {
                var st = await client.Service.GetDuplicateScanStatusAsync();
                if (_stopRequested)
                {
                    _stopRequested = false;
                    await client.Service.CancelDuplicateScanAsync();
                }

                if (!st.Running)
                {
                    var report = st.HasReport ? await client.Service.GetDuplicateReportAsync() : null;
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

                _status.Text = string.Create(Vi, $"{st.Stage}... đã thấy {st.Files:N0} file để so, đã đọc {Size(st.HashedBytes)}, {st.Seconds:0} giây.\n{st.Current}");
                await Task.Delay(1000);
            }
        }
        catch (TimeoutException)
        {
            _status.Text = "Chưa kết nối được dịch vụ OW. Bấm \"Kiểm tra lại\" ở trang Tổng quan rồi thử lại.";
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex);
            _status.Text = $"Không tìm được: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void Show(DuplicateReport r)
    {
        _report = r;
        var drives = string.Join(", ", r.DriveLetters.Select(d => d + ":"));
        if (r.Groups.Count == 0)
        {
            _status.Text = string.Create(Vi, $"Không thấy file trùng nào trên {drives} (đã so {r.FilesCompared:N0} file, {r.Seconds:0} giây).");
            return;
        }

        var text = string.Create(
            Vi,
            $"Tìm thấy {r.Groups.Count:N0} nhóm file trùng trên {drives}. Nếu mỗi nhóm chỉ giữ 1 bản sẽ lấy lại được {Size(r.WastedBytes)}. (Đã so {r.FilesCompared:N0} file, {r.Seconds:0} giây.)");
        if (r.SkippedProgramFolders > 0)
        {
            text += string.Create(Vi, $" Đã bỏ qua {r.SkippedProgramFolders:N0} thư mục chương trình/game.");
        }

        _status.Text = text + "\nĐánh dấu bản muốn bỏ, hoặc bấm \"Chọn giúp tôi\" để chọn tất cả trừ bản cũ nhất của mỗi nhóm. Bấm \"Xem\" để mở thư mục chứa file.";
        _actions.Visibility = Visibility.Visible;
        ShowMore();
    }

    private void ShowMore()
    {
        if (_report is null)
        {
            return;
        }

        foreach (var g in _report.Groups.Skip(_shown.Count).Take(PageSize))
        {
            var boxes = new List<(DuplicateFileInfo, CheckBox)>();
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock
            {
                Text = string.Create(Vi, $"{g.Files.Count} bản giống nhau · mỗi bản {Size(g.SizeBytes)} · lấy lại được {Size(g.SizeBytes * (g.Files.Count - 1))}"),
                FontWeight = FontWeights.SemiBold,
            });

            foreach (var f in g.Files)
            {
                var box = new CheckBox
                {
                    Content = new TextBlock
                    {
                        Text = string.Create(Vi, $"{f.Path}\nSửa lần cuối {f.LastWriteUtc.ToLocalTime():dd/MM/yyyy HH:mm}"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                };
                box.Checked += (_, _) => OnTicked(g);
                box.Unchecked += (_, _) => UpdateSelection();
                var open = new HyperlinkButton { Content = "Xem", VerticalAlignment = VerticalAlignment.Center };
                var path = f.Path;
                open.Click += (_, _) => ShowInExplorer(path);

                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                Grid.SetColumn(open, 1);
                row.Children.Add(box);
                row.Children.Add(open);
                panel.Children.Add(row);
                boxes.Add((f, box));
            }

            _shown.Add((g, boxes));
            _groups.Children.Add(AppPages.Card(panel));
        }

        var left = _report.Groups.Count - _shown.Count;
        _more.Visibility = left > 0 ? Visibility.Visible : Visibility.Collapsed;
        _more.Content = string.Create(Vi, $"Hiện thêm ({left:N0} nhóm nữa)");
        UpdateSelection();
    }

    /// <summary>A group may never have every copy ticked: the last tick is undone with a note.</summary>
    private void OnTicked(DuplicateGroupInfo group)
    {
        if (_adjusting)
        {
            return;
        }

        var entry = _shown.First(s => ReferenceEquals(s.Group, group));
        if (entry.Boxes.All(b => b.Box.IsChecked == true))
        {
            _adjusting = true;
            entry.Boxes.Last(b => b.Box.IsChecked == true).Box.IsChecked = false;
            _adjusting = false;
            _status.Text = "Mỗi nhóm phải giữ lại ít nhất 1 bản, nên không thể chọn bỏ hết các bản trong một nhóm.";
        }

        UpdateSelection();
    }

    private void SelectAll(bool keepOldest)
    {
        _adjusting = true;
        foreach (var (_, boxes) in _shown)
        {
            for (var i = 0; i < boxes.Count; i++)
            {
                // The service lists the oldest copy first.
                boxes[i].Box.IsChecked = keepOldest && i > 0;
            }
        }

        _adjusting = false;
        UpdateSelection();
    }

    private List<(DuplicateFileInfo File, long Size)> Selected() =>
        _shown.SelectMany(s => s.Boxes.Where(b => b.Box.IsChecked == true).Select(b => (b.File, s.Group.SizeBytes))).ToList();

    private void UpdateSelection()
    {
        var chosen = Selected();
        _remove.IsEnabled = chosen.Count > 0 && !_busy.IsActive;
        _selection.Text = chosen.Count == 0 ? string.Empty : string.Create(Vi, $"Đã chọn {chosen.Count:N0} file ({Size(chosen.Sum(c => c.Size))})");
    }

    private async Task RemoveAsync()
    {
        var chosen = Selected();
        if (chosen.Count == 0 || _report is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = _remove.XamlRoot,
            Title = "Bỏ các bản trùng đã chọn?",
            Content = string.Create(
                Vi,
                $"{chosen.Count:N0} file ({Size(chosen.Sum(c => c.Size))}) sẽ được chuyển vào vùng cách ly. Mỗi nhóm vẫn giữ lại ít nhất 1 bản.\n\nTrước khi chuyển, OW kiểm tra lại nội dung từng file; file nào đã thay đổi sẽ được giữ nguyên.\n\nTrong 7 ngày bạn có thể bấm \"Hoàn tác\" ở trang Lịch sử để lấy lại toàn bộ."),
            DefaultButton = ContentDialogButton.Close,
            PrimaryButtonText = "Bỏ",
            CloseButtonText = "Thôi",
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        SetBusy(true);
        _status.Text = "Đang kiểm tra lại nội dung và chuyển vào vùng cách ly... File lớn có thể mất một lúc.";
        try
        {
            await using var client = await OwServiceClient.ConnectAsync(TimeSpan.FromSeconds(3));
            var result = await client.Service.RemoveDuplicatesAsync(_report.ScanId, chosen.Select(c => c.File.Path).ToList());
            if (result.BatchId != Guid.Empty)
            {
                Clear();
                _status.Text = $"{result.Message} ({Size(result.Bytes)}). Muốn lấy lại thì bấm \"Hoàn tác\" ở trang Lịch sử.";
                Removed?.Invoke();
            }
            else
            {
                _status.Text = result.Message;
            }
        }
        catch (TimeoutException)
        {
            _status.Text = "Chưa kết nối được dịch vụ OW. Bấm \"Kiểm tra lại\" ở trang Tổng quan rồi thử lại.";
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex);
            _status.Text = $"Không bỏ được: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private static void ShowInExplorer(string path)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), UseShellExecute = false };
            psi.ArgumentList.Add("/select," + path);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            CrashReporter.Write(ex);
        }
    }

    private void Clear()
    {
        _report = null;
        _shown.Clear();
        _groups.Children.Clear();
        _actions.Visibility = Visibility.Collapsed;
        _more.Visibility = Visibility.Collapsed;
        _selection.Text = string.Empty;
    }

    private void SetBusy(bool busy)
    {
        _busy.IsActive = busy;
        _busy.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _scan.IsEnabled = !busy;
        foreach (var b in _driveBoxes.Children.OfType<CheckBox>())
        {
            b.IsEnabled = !busy;
        }

        _stop.Visibility = busy && _report is null ? Visibility.Visible : Visibility.Collapsed;
        _stop.IsEnabled = busy;
        _stopRequested = false;
        UpdateSelection();
    }

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
