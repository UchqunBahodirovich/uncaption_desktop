using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    /// <summary>Height of the drag strip at the top of every page (DIP).</summary>
    public const double TitleBarHeight = 48;

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
        SetupTitleBar();

        ApplyTheme();
        services.Auth.SignedOut += (_, _) => DispatcherQueue.TryEnqueue(ShowLogin);

        if (services.Auth.TryRestore()) ShowPlayground();
        else ShowLogin();
    }

    public void ApplyTheme()
    {
        Root.RequestedTheme = AppServices.Current.Settings.DarkMode ? ElementTheme.Dark : ElementTheme.Light;
    }

    // ═══ Custom title bar ════════════════════════════════════════════════════

    private FrameworkElement? _titleBarInteractive;
    private RectInt32 _dragRegion;

    /// <summary>
    /// Removes the system caption (title text + min/max/close) but keeps the resize border.
    /// Dragging, double-click to maximize and the system menu stay native through a Caption
    /// region over the top strip; the custom buttons are left out of it so they get clicks.
    /// </summary>
    private void SetupTitleBar()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.SetBorderAndTitleBar(true, false);
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPresenterChange || args.DidSizeChange) UpdateMaximizeGlyph();
        };
        Activated += (_, args) =>
            CaptionButtons.Opacity = args.WindowActivationState == WindowActivationState.Deactivated ? 0.5 : 1;
        // The logo button moves with the animated sidebar, so the region follows layout, not size.
        Root.LayoutUpdated += (_, _) => UpdateDragRegion();
        RootFrame.Navigated += (_, _) => SetTitleBarInteractive((RootFrame.Content as PlaygroundPage)?.TitleBarInteractive);
    }

    /// <summary>A page control living inside the top strip (e.g. the sidebar toggle) that must stay clickable.</summary>
    public void SetTitleBarInteractive(FrameworkElement? element)
    {
        if (_titleBarInteractive is not null) _titleBarInteractive.SizeChanged -= OnInteractiveSizeChanged;
        _titleBarInteractive = element;
        if (element is not null) element.SizeChanged += OnInteractiveSizeChanged;
        UpdateDragRegion();
    }

    private void OnInteractiveSizeChanged(object sender, SizeChangedEventArgs e) => UpdateDragRegion();

    private void UpdateDragRegion()
    {
        if (Root.XamlRoot is null || Root.ActualWidth <= 0) return;
        var scale = Root.XamlRoot.RasterizationScale;

        double left = 0;
        if (_titleBarInteractive is { ActualWidth: > 0 } element)
        {
            var bounds = element.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            if (bounds.Top < TitleBarHeight) left = bounds.Right + 4;
        }
        var right = CaptionButtons.ActualWidth > 0
            ? CaptionButtons.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0)).X - 4
            : Root.ActualWidth;

        var caption = new RectInt32((int)(left * scale), 0, (int)(Math.Max(0, right - left) * scale), (int)(TitleBarHeight * scale));
        if (caption.Equals(_dragRegion)) return; // LayoutUpdated fires often; only push changes
        _dragRegion = caption;
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Caption, [caption]);
    }

    private void UpdateMaximizeGlyph()
    {
        var maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        MaximizeButton.Content = maximized ? "" : "";
        var label = maximized ? "Restore" : "Maximize";
        ToolTipService.SetToolTip(MaximizeButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MaximizeButton, label);
    }

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter p) p.Minimize();
    }

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is not OverlappedPresenter p) return;
        if (p.State == OverlappedPresenterState.Maximized) p.Restore();
        else p.Maximize();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => _ = CloseAfterShutdownAsync();

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

    public async Task SignOutAsync()
    {
        var s = AppServices.Current;
        await s.Session.ShutdownAsync();
        s.Auth.SignOut();
        s.Account.Reset();
        s.Transcript.ResetForNewSession();
        ShowLogin();
    }

    /// <summary>Closing while live (Alt+F4, taskbar): stop capture and submit usage before exiting.</summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingHandled || !AppServices.Current.Session.IsBusy) return;
        args.Cancel = true;
        _ = CloseAfterShutdownAsync();
    }

    private async Task CloseAfterShutdownAsync()
    {
        if (_closingHandled) return;
        _closingHandled = true;
        var session = AppServices.Current.Session;
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
