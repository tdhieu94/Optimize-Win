using Microsoft.UI.Xaml;

namespace OW.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        CrashReporter.Install(this);
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
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            CrashReporter.Report(ex);
            throw;
        }
    }
}
