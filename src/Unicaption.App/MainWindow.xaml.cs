using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using Unicaption.App.Platform;
using Unicaption.App.Views;
using Windows.Graphics;
using WinRT.Interop;

namespace Unicaption.App;

public sealed partial class MainWindow : Window
{
    private const int ReferenceWidth = 1280, ReferenceHeight = 720;
    private const int MinWidth = 1024, MinHeight = 700;

    private readonly WindowHooks _hooks;
    private bool _closingHandled;

    public MainWindow()
    {
        InitializeComponent();
        var services = AppServices.Current;
        var hwnd = WindowNative.GetWindowHandle(this);
        _hooks = new WindowHooks(hwnd, MinWidth, MinHeight);
        _hooks.Suspending += () => _ = services.Session.OnSystemSuspendAsync();

        AppWindow.Resize(new SizeInt32((int)(ReferenceWidth * _hooks.Scale), (int)(ReferenceHeight * _hooks.Scale)));
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "app_icon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        AppWindow.Closing += OnClosing;

        ApplyTheme();
        services.Auth.SignedOut += (_, _) => DispatcherQueue.TryEnqueue(ShowLogin);

        if (services.Auth.TryRestore()) ShowPlayground();
        else ShowLogin();
    }

    public void ApplyTheme()
    {
        var dark = AppServices.Current.Settings.DarkMode;
        Root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var tb = AppWindow.TitleBar;
            var bg = dark ? ColorHelper.FromArgb(255, 0x0D, 0x11, 0x17) : ColorHelper.FromArgb(255, 0xF7, 0xF8, 0xFA);
            var fg = dark ? ColorHelper.FromArgb(255, 0xF0, 0xF4, 0xF8) : ColorHelper.FromArgb(255, 0x1C, 0x24, 0x30);
            tb.BackgroundColor = bg;
            tb.InactiveBackgroundColor = bg;
            tb.ButtonBackgroundColor = bg;
            tb.ButtonInactiveBackgroundColor = bg;
            tb.ForegroundColor = fg;
            tb.ButtonForegroundColor = fg;
        }
    }

    public void ShowLogin()
    {
        RootFrame.Navigate(typeof(LoginPage), null, new SuppressNavigationTransitionInfo());
        RootFrame.BackStack.Clear();
    }

    public void ShowPlayground()
    {
        RootFrame.Navigate(typeof(PlaygroundPage), null, new SuppressNavigationTransitionInfo());
        RootFrame.BackStack.Clear();
        _ = AppServices.Current.Account.RefreshAsync();
    }

    /// <summary>Usage/Billing are pushed on top; the live session keeps running (NAV 02).</summary>
    public void ShowUsage() => RootFrame.Navigate(typeof(UsagePage), null, new DrillInNavigationTransitionInfo());

    public void ShowBilling() => RootFrame.Navigate(typeof(BillingPage), null, new DrillInNavigationTransitionInfo());

    public void GoBack()
    {
        if (RootFrame.CanGoBack) RootFrame.GoBack();
        else ShowPlayground();
    }

    public async Task SignOutAsync()
    {
        var s = AppServices.Current;
        await s.Session.ShutdownAsync();
        s.Auth.SignOut();
        s.Account.Reset();
        s.Transcript.ResetForNewSession();
        ShowLogin();
    }

    /// <summary>Closing while live: stop capture and submit usage before exiting.</summary>
    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        var session = AppServices.Current.Session;
        if (_closingHandled || !session.IsBusy) return;
        args.Cancel = true;
        _closingHandled = true;
        try
        {
            await session.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            AppServices.Current.Diagnostics.Warn("app.close_timeout");
        }
        Close();
    }
}
