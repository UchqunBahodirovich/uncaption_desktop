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
    private OnboardingTour? _tour;
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
        Motion.AttachShowHide(Dictionary, 24, 0); // slides in from, and back out to, the right edge

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

        // Layout must settle first so the tour can anchor to real control bounds.
        if (!_s.Settings.OnboardingCompleted) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, StartTour);

        if (!_s.UpdatePromptShown)
        {
            _s.UpdatePromptShown = true;
            await CheckForUpdateAsync();
        }
    }

    /// <summary>The page is cached; refresh everything that may differ after re-login.</summary>
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

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _tour?.Stop();
    }

    /// <summary>The sidebar toggle sits inside the window drag strip and must stay clickable.</summary>
    internal FrameworkElement TitleBarInteractive => BrandButton;

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

        // Only what the user needs at a glance: the plan and how many minutes are left.
        PlanBadge.Text = plan.PlanName.ToUpperInvariant();
        var left = plan.LimitMinutes is { } l ? $"{Math.Max(0, l - plan.UsedMinutes):0} min left" : "Unlimited minutes";
        MinutesText.Text = left + (acc.State == AccountLoadState.Cached ? " · offline" : "");
        ToolTipService.SetToolTip(MiniAvatar, $"{plan.PlanName} · {left}");
        MinutesBar.IsIndeterminate = false;
        MinutesBar.Value = plan.MinutesProgress ?? 0;
        MinutesBar.Foreground = Brush(plan.IsMinutesLimitReached ? "UcError" : "UcBrand");
    }

    private void RenderSettings()
    {
        // Model / Domain nav items wear the icon of the item picked in their menu.
        SetNavGlyph(ModelNav, ModelGlyphs[ModelIndex % ModelGlyphs.Length]);
        SetNavGlyph(DomainNav, DomainGlyphs[DomainIndex % DomainGlyphs.Length]);

        var st = _s.Settings;
        var locked = _s.Session.IsBusy;
        SourcePanel.SetLanguage(st.SourceLanguage, !locked, st.SourceLanguageHints.Count);
        TranslationPanel.SetLanguage(st.TargetLanguage, !locked);
        SourcePanel.SetScale(st.TextScale);
        TranslationPanel.SetScale(st.TextScale);
        Dictionary.SetLanguages(st.SourceLanguage, st.TargetLanguage);


        var source = (AudioSourceKind)st.AudioSource;
        SetNavState(MicNav, source == AudioSourceKind.Mic ? NavState.Filled : NavState.Normal);
        SetNavState(SystemNav, source == AudioSourceKind.System ? NavState.Filled : NavState.Normal);
        SetNavState(BothNav, source == AudioSourceKind.Both ? NavState.Filled : NavState.Normal);
        var activeSource = source switch { AudioSourceKind.System => SystemNav, AudioSourceKind.Both => BothNav, _ => MicNav };
        foreach (var b in new[] { MicNav, SystemNav, BothNav, ModelNav, DomainNav })
        {
            // The source being captured stays fully visible while locked; it just ignores input.
            var keepVisible = locked && b == activeSource;
            b.IsEnabled = !locked || keepVisible;
            b.IsHitTestVisible = !locked;
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
        StartIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        StartSpinner.IsActive = busy;
        StartSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
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
            new RealtimeConfig(_s.Config.RealtimeModel, st.SourceLanguage, st.TargetLanguage, st.SourceLanguageHints,
                st.Domain, st.CustomContext.Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))));
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
        var dialog = source
            ? new LanguageDialog(st.SourceLanguage, forTranslation: false, st.SourceLanguageHints) { XamlRoot = XamlRoot }
            : new LanguageDialog(st.TargetLanguage, forTranslation: true) { XamlRoot = XamlRoot };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || dialog.Selected.Length == 0) return;
        if (source)
        {
            st.SourceLanguage = dialog.Selected;
            st.SourceLanguageHints = dialog.Hints.ToList();
        }
        else
        {
            st.TargetLanguage = dialog.Selected;
        }
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

        if (animate && Motion.AnimationsEnabled)
        {
            var anim = new DoubleAnimation
            {
                To = target,
                Duration = TimeSpan.FromMilliseconds(200),
                EnableDependentAnimation = true,
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 },
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
        ModelNav, DomainNav, ContextNav, NotesNav, DictionaryNav, MicNav, SystemNav, BothNav,
    ];

    private static string? Label(Button b) =>
        b.Content is StackPanel sp && sp.Children.Count > 1 && sp.Children[1] is TextBlock t ? t.Text : null;

    private static void SetNavGlyph(Button b, string glyph)
    {
        if (b.Content is StackPanel { Children.Count: > 0 } sp && sp.Children[0] is FontIcon icon) icon.Glyph = glyph;
    }

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

    private static readonly string[] ModelGlyphs = ["", "", ""];
    // Product decision: every Apex model is selectable. The consumer currently runs stt-rt-v4
    // for all of them (it ignores "model"); the domain is sent to Soniox as context.
    // The sidebar icons follow both selections.
    private int ModelIndex => Math.Max(0, Catalogs.Models.ToList().FindIndex(m => m.Name == _s.Settings.Model));

    private int DomainIndex => Math.Max(0, Catalogs.Domains.ToList().FindIndex(d => d.Name == _s.Settings.Domain));

    private static readonly string[] DomainGlyphs = ["", "", "", "", "", ""];

    private void OnModel(object sender, RoutedEventArgs e)
    {
        if (_s.Session.IsBusy) return; // the model is part of the session config
        Flyout? flyout = null;
        var current = ModelIndex;
        var cards = Catalogs.Models.Select((m, i) => OptionCard(ModelGlyphs[i % ModelGlyphs.Length], m.Name, m.Subtitle,
            TierLabel(m.Tier), selected: i == current, available: true, onClick: () =>
            {
                _s.Settings.Model = m.Name;
                _s.Settings.Save();
                RenderSettings();
                flyout?.Hide();
            }));
        flyout = ShowPopup((Button)sender, "Model", "Applies from the next session.", OptionList(cards));
    }

    private void OnDomain(object sender, RoutedEventArgs e)
    {
        if (_s.Session.IsBusy) return; // the domain is part of the session config
        Flyout? flyout = null;
        var current = DomainIndex;
        var cards = Catalogs.Domains.Select((d, i) => OptionCard(DomainGlyphs[i % DomainGlyphs.Length], d.Name, d.Subtitle,
            TierLabel(d.Tier), selected: i == current, available: true, onClick: () =>
            {
                _s.Settings.Domain = d.Name;
                _s.Settings.Save();
                RenderSettings();
                flyout?.Hide();
            }));
        flyout = ShowPopup((Button)sender, "Domain", "Tunes recognition vocabulary for your field. Applies from the next session.", OptionList(cards));
    }

    // ═══ Account menu ════════════════════════════════════════════════════════
    // Text size, theme, support and log out live behind the account name instead of the
    // sidebar list: they are set once and rarely touched, so they should not crowd it.

    private void OnAccountMenu(object sender, RoutedEventArgs e)
    {
        Flyout? flyout = null;
        void Close() => flyout?.Hide();

        var body = new StackPanel { Spacing = 4 };

        body.Children.Add(MenuLabel("TEXT SIZE"));
        var sizes = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        FillSizeTiles(sizes);
        body.Children.Add(sizes);

        body.Children.Add(MenuLabel("APPEARANCE"));
        var dark = _s.Settings.DarkMode;
        body.Children.Add(MenuRow(dark ? "" : "", dark ? "Light theme" : "Dark theme", dark ? "Dark" : "Light", danger: false, () =>
        {
            Close();
            ToggleTheme();
        }));

        body.Children.Add(new Border { Height = 1, Margin = new Thickness(4, 6, 4, 6), Background = Brush("UcBorderSubtle") });
        body.Children.Add(MenuRow("", "Help & support", null, danger: false, () =>
        {
            Close();
            _ = ShowSupportAsync();
        }));
        body.Children.Add(MenuRow("", "Log out", null, danger: true, () =>
        {
            Close();
            _ = App.MainWindow.SignOutAsync();
        }));

        var plan = _s.Account.Plan;
        var planName = plan is null || plan.PlanName.Length == 0 ? null : char.ToUpperInvariant(plan.PlanName[0]) + plan.PlanName[1..];
        var hint = planName is null ? MinutesText.Text : $"{planName} plan · {MinutesText.Text}";
        // Opens to the side at the shared 320 width, like the Model / Domain menus.
        flyout = ShowPopup((Button)sender, NameText.Text, hint, body);
    }

    /// <summary>Text size applies at once and the menu stays open, so the change can be judged in place.</summary>
    private void FillSizeTiles(Grid grid)
    {
        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        var sizes = Catalogs.TextSizes;
        for (var i = 0; i < sizes.Count; i++)
        {
            var (label, scale) = sizes[i];
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var tile = SizeTile(label, scale, Math.Abs(_s.Settings.TextScale - scale) < 0.001, () =>
            {
                _s.Settings.TextScale = scale;
                _s.Settings.Save();
                RenderSettings();
                FillSizeTiles(grid);
            });
            Grid.SetColumn(tile, i);
            grid.Children.Add(tile);
        }
    }

    private TextBlock MenuLabel(string text) => new()
    {
        Text = text,
        FontSize = 10,
        FontWeight = Microsoft.UI.Text.FontWeights.ExtraBold,
        CharacterSpacing = 73,
        Foreground = Brush("UcTextMuted"),
        Margin = new Thickness(4, 4, 4, 2),
    };

    private Button MenuRow(string glyph, string label, string? trailing, bool danger, Action onClick)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 16,
            FontFamily = (FontFamily)Application.Current.Resources["UcIcons"],
            Foreground = Brush(danger ? "UcError" : "UcTextSecondary"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var text = new TextBlock
        {
            Text = label,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Foreground = Brush(danger ? "UcError" : "UcTextPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (trailing is not null)
        {
            var t = new TextBlock { Text = trailing, FontSize = 12, Foreground = Brush("UcTextMuted"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(t, 2);
            grid.Children.Add(t);
        }

        var button = new Button
        {
            Content = grid,
            Style = (Style)Application.Current.Resources["UcRowButton"],
            Padding = new Thickness(10, 9, 10, 9),
            Margin = new Thickness(0),
        };
        AutomationProperties.SetName(button, label);
        button.Click += (_, _) => onClick();
        return button;
    }

    private void ToggleTheme()
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
        await ShowDialogAsync(new ContextDialog(_s.Settings, Catalogs.Domains[DomainIndex].Name) { XamlRoot = XamlRoot });
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

    private async Task ShowSupportAsync()
    {
        if (_dialogOpen) return;
        var dialog = new SupportDialog(XamlRoot);
        await ShowDialogAsync(dialog);
        if (dialog.TourRequested) StartTour();
    }

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
    // Option pickers share one layout: title + one-line hint, then cards. The selected card is
    // tinted with the brand; unavailable options are shown dimmed with a "Soon" tag, never as
    // controls that look clickable but do nothing.

    private Flyout ShowPopup(Button anchor, string title, string hint, UIElement body, double width = 320,
        FlyoutPlacementMode placement = FlyoutPlacementMode.RightEdgeAlignedTop)
    {
        var stack = new StackPanel { Width = width - 24, Spacing = 12 };
        var head = new StackPanel { Spacing = 2, Margin = new Thickness(4, 2, 4, 0) };
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("UcTextPrimary"),
        });
        head.Children.Add(new TextBlock { Text = hint, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Brush("UcTextMuted") });
        stack.Children.Add(head);
        stack.Children.Add(body);

        // Same surface as dialogs (UcFlyoutPresenter); brushes are resolved for the page theme
        // because flyout popups do not reliably inherit it.
        var presenterStyle = new Style(typeof(FlyoutPresenter)) { BasedOn = (Style)Application.Current.Resources["UcFlyoutPresenter"] };
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.BackgroundProperty, Brush("UcSurfaceCard")));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.BorderBrushProperty, Brush("UcBorderSubtle")));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(12)));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.RequestedThemeProperty, ActualTheme));
        var flyout = new Flyout
        {
            Content = stack,
            Placement = placement,
            FlyoutPresenterStyle = presenterStyle,
        };
        flyout.ShowAt(anchor);
        return flyout;
    }

    private static StackPanel OptionList(IEnumerable<UIElement> cards)
    {
        var list = new StackPanel { Spacing = 4 };
        foreach (var c in cards) list.Children.Add(c);
        return list;
    }

    private UIElement OptionCard(string glyph, string title, string subtitle, string? badge, bool selected, bool available,
        Action? onClick = null)
    {
        var tile = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brush(selected ? "UcBrand" : "UcSurfaceRaised"),
            Child = new FontIcon
            {
                Glyph = glyph,
                FontSize = 16,
                FontFamily = (FontFamily)Application.Current.Resources["UcIcons"],
                Foreground = Brush(selected ? "UcOnBrand" : "UcTextSecondary"),
            },
        };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = Brush("UcTextPrimary"),
        });
        if (badge is not null) titleRow.Children.Add(Pill(badge, "UcBrandSurface", "UcBrand"));
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(titleRow);
        text.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Foreground = Brush("UcTextMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(tile);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var trailing = available ? Radio(selected) : Soon();
        Grid.SetColumn(trailing, 2);
        grid.Children.Add(trailing);

        var name = available ? $"{title}, {subtitle}" : $"{title}, {subtitle}, coming soon";
        if (!available)
        {
            // Not a button: nothing to press yet.
            var row = new Border { Padding = new Thickness(10), CornerRadius = new CornerRadius(12), Opacity = 0.6, Child = grid };
            AutomationProperties.SetName(row, name);
            return row;
        }

        var button = new Button
        {
            Content = grid,
            Style = (Style)Application.Current.Resources["UcRowButton"],
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(12),
            Margin = new Thickness(0),
            BorderThickness = new Thickness(1),
            BorderBrush = selected ? Brush("UcBrandBorder") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Background = selected ? Brush("UcBrandSurface") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        AutomationProperties.SetName(button, name);
        if (selected) AutomationProperties.SetItemStatus(button, "Selected");
        if (onClick is not null) button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>Compact size tile: an "Aa" sample at the real scale plus its percentage; the name is in the tooltip.</summary>
    private FrameworkElement SizeTile(string label, double scale, bool selected, Action onClick)
    {
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(new Grid
        {
            Height = 22, // shared baseline row
            Children =
            {
                new TextBlock
                {
                    Text = "Aa",
                    FontSize = 13 * scale,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = Brush(selected ? "UcBrand" : "UcTextPrimary"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                },
            },
        });
        content.Children.Add(new TextBlock
        {
            Text = $"{scale * 100:0}%",
            FontSize = 10,
            Foreground = Brush(selected ? "UcBrand" : "UcTextMuted"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var button = new Button
        {
            Content = content,
            Style = (Style)Application.Current.Resources["UcRowButton"],
            Padding = new Thickness(4, 6, 4, 6),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0),
            BorderThickness = new Thickness(1),
            BorderBrush = Brush(selected ? "UcBrandBorder" : "UcBorderSubtle"),
            Background = Brush(selected ? "UcBrandSurface" : "UcSurfaceSubtle"),
        };
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, $"{label}, {scale * 100:0} percent");
        if (selected) AutomationProperties.SetItemStatus(button, "Selected");
        button.Click += (_, _) => onClick();
        return button;
    }

    private FrameworkElement Radio(bool selected)
    {
        var grid = new Grid { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Fill = selected ? Brush("UcBrand") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Stroke = Brush(selected ? "UcBrand" : "UcBorderSubtle"),
            StrokeThickness = 1.5,
        });
        if (selected)
        {
            grid.Children.Add(new FontIcon
            {
                Glyph = "",
                FontSize = 10,
                FontFamily = (FontFamily)Application.Current.Resources["UcIcons"],
                Foreground = Brush("UcOnBrand"),
            });
        }
        return grid;
    }

    private FrameworkElement Soon()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(new FontIcon
        {
            Glyph = "",
            FontSize = 9,
            FontFamily = (FontFamily)Application.Current.Resources["UcIcons"],
            Foreground = Brush("UcTextMuted"),
        });
        content.Children.Add(new TextBlock
        {
            Text = "Soon",
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = Brush("UcTextMuted"),
        });
        return new Border
        {
            Padding = new Thickness(8, 3, 8, 3),
            CornerRadius = new CornerRadius(6),
            Background = Brush("UcSurfaceRaised"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content,
        };
    }

    private Border Pill(string text, string background, string foreground) => new()
    {
        Padding = new Thickness(6, 1, 6, 1),
        CornerRadius = new CornerRadius(6),
        Background = Brush(background),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 9,
            FontWeight = Microsoft.UI.Text.FontWeights.ExtraBold,
            CharacterSpacing = 60,
            Foreground = Brush(foreground),
        },
    };

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
        if (update is null || _dialogOpen || _tour?.IsRunning == true) return;

        var notes = update.Notes?.Trim() ?? "";
        if (_s.Session.IsBusy) notes = (notes.Length > 0 ? notes + "\n\n" : "") + "The current session will be stopped first.";
        var ask = Ui.Dialog(this, "", "Update available", $"Unicaption {update.Version} is ready to install.",
            notes.Length > 0 ? notes : null);
        ask.PrimaryButtonText = "Update";
        ask.CloseButtonText = "Later";
        if (await ShowDialogAsync(ask) != ContentDialogResult.Primary) return;
        await InstallUpdateAsync(update);
    }

    private async Task InstallUpdateAsync(WindowsUpdate update)
    {
        // UPDATE 03 / AC 37: never install during capture; stop and submit usage first.
        await _s.Session.ShutdownAsync();

        var bar = new ProgressBar { IsIndeterminate = true, Width = 360, Margin = new Thickness(0, 8, 0, 0), Foreground = Brush("UcBrand") };
        var cts = new CancellationTokenSource();
        var finished = false;
        var dialog = Ui.Dialog(this, "", "Downloading update", $"Unicaption {update.Version}", bar);
        dialog.CloseButtonText = "Cancel";
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

    // ═══ Onboarding ══════════════════════════════════════════════════════════

    private void StartTour()
    {
        if (_tour?.IsRunning == true || _dialogOpen) return;
        _tour = new OnboardingTour(PageRoot, TourSteps());
        _tour.Finished += (_, _) =>
        {
            _s.Settings.OnboardingCompleted = true;
            _s.Settings.Save();
        };
        _tour.Start();
    }

    private IEnumerable<OnboardingTour.Step> TourSteps() =>
    [
        new(null, "", "Welcome to Unicaption",
            "Live captions and translation for anything you hear. Here is a one-minute tour of your workspace.", Hero: true),
        new(SourcePanel, "", "Live transcription",
            "Speech appears here as it is recognised. Use the language pill to choose what is being spoken.",
            TeachingTipPlacementMode.Right),
        new(TranslationPanel, "", "Live translation",
            "The same speech, translated as you go. Pick the target language from the pill on this panel.",
            TeachingTipPlacementMode.Left),
        new(MicNav, "", "Choose what to listen to",
            "Capture your microphone, the computer's sound (calls, videos), or both at once.",
            TeachingTipPlacementMode.Right),
        new(ContextNav, "", "Add context",
            "Names, products and jargon you add here help recognition get specialist terms right.",
            TeachingTipPlacementMode.Right),
        new(NotesNav, "", "Notes and dictionary",
            "Keep a notepad beside the captions, or look up a single word without leaving the session.",
            TeachingTipPlacementMode.Right),
        new(AccountButton, "", "Your account",
            "Click your name for text size, theme, help and log out.", TeachingTipPlacementMode.Right),
        new(MiniAvatar, "", "Your account",
            "Click your avatar for text size, theme, help and log out.", TeachingTipPlacementMode.Right),
        new(StartButton, "", "You're ready",
            "Press Start to begin captioning, and press it again to stop. You can replay this tour from Support.",
            TeachingTipPlacementMode.Top),
    ];

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
