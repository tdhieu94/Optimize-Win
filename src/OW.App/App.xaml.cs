using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;

namespace OW.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        CrashReporter.Install(this);
        DebugSettings.XamlResourceReferenceFailed += (_, e) => CrashReporter.WriteText("XamlResourceReferenceFailed: " + e.Message);
        try
        {
            InitializeComponent();
        }
        catch (Exception ex)
        {
            CrashReporter.Report(ex);
            throw;
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Skip(1).Any(a => string.Equals(a, HealthCheck.Argument, StringComparison.OrdinalIgnoreCase)))
        {
            // Background check from the scheduled task: no window unless notifications cannot be shown.
            var openWindow = false;
            try
            {
                openWindow = await HealthCheck.RunAsync();
            }
            catch (Exception ex)
            {
                CrashReporter.Write(ex);
            }

            if (!openWindow)
            {
                Exit();
                return;
            }
        }
        else
        {
            ListenForNotificationClicks();
        }

        OpenWindow();
    }

    private void OpenWindow()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("OW_TEST_FALLBACK") == "1")
            {
                throw new InvalidOperationException("OW_TEST_FALLBACK is set (used by CI to test the fallback window).");
            }

            _window = new MainWindow();
        }
        catch (Exception ex)
        {
            // Keep the app usable: log everything we can learn, then show the simple window instead.
            CrashReporter.Write(ex);
            CrashReporter.WriteText(XamlDiagnostics.Probe());
            _window = new FallbackWindow();
        }

        _window.Activate();
    }

    /// <summary>
    /// A click on an OW notification while the app is open brings the window to the front.
    /// (When the app is closed, Windows simply starts it, which opens the overview with the alerts.)
    /// </summary>
    private void ListenForNotificationClicks()
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                return;
            }

            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            AppNotificationManager.Default.NotificationInvoked += (_, _) => dispatcher.TryEnqueue(() =>
            {
                (_window as MainWindow)?.ShowHome();
                _window?.Activate();
            });
            AppNotificationManager.Default.Register();
        }
        catch (Exception ex)
        {
            // Notifications are a convenience; the window must open regardless.
            CrashReporter.WriteText("Notification registration skipped: " + ex.Message);
        }
    }
}
