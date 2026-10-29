using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Unicaption.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            AppServices.Current?.Diagnostics.Error("app.unhandled", e.Exception);
            e.Handled = true; // keep the app alive; the failing action shows its own error state
        };
    }

    public static MainWindow MainWindow => ((App)Current)._window!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppServices.Initialize(DispatcherQueue.GetForCurrentThread());
        _window = new MainWindow();
        _window.Activate();
    }
}
