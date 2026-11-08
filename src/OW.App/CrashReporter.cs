using System.Runtime.InteropServices;

namespace OW.App;

/// <summary>
/// If the app fails, write what happened to %LOCALAPPDATA%\OptimizeWin\crash.log and tell the user,
/// instead of silently disappearing.
/// </summary>
internal static class CrashReporter
{
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OptimizeWin", "crash.log");

    public static void Install(Microsoft.UI.Xaml.Application app)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write(e.Exception);
            e.SetObserved();
        };
        app.UnhandledException += (_, e) => Report(e.Exception);
    }

    public static void Report(Exception? ex)
    {
        Write(ex);
        MessageBoxW(
            IntPtr.Zero,
            $"Optimize Win gặp lỗi và phải đóng lại.\n\n{ex?.Message}\n\nChi tiết đã được lưu ở:\n{LogPath}",
            "Optimize Win",
            0x10);
    }

    public static void Write(Exception? ex)
    {
        var text = $"{ex}";
        if (ex is not null)
        {
            text += $"\n  HResult: 0x{ex.HResult:X8}";
            foreach (System.Collections.DictionaryEntry entry in ex.Data)
            {
                text += $"\n  Data[{entry.Key}]: {entry.Value}";
            }
        }

        WriteText(text);
    }

    public static void WriteText(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {text}\n\n");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}
