using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Unicaption.App.Controls;
using Unicaption.App.Dialogs;
using Unicaption.App.Platform;
using Unicaption.Core.Api;
using Unicaption.Core.Audio;
using Unicaption.Core.Catalog;
using Unicaption.Core.Realtime;
using Unicaption.Core.Session;
using Unicaption.Core.Transcript;
using Unicaption.Core.Update;

namespace Unicaption.App.Views;

/// <summary>
/// Playground: sidebar, two transcript panels, Start/Stop and workspace overlays.
/// All session logic lives in <see cref="SessionController"/>; this page only renders
/// its state and forwards user intent.
/// </summary>
public sealed partial class PlaygroundPage : Page
{
    private const double CompactWidth = 900;

    private readonly AppServices _s = AppServices.Current;
    private readonly DispatcherQueueTimer _tick;
    private NotesPanel? _notes;
    private FlowDirection? _sourceDirectionOverride;
    private bool _sidebarMini;
    private bool _dialogOpen;
    private bool _initialized;

    public PlaygroundPage()
    {
        InitializeComponent();

        SourcePanel.Configure("Live Transcription", false, "Transcription will appear here", "");
        TranslationPanel.Configure("Live Translate", true, "Translation will appear here", "");
        SourcePanel.LanguageClicked += (_, _) => _ = PickLanguageAsync(source: true);
        TranslationPanel.LanguageClicked += (_, _) => _ = PickLanguageAsync(source: false);
        SourcePanel.ClearClicked += (_, _) => ClearSource();
        SourcePanel.DirectionToggled += (_, _) => ToggleSourceDirection();
        Dictionary.CloseRequested += (_, _) => SetDictionaryOpen(false);

        _tick = DispatcherQueue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(250);
        _tick.Tick += (_, _) => UpdateLiveClock();

        _s.Session.StateChanged += OnSessionStateChanged;
        _s.Session.TranscriptChanged += OnTranscriptChanged;
        _s.Account.Changed += (_, _) => RenderAccount();

        SizeChanged += (_, e) =>
        {
            ApplySidebarMode(animate: true);
            _notes?.Clamp();
        };
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized) return;
        _initialized = true;
        RenderProfile();
        RenderAccount();
        RenderSettings();
        RenderSession();
        RenderTranscripts(true, true);
        ApplySidebarMode(animate: false);

        if (!_s.UpdatePromptShown)
        {
            _s.UpdatePromptShown = true;
            await CheckForUpdateAsync();
        }
    }

    /// <summary>The page is cached; refresh everything that may differ after re-login or Usage/Billing.</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!_initialized) return;
        RenderProfile();
        RenderAccount();
        RenderSettings();
        RenderSession();
        RenderTranscripts(true, true);
    }

    // ═══ Rendering ═══════════════════════════════════════════════════════════

    private void RenderProfile()
    {
        var user = _s.Auth.User;
        var name = string.IsNullOrWhiteSpace(user?.DisplayName) ? "User" : user!.DisplayName!.Trim();
        NameText.Text = name;
        AvatarText.Text = MiniAvatarText.Text = name[..1].ToUpperInvariant();
    }

    private void RenderAccount()
    {
        var acc = _s.Account;
        var plan = acc.Plan;
        AccountRetry.Visibility = acc.State is AccountLoadState.Error or AccountLoadState.Cached ? Visibility.Visible : Visibility.Collapsed;

        if (plan is null)
        {
            PlanBadge.Text = acc.State == AccountLoadState.Error ? "UNAVAILABLE" : "…";
            MinutesText.Text = acc.State switch
            {
                AccountLoadState.Error => acc.ErrorMessage ?? "Plan could not be loaded",
                _ => "Loading plan…",
            };
            MinutesBar.IsIndeterminate = acc.State == AccountLoadState.Loading;
            MinutesBar.Value = 0;
            return;
        }

        PlanBadge.Text = plan.PlanName.ToUpperInvariant();
        var limit = plan.LimitMinutes is { } l ? $"{l:0}" : "∞";
        MinutesText.Text = $"{plan.UsedMinutes:0} / {limit} min" + (acc.State == AccountLoadState.Cached ? " · offline" : "");
        MinutesBar.IsIndeterminate = false;
        MinutesBar.Value = plan.MinutesProgress ?? 0;
        MinutesBar.Foreground = Brush(plan.IsMinutesLimitReached ? "UcError" : "UcBrand");
    }

    private void RenderSettings()
    {
        var st = _s.Settings;
        var locked = _s.Session.IsBusy;
        SourcePanel.SetLanguage(st.SourceLanguage, !locked);
        TranslationPanel.SetLanguage(st.TargetLanguage, !locked);
        SourcePanel.SetScale(st.TextScale);
        TranslationPanel.SetScale(st.TextScale);
        Dictionary.SetLanguages(st.SourceLanguage, st.TargetLanguage);

        ThemeIcon.Glyph = st.DarkMode ? "" : "";
        ThemeLabel.Text = st.DarkMode ? "Light Theme" : "Dark Theme";
        AutomationProperties.SetName(ThemeNav, ThemeLabel.Text);

        var source = (AudioSourceKind)st.AudioSource;
        SetNavState(MicNav, source == AudioSourceKind.Mic ? NavState.Filled : NavState.Normal);
        SetNavState(SystemNav, source == AudioSourceKind.System ? NavState.Filled : NavState.Normal);
        SetNavState(BothNav, source == AudioSourceKind.Both ? NavState.Filled : NavState.Normal);
        foreach (var b in new[] { MicNav, SystemNav, BothNav, ModelNav })
        {
            b.IsEnabled = !locked;
            ToolTipService.SetToolTip(b, locked ? "Stop the session to change this" : (_sidebarMini ? Label(b) : null));
        }

        SetNavState(NotesNav, _notes?.Visibility == Visibility.Visible ? NavState.Selected : NavState.Normal);
        SetNavState(DictionaryNav, Dictionary.Visibility == Visibility.Visible ? NavState.Selected : NavState.Normal);
    }

    private void RenderSession()
    {
        var session = _s.Session;
        var state = session.State;
        var busy = state is SessionState.RequestingPermission or SessionState.Connecting or SessionState.StartingCapture or SessionState.Stopping;

        StartButton.IsEnabled = !busy;
        StartButton.Background = Brush(state == SessionState.Live ? "UcError" : "UcBrand");
        StartIcon.Glyph = state == SessionState.Live ? "" : "";
        StartLabel.Text = state switch
        {
            SessionState.Live => "Stop",
            SessionState.Stopping => "Stopping…",
            SessionState.RequestingPermission or SessionState.Connecting or SessionState.StartingCapture => "Connecting…",
            SessionState.Error => "Retry",
            _ => "Start",
        };
        AutomationProperties.SetName(StartButton, StartLabel.Text);

        var live = state == SessionState.Live;
        if (live) _tick.Start(); else _tick.Stop();
        UpdateLiveClock();

        if (state == SessionState.Error && session.LastError is { } err) ShowError(err);
        else if (state != SessionState.Error) ErrorBar.IsOpen = false;

        RenderSettings();
    }

    private void UpdateLiveClock()
    {
        var live = _s.Session.State == SessionState.Live;
        var seconds = _s.Session.Clock.ElapsedWholeSeconds; // same clock as billing (TIME 01)
        SourcePanel.SetLive(live, seconds);
        TranslationPanel.SetLive(live, seconds);
    }

    private void RenderTranscripts(bool source, bool translation)
    {
        if (source) SourcePanel.Render(_s.Transcript.Source.Segments);
        if (translation) TranslationPanel.Render(_s.Transcript.Translation.Segments);
    }

    private void ShowError(SessionError err)
    {
        ErrorBar.Severity = InfoBarSeverity.Error;
        ErrorBar.Message = err.Message;
        ErrorBar.ActionButton = err.Kind switch
        {
            SessionErrorKind.PermissionDenied => ActionButton("Open Windows settings", SystemBrowser.OpenMicrophonePrivacySettings),
            SessionErrorKind.LimitReached => ActionButton("Open Billing", () => App.MainWindow.ShowBilling()),
            SessionErrorKind.AuthExpired => ActionButton("Sign in", () => _ = App.MainWindow.SignOutAsync()),
            _ => ActionButton("Retry", () => _ = StartAsync()),
        };
        ErrorBar.IsOpen = true;
        Announce(err.Message);
    }

    private static Button ActionButton(string text, Action onClick)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => onClick();
        return b;
    }

    // ═══ Session ═════════════════════════════════════════════════════════════

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        RenderSession();
        switch (_s.Session.State)
        {
            case SessionState.Live:
                Announce("Session started");
                break;
            case SessionState.Idle:
                Announce("Session stopped");
                break;
        }
    }

    private void OnTranscriptChanged(object? sender, (bool Source, bool Translation) e) => RenderTranscripts(e.Source, e.Translation);

    private void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_s.Session.IsLive) _ = _s.Session.StopAsync();
        else _ = StartAsync();
    }

    private Task StartAsync()
    {
        var st = _s.Settings;
        ErrorBar.IsOpen = false;
        return _s.Session.StartAsync(new SessionOptions(
            (AudioSourceKind)st.AudioSource,
            new RealtimeConfig(_s.Config.RealtimeModel, st.SourceLanguage, st.TargetLanguage)));
    }

    private void OnErrorClosed(InfoBar sender, object args) => _s.Session.DismissError();

    // ═══ Transcript controls ═════════════════════════════════════════════════

    private void ClearSource()
    {
        // TEXT 03: left panel only; translation, clock, audio and usage continue.
        _s.Transcript.Source.Clear();
        RenderTranscripts(true, false);
        Announce("Transcription cleared");
    }

    private void ToggleSourceDirection()
    {
        var current = _sourceDirectionOverride ?? SourcePanel.EffectivePanelDirection;
        _sourceDirectionOverride = current == FlowDirection.RightToLeft ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;
        SourcePanel.SetDirectionOverride(_sourceDirectionOverride);
    }

    private async Task PickLanguageAsync(bool source)
    {
        if (_s.Session.IsBusy || _dialogOpen) return;
        var st = _s.Settings;
        var dialog = new LanguageDialog(source ? st.SourceLanguage : st.TargetLanguage, forTranslation: !source) { XamlRoot = XamlRoot };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        if (source) st.SourceLanguage = dialog.Selected;
        else st.TargetLanguage = dialog.Selected;
        st.Save();
        if (source) _sourceDirectionOverride = null;
        SourcePanel.SetDirectionOverride(_sourceDirectionOverride);
        RenderSettings();
    }

    // ═══ Sidebar ═════════════════════════════════════════════════════════════

    private void OnToggleSidebar(object sender, RoutedEventArgs e)
    {
        _s.Settings.SidebarExpanded = !_s.Settings.SidebarExpanded;
        _s.Settings.Save();
        ApplySidebarMode(animate: true);
    }

    private void ApplySidebarMode(bool animate)
    {
        var mini = ActualWidth > 0 && ActualWidth < CompactWidth || !_s.Settings.SidebarExpanded;
        if (mini == _sidebarMini && Sidebar.Width is 72.0 or 264.0) return;
        _sidebarMini = mini;
        var target = mini ? 72.0 : 264.0;

        if (animate && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            var anim = new DoubleAnimation
            {
                To = target,
                Duration = TimeSpan.FromMilliseconds(180),
                EnableDependentAnimation = true,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(anim, Sidebar);
            Storyboard.SetTargetProperty(anim, "Width");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Begin();
        }
        else
        {
            Sidebar.Width = target;
        }

        ProfileCard.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        MiniProfile.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceLabel.Visibility = AudioLabel.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        foreach (var b in NavButtons())
        {
            if (b.Content is StackPanel sp && sp.Children.Count > 1) sp.Children[1].Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
            b.HorizontalContentAlignment = mini ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            b.Padding = mini ? new Thickness(8, 11, 8, 11) : new Thickness(12, 11, 12, 11);
            ToolTipService.SetToolTip(b, mini ? Label(b) : null); // AC 23: tooltips in mini mode
        }
    }

    private IEnumerable<Button> NavButtons() =>
    [
        ModelNav, DomainNav, TextSizeNav, ThemeNav, ContextNav, NotesNav, DictionaryNav, MicNav, SystemNav, BothNav,
        SupportNav, LogoutNav,
    ];

    private static string? Label(Button b) =>
        b.Content is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock t ? t.Text : null;

    private enum NavState { Normal, Selected, Filled }

    private void SetNavState(Button b, NavState state)
    {
        b.Background = state switch
        {
            NavState.Filled => Brush("UcBrand"),
            NavState.Selected => Brush("UcBrandSurface"),
            _ => new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        var fg = state switch
        {
            NavState.Filled => Brush("UcOnBrand"),
            NavState.Selected => Brush("UcBrand"),
            _ => null,
        };
        if (b.Content is StackPanel sp)
        {
            if (sp.Children[0] is FontIcon icon) icon.Foreground = fg ?? Brush("UcTextSecondary");
            if (sp.Children.Count > 1 && sp.Children[1] is TextBlock label)
            {
                label.Foreground = fg ?? Brush("UcTextPrimary");
                label.FontWeight = state == NavState.Filled ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Medium;
            }
        }
        if (state != NavState.Normal) AutomationProperties.SetItemStatus(b, "Selected");
        else AutomationProperties.SetItemStatus(b, "");
    }

    private void OnModel(object sender, RoutedEventArgs e)
    {
        // WS 02: Apex names are not mapped to server models yet; only the active model is selectable.
        var rows = Catalogs.Models.Select((m, i) => PopupRow(m.Name, i == 0 ? m.Subtitle : "Coming soon", TierLabel(m.Tier),
            selected: i == 0, enabled: false, onClick: null)).ToList();
        ShowPopup((Button)sender, "CHOOSE MODEL", rows);
    }

    private void OnDomain(object sender, RoutedEventArgs e)
    {
        // WS 02: domain is not sent to the backend yet; shown as information, not as an active AI switch.
        var rows = Catalogs.Domains.Select((d, i) => PopupRow(d.Name, i == 0 ? d.Subtitle : "Coming soon", TierLabel(d.Tier),
            selected: i == 0, enabled: false, onClick: null)).ToList();
        ShowPopup((Button)sender, "CHOOSE DOMAIN", rows);
    }

    private void OnTextSize(object sender, RoutedEventArgs e)
    {
        Flyout? flyout = null;
        var rows = Catalogs.TextSizes.Select(t => PopupRow(t.Label, null, $"{t.Scale * 100:0}%",
            selected: Math.Abs(_s.Settings.TextScale - t.Scale) < 0.001, enabled: true, onClick: () =>
            {
                _s.Settings.TextScale = t.Scale;
                _s.Settings.Save();
                RenderSettings();
                flyout?.Hide();
            })).ToList();
        flyout = ShowPopup((Button)sender, "TEXT SIZE", rows, width: 220);
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        _s.Settings.DarkMode = !_s.Settings.DarkMode;
        _s.Settings.Save();
        App.MainWindow.ApplyTheme();
        DispatcherQueue.TryEnqueue(() =>
        {
            RenderSettings();
            RenderSession();
            RenderAccount();
        });
    }

    private async void OnContext(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen) return;
        await ShowDialogAsync(new ContextDialog(_s.Settings, Catalogs.Domains[0].Name) { XamlRoot = XamlRoot });
    }

    private void OnNotes(object sender, RoutedEventArgs e)
    {
        if (_notes is not null)
        {
            CloseNotes();
            return;
        }
        _notes = new NotesPanel();
        _notes.CloseRequested += (_, _) => CloseNotes();
        NotesHost.Children.Add(_notes);
        _notes.Loaded += (_, _) =>
        {
            _notes?.PlaceDefault();
            _notes?.FocusEditor();
        };
        RenderSettings();
    }

    /// <summary>Close keeps the text for this run (WS 04): the panel is hidden, not destroyed.</summary>
    private void CloseNotes()
    {
        if (_notes is null) return;
        _notes.Visibility = _notes.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (_notes.Visibility == Visibility.Visible) _notes.FocusEditor();
        SetNavState(NotesNav, _notes.Visibility == Visibility.Visible ? NavState.Selected : NavState.Normal);
        if (_notes.Visibility == Visibility.Collapsed) NotesNav.Focus(FocusState.Programmatic);
    }

    private void OnDictionary(object sender, RoutedEventArgs e) => SetDictionaryOpen(Dictionary.Visibility != Visibility.Visible);

    private void SetDictionaryOpen(bool open)
    {
        Dictionary.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) Dictionary.FocusQuery();
        else DictionaryNav.Focus(FocusState.Programmatic);
        RenderSettings();
    }

    private void OnAudioSource(object sender, RoutedEventArgs e)
    {
        if (_s.Session.IsBusy) return;
        _s.Settings.AudioSource = int.Parse((string)((Button)sender).Tag);
        _s.Settings.Save();
        RenderSettings();
    }

    private async void OnSupport(object sender, RoutedEventArgs e)
    {
        if (_dialogOpen) return;
        await ShowDialogAsync(new SupportDialog(XamlRoot));
    }

    private async void OnLogout(object sender, RoutedEventArgs e) => await App.MainWindow.SignOutAsync();

    private void OnUsage(object sender, RoutedEventArgs e) => App.MainWindow.ShowUsage();

    private void OnBilling(object sender, RoutedEventArgs e) => App.MainWindow.ShowBilling();

    private void OnAccountRetry(object sender, RoutedEventArgs e) => _ = _s.Account.RefreshAsync();

    private void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Dictionary.Visibility == Visibility.Visible)
        {
            SetDictionaryOpen(false);
            args.Handled = true;
        }
        else if (_notes?.Visibility == Visibility.Visible)
        {
            CloseNotes();
            args.Handled = true;
        }
    }

    // ═══ Popups ══════════════════════════════════════════════════════════════

    private Flyout ShowPopup(Button anchor, string header, IEnumerable<UIElement> rows, double width = 280)
    {
        var stack = new StackPanel { Width = width - 32 };
        stack.Children.Add(new TextBlock
        {
            Text = header,
            Style = (Style)Application.Current.Resources["UcSectionLabel"],
            Margin = new Thickness(0, 0, 0, 12),
        });
        foreach (var r in rows) stack.Children.Add(r);

        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(14)));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(16)));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.BackgroundProperty, Brush("UcSurfaceCard")));
        var flyout = new Flyout
        {
            Content = stack,
            Placement = FlyoutPlacementMode.RightEdgeAlignedTop,
            FlyoutPresenterStyle = presenterStyle,
        };
        flyout.ShowAt(anchor);
        return flyout;
    }

    private UIElement PopupRow(string title, string? subtitle, string? badge, bool selected, bool enabled, Action? onClick)
    {
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = Brush(selected ? "UcBrand" : "UcTextPrimary") });
        if (badge is not null)
        {
            titleRow.Children.Add(new Border
            {
                Style = (Style)Application.Current.Resources["UcBadge"],
                Child = new TextBlock { Text = badge, FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.ExtraBold, Foreground = Brush("UcBrand") },
            });
        }
        var text = new StackPanel();
        text.Children.Add(titleRow);
        if (subtitle is not null) text.Children.Add(new TextBlock { Text = subtitle, FontSize = 13, Foreground = Brush("UcTextSecondary") });

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (selected)
            grid.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Foreground = Brush("UcBrand"), FontFamily = (FontFamily)Application.Current.Resources["UcIcons"] });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var button = new Button
        {
            Content = grid,
            Style = (Style)Application.Current.Resources["UcRowButton"],
            Background = selected ? Brush("UcBrandSurface") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            IsEnabled = enabled || selected,
        };
        AutomationProperties.SetName(button, subtitle is null ? title : $"{title}, {subtitle}");
        if (selected) AutomationProperties.SetItemStatus(button, "Selected");
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }

    private static string? TierLabel(Tier t) => t switch { Tier.Free => "FREE", Tier.Pro => "PRO", Tier.Ultra => "ULTRA", _ => null };

    // ═══ Update (section 17) ═════════════════════════════════════════════════

    private async Task CheckForUpdateAsync()
    {
        WindowsUpdate? update;
        try
        {
            update = await _s.Updates.CheckAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _s.Diagnostics.Warn("update.check_failed", ("type", ex.GetType().Name));
            return;
        }
        if (update is null || _dialogOpen) return;

        var notes = string.IsNullOrWhiteSpace(update.Notes) ? "" : "\n\n" + update.Notes;
        var ask = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Update available",
            Content = $"Unicaption {update.Version} is ready to install.{notes}" +
                      (_s.Session.IsBusy ? "\n\nThe current session will be stopped first." : ""),
            PrimaryButtonText = "Update",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ShowDialogAsync(ask) != ContentDialogResult.Primary) return;
        await InstallUpdateAsync(update);
    }

    private async Task InstallUpdateAsync(WindowsUpdate update)
    {
        // UPDATE 03 / AC 37: never install during capture; stop and submit usage first.
        await _s.Session.ShutdownAsync();

        var bar = new ProgressBar { IsIndeterminate = true, Width = 320, Margin = new Thickness(0, 8, 0, 0) };
        var cts = new CancellationTokenSource();
        var finished = false;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Downloading update",
            Content = bar,
            CloseButtonText = "Cancel",
        };
        dialog.Closing += (_, args) =>
        {
            if (!finished) cts.Cancel();
        };
        _dialogOpen = true;
        _ = Ui.ShowThemed(dialog, this);

        var progress = new Progress<(long Received, long? Total)>(p =>
        {
            if (p.Total is > 0)
            {
                bar.IsIndeterminate = false;
                bar.Value = 100.0 * p.Received / p.Total.Value;
            }
        });

        try
        {
            var path = await _s.Updates.DownloadAsync(update, progress, cts.Token);
            finished = true;
            dialog.Hide();
            _s.Diagnostics.Info("update.launching", ("version", update.Version));
            Process.Start(new ProcessStartInfo(path, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1") { UseShellExecute = true });
            Application.Current.Exit(); // the installer needs this process closed
        }
        catch (OperationCanceledException)
        {
            // user cancelled; app stays on the current version
        }
        catch (Exception ex)
        {
            finished = true;
            dialog.Hide();
            ErrorBar.Severity = InfoBarSeverity.Warning;
            ErrorBar.Message = ex is UpdateException ? $"Update could not be installed: {ex.Message}" : "Update could not be installed.";
            ErrorBar.ActionButton = ActionButton("Retry", () => _ = InstallUpdateAsync(update));
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _dialogOpen = false;
            cts.Dispose();
        }
    }

    // ═══ Helpers ═════════════════════════════════════════════════════════════

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        _dialogOpen = true;
        try
        {
            return await Ui.ShowThemed(dialog, this);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void Announce(string text) => Ui.Announce(StatusAnnouncer, text);

    private Brush Brush(string key) => Ui.ThemeBrush(this, key);
}
