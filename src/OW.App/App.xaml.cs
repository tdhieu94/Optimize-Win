using Microsoft.UI.Xaml;

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

    protected override void OnLaunched(LaunchActivatedEventArgs args)
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
}
