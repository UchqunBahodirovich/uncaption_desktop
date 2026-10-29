using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Unicaption.App.Controls;

/// <summary>
/// First-run tour: one <see cref="TeachingTip"/> per step, anchored to the real control it
/// explains. Steps whose target is not on screen (e.g. collapsed panels) are skipped. Closing a
/// tip (X / Escape) ends the tour; "Get started" finishes it. Either way <see cref="Finished"/>
/// fires once, so the caller can remember that onboarding was seen.
/// </summary>
internal sealed class OnboardingTour
{
    public sealed record Step(
        FrameworkElement? Target,
        string Glyph,
        string Title,
        string Body,
        TeachingTipPlacementMode Placement = TeachingTipPlacementMode.Auto,
        bool Hero = false);

    private readonly Panel _host;
    private readonly List<Step> _steps;
    private TeachingTip? _tip;
    private int _index;

    public OnboardingTour(Panel host, IEnumerable<Step> steps)
    {
        _host = host;
        _steps = steps.Where(s => s.Target is null || IsOnScreen(s.Target)).ToList();
    }

    public event EventHandler? Finished;

    public bool IsRunning => _tip is not null;

    public void Start()
    {
        if (_steps.Count == 0) return;
        Show(0);
    }

    /// <summary>Ends the tour without waiting for the user (navigation away, session start…).</summary>
    public void Stop()
    {
        if (_tip is not { } tip) return;
        _tip = null;
        tip.IsOpen = false;
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void Show(int index)
    {
        _index = index;
        var step = _steps[index];
        var previous = _tip;
        var footer = Footer(index, out var primary);

        var tip = new TeachingTip
        {
            Title = step.Title,
            Subtitle = step.Body,
            Target = step.Target,
            PreferredPlacement = step.Target is null ? TeachingTipPlacementMode.Center : step.Placement,
            PlacementMargin = new Thickness(8),
            IconSource = new FontIconSource { Glyph = step.Glyph, FontFamily = (FontFamily)Application.Current.Resources["UcIcons"] },
            HeroContent = step.Hero ? Hero() : null,
            Content = footer,
            RequestedTheme = _host.ActualTheme,
            ShouldConstrainToRootBounds = true,
        };
        tip.Closed += (sender, args) =>
        {
            _host.DispatcherQueue.TryEnqueue(() => _host.Children.Remove(sender));
            if (ReferenceEquals(sender, _tip) && args.Reason != TeachingTipCloseReason.Programmatic) Complete();
        };
        primary.Loaded += (_, _) => primary.Focus(FocusState.Programmatic);

        _tip = tip;
        _host.Children.Add(tip);
        if (previous is not null) previous.IsOpen = false;
        tip.IsOpen = true;
    }

    private void Next()
    {
        if (_index + 1 < _steps.Count) Show(_index + 1);
        else Stop();
    }

    private void Back()
    {
        if (_index > 0) Show(_index - 1);
    }

    private void Complete()
    {
        _tip = null;
        Finished?.Invoke(this, EventArgs.Empty);
    }

    // ═══ Content ═════════════════════════════════════════════════════════════

    private UIElement Footer(int index, out Button primary)
    {
        var last = index == _steps.Count - 1;

        var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < _steps.Count; i++)
        {
            dots.Children.Add(new Border
            {
                // Current step is a short pill; the rest are dots, so progress reads at a glance.
                Width = i == index ? 18 : 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = Ui.ThemeBrush(_host, i == index ? "UcBrand" : "UcProgressTrack"),
            });
        }
        AutomationProperties.SetName(dots, $"Step {index + 1} of {_steps.Count}");

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var secondary = TourButton(index == 0 ? "Skip" : "Back", "UcSecondaryButton");
        secondary.Click += (_, _) =>
        {
            if (index == 0) Stop();
            else Back();
        };
        primary = TourButton(last ? "Get started" : "Next", "UcPrimaryButton");
        primary.Click += (_, _) => Next();
        buttons.Children.Add(secondary);
        buttons.Children.Add(primary);

        var grid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(dots);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return grid;
    }

    private static Button TourButton(string text, string style) => new()
    {
        Content = text,
        Style = (Style)Application.Current.Resources[style],
        MinHeight = 34,
        MinWidth = 72,
    };

    private UIElement Hero()
    {
        var glow = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
        glow.GradientStops.Add(new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(0x33, 0xFF, 0x3D, 0x00) });
        glow.GradientStops.Add(new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(0x0A, 0xFF, 0x3D, 0x00) });
        return new Border
        {
            Height = 128,
            Background = glow,
            Child = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/icon128.png")),
                Width = 64,
                Height = 64,
            },
        };
    }

    private static bool IsOnScreen(FrameworkElement element) =>
        element.Visibility == Visibility.Visible && element.ActualWidth > 0 && element.ActualHeight > 0;
}
