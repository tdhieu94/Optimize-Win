using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;

namespace OW.App;

/// <summary>
/// When the main window fails to load, try each piece of it on its own and log which one breaks,
/// so the cause can be found from crash.log without a debugger.
/// </summary>
internal static class XamlDiagnostics
{
    private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"";

    public static string Probe()
    {
        var sb = new StringBuilder("XAML probe:\n");
        foreach (var key in new[] { "TitleLargeTextBlockStyle", "SubtitleTextBlockStyle", "TextFillColorSecondaryBrush" })
        {
            try
            {
                var found = Application.Current.Resources.TryGetValue(key, out var value);
                sb.AppendLine($"  resource {key}: {(found ? value?.GetType().Name : "MISSING")}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  resource {key}: {ex.GetType().Name} {ex.Message}");
            }
        }

        var snippets = new (string Name, string Xaml)[]
        {
            ("TextBlock", $"<TextBlock {Ns} Text=\"x\" />"),
            ("Button", $"<Button {Ns} Content=\"x\" />"),
            ("StackPanel", $"<StackPanel {Ns} Spacing=\"16\" />"),
            ("ScrollViewer", $"<ScrollViewer {Ns} />"),
            ("InfoBar", $"<InfoBar {Ns} IsOpen=\"True\" Title=\"x\" />"),
            ("TitleLargeStyle", $"<TextBlock {Ns} Text=\"x\" Style=\"{{StaticResource TitleLargeTextBlockStyle}}\" />"),
            ("SubtitleStyle", $"<TextBlock {Ns} Text=\"x\" Style=\"{{StaticResource SubtitleTextBlockStyle}}\" />"),
            ("SecondaryBrush", $"<TextBlock {Ns} Text=\"x\" Foreground=\"{{ThemeResource TextFillColorSecondaryBrush}}\" />"),
        };

        foreach (var (name, xaml) in snippets)
        {
            try
            {
                XamlReader.Load(xaml);
                sb.AppendLine($"  {name}: ok");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  {name}: FAILED {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}");
            }
        }

        return sb.ToString();
    }
}
