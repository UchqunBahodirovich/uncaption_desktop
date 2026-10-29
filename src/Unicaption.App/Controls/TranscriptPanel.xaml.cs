using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Unicaption.Core.Time;
using Unicaption.Core.Transcript;
using Windows.UI;

namespace Unicaption.App.Controls;

/// <summary>
/// One transcript column (TEXT 01–04). Segments are rendered incrementally: a view is
/// reused per <see cref="Segment"/> and only rebuilt when its revision, scale, active
/// flag or direction changes. Text is selectable for copy.
/// </summary>
public sealed partial class TranscriptPanel : UserControl
{
    private static readonly Color[] SpeakerFg =
    [
        Color.FromArgb(255, 0x4F, 0x46, 0xE5), Color.FromArgb(255, 0x05, 0x96, 0x69),
        Color.FromArgb(255, 0xD9, 0x77, 0x06), Color.FromArgb(255, 0xDC, 0x26, 0x26),
        Color.FromArgb(255, 0x7C, 0x3A, 0xED),
    ];

    private static readonly Color[] SpeakerBg =
    [
        Color.FromArgb(0x14, 0x63, 0x66, 0xF1), Color.FromArgb(0x14, 0x10, 0xB9, 0x81),
        Color.FromArgb(0x14, 0xF5, 0x9E, 0x0B), Color.FromArgb(0x14, 0xEF, 0x44, 0x44),
        Color.FromArgb(0x14, 0xA8, 0x55, 0xF7),
    ];

    private readonly Dictionary<Segment, SegmentView> _views = [];
    private bool _isTranslation;
    private bool _followTail = true;
    private double _scale = 1;
    private FlowDirection? _directionOverride;
    private string _language = "en";

    public TranscriptPanel()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => RebuildAll();
        Motion.AttachShowHide(JumpButton, 0, 8); // rises from the bottom edge it is pinned to
    }

    public event EventHandler? LanguageClicked;
    public event EventHandler? ClearClicked;
    public event EventHandler? DirectionToggled;

    public void Configure(string title, bool isTranslation, string emptySubtitle, string emptyGlyph)
    {
        _isTranslation = isTranslation;
        TitleText.Text = title;
        EmptySubtitle.Text = emptySubtitle;
        EmptyIcon.Glyph = emptyGlyph;
        LeadLanguagePill.Visibility = isTranslation ? Visibility.Collapsed : Visibility.Visible;
        TrailLanguagePill.Visibility = isTranslation ? Visibility.Visible : Visibility.Collapsed;
        SourceActions.Visibility = isTranslation ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(this, title);
    }

    /// <param name="hintCount">Extra hint languages; shown as "EN +2" on the pill.</param>
    public void SetLanguage(string code, bool enabled, int hintCount = 0)
    {
        _language = code;
        foreach (var pill in new[] { LeadLanguagePill, TrailLanguagePill })
        {
            pill.Content = hintCount > 0 ? $"{code.ToUpperInvariant()} +{hintCount}" : code.ToUpperInvariant();
            pill.IsEnabled = enabled; // NAV 02: languages are locked while a session is active
            AutomationProperties.SetName(pill, $"{TitleText.Text} language: {Core.Catalog.Catalogs.LanguageName(code)}");
            ToolTipService.SetToolTip(pill, enabled ? "Change language" : "Stop the session to change language");
        }
    }

    public void SetLive(bool live, int seconds)
    {
        LiveBadge.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        LiveText.Text = "LIVE " + SessionClock.Format(seconds);
    }

    /// <summary>Manual direction toggle for the source panel (TEXT 03); null = automatic by language.</summary>
    public void SetDirectionOverride(FlowDirection? direction)
    {
        _directionOverride = direction;
        RebuildAll();
    }

    public FlowDirection EffectivePanelDirection =>
        _directionOverride ?? (Rtl.IsRtl(_language) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight);

    public void SetScale(double scale)
    {
        if (Math.Abs(scale - _scale) < 0.001) return;
        _scale = scale;
        RebuildAll();
    }

    public void Render(IReadOnlyList<Segment> segments)
    {
        var visible = segments.Where(s => !s.IsEmpty).ToList();
        EmptyState.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var alive = new HashSet<Segment>(visible);
        foreach (var dead in _views.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            SegmentsHost.Children.Remove(_views[dead].Root);
            _views.Remove(dead);
        }

        for (var i = 0; i < visible.Count; i++)
        {
            var seg = visible[i];
            var active = i == visible.Count - 1;
            if (!_views.TryGetValue(seg, out var view))
            {
                view = new SegmentView();
                _views[seg] = view;
            }
            view.Update(seg, active, _scale, DirectionFor(seg), this);

            var idx = SegmentsHost.Children.IndexOf(view.Root);
            if (idx != i)
            {
                if (idx >= 0) SegmentsHost.Children.RemoveAt(idx);
                SegmentsHost.Children.Insert(Math.Min(i, SegmentsHost.Children.Count), view.Root);
            }
        }

        if (_followTail) ScrollToEnd();
        else if (visible.Count > 0) JumpButton.Visibility = Visibility.Visible;
    }

    private FlowDirection DirectionFor(Segment s) =>
        _directionOverride ?? (Rtl.IsRtl(s.Language ?? _language) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight);

    private void RebuildAll()
    {
        foreach (var v in _views.Values) v.Invalidate();
        var ordered = SegmentsHost.Children.Select(c => _views.FirstOrDefault(kv => kv.Value.Root == c).Key)
            .Where(k => k is not null).ToList();
        Render(ordered!);
    }

    private void ScrollToEnd()
    {
        JumpButton.Visibility = Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => Scroller.ChangeView(null, Scroller.ScrollableHeight, null, true));
    }

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate) return;
        _followTail = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 24;
        if (_followTail) JumpButton.Visibility = Visibility.Collapsed;
    }

    private void OnJumpClick(object sender, RoutedEventArgs e)
    {
        _followTail = true;
        ScrollToEnd();
    }

    private void OnLanguageClick(object sender, RoutedEventArgs e) => LanguageClicked?.Invoke(this, EventArgs.Empty);
    private void OnClearClick(object sender, RoutedEventArgs e) => ClearClicked?.Invoke(this, EventArgs.Empty);
    private void OnDirectionClick(object sender, RoutedEventArgs e) => DirectionToggled?.Invoke(this, EventArgs.Empty);

    /// <summary>Segment: padding 10×4, side border 2.5, outer radius 8, bottom margin 6.</summary>
    private sealed class SegmentView
    {
        private readonly RichTextBlock _text;
        private readonly TextBlock _resumed;
        private int _revision = -1;
        private bool _active;
        private double _scale;
        private FlowDirection _dir;

        public SegmentView()
        {
            _resumed = new TextBlock
            {
                Text = "— resumed —",
                FontSize = 10,
                CharacterSpacing = 50,
                Margin = new Thickness(0, 0, 0, 2),
                Visibility = Visibility.Collapsed,
            };
            _text = new RichTextBlock
            {
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            };
            var stack = new StackPanel();
            stack.Children.Add(_resumed);
            stack.Children.Add(_text);
            Root = new Border { Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(10, 4, 10, 4), Child = stack };
        }

        public Border Root { get; }

        public void Invalidate() => _revision = -1;

        public void Update(Segment seg, bool active, double scale, FlowDirection dir, FrameworkElement host)
        {
            if (_revision == seg.Revision && _active == active && Math.Abs(_scale - scale) < 0.001 && _dir == dir) return;
            _revision = seg.Revision;
            _active = active;
            _scale = scale;
            _dir = dir;

            Brush Theme(string key) => Ui.ThemeBrush(host, key);

            var palette = seg.Speaker % SpeakerFg.Length;
            var fg = new SolidColorBrush(SpeakerFg[palette]);

            Root.FlowDirection = dir;
            Root.Background = active ? Theme("UcBrandSurface") : new SolidColorBrush(SpeakerBg[palette]);
            Root.BorderBrush = active ? Theme("UcBrand") : fg;
            // In RTL the FlowDirection mirrors left/right, so the stripe lands on the right automatically.
            Root.BorderThickness = new Thickness(2.5, 0, 0, 0);
            Root.CornerRadius = new CornerRadius(0, 8, 8, 0);

            _resumed.Visibility = seg.Resumed ? Visibility.Visible : Visibility.Collapsed;
            _resumed.Foreground = Theme("UcTextMuted");

            var p = new Paragraph { LineHeight = 14 * scale * 1.75 };
            p.Inlines.Add(new Run
            {
                Text = $"S{seg.Speaker + 1} ",
                FontSize = 10 * scale,
                FontWeight = FontWeights.ExtraBold,
                CharacterSpacing = 60,
                Foreground = fg,
            });
            p.Inlines.Add(new Run { Text = seg.FinalText, FontSize = 14 * scale, Foreground = Theme("UcTextPrimary") });
            if (seg.TempText.Length > 0)
            {
                p.Inlines.Add(new Run
                {
                    Text = seg.TempText,
                    FontSize = 14 * scale,
                    FontStyle = Windows.UI.Text.FontStyle.Italic,
                    Foreground = Theme("UcTextMuted"),
                });
            }
            _text.Blocks.Clear();
            _text.Blocks.Add(p);
        }
    }
}

internal static class ThemeLookup
{
    /// <summary>Reads a brush from the Light/Dark theme dictionary matching the element's actual theme.</summary>
    public static object? TryGetValueFor(this IDictionary<object, object> themeDictionaries, ElementTheme theme, string key)
    {
        var name = theme == ElementTheme.Dark ? "Dark" : "Light";
        foreach (var dict in Application.Current.Resources.MergedDictionaries)
        {
            if (dict.ThemeDictionaries.TryGetValue(name, out var td) && td is ResourceDictionary rd &&
                rd.TryGetValue(key, out var value))
                return value;
        }
        return themeDictionaries.TryGetValue(name, out var own) && own is ResourceDictionary ownRd &&
               ownRd.TryGetValue(key, out var v) ? v : null;
    }
}
