using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Unicaption.App.Controls;

/// <summary>
/// Shared motion for panels that appear on demand (Dictionary, Jump to latest, sign-in card).
/// Composition implicit animations run off the UI thread, so they stay smooth while transcript
/// text streams in. Panels enter and leave along the same path, and exit faster than they enter.
/// With Windows animation effects off, only the opacity fade remains (no movement).
/// </summary>
internal static class Motion
{
    public static readonly TimeSpan Enter = TimeSpan.FromMilliseconds(220);
    public static readonly TimeSpan Exit = TimeSpan.FromMilliseconds(150);

    public static bool AnimationsEnabled => new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    /// <summary>Fade + slide in from (dx, dy) when shown, and back out the same way when hidden.</summary>
    public static void AttachShowHide(UIElement element, float dx, float dy)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(element).Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var offset = AnimationsEnabled ? new Vector3(dx, dy, 0) : Vector3.Zero;
        ElementCompositionPreview.SetImplicitShowAnimation(element, FadeSlide(compositor, 0, 1, offset, Vector3.Zero, Enter));
        ElementCompositionPreview.SetImplicitHideAnimation(element, FadeSlide(compositor, 1, 0, Vector3.Zero, offset, Exit));
    }

    private static CompositionAnimationGroup FadeSlide(Compositor c, float fromOpacity, float toOpacity, Vector3 from, Vector3 to, TimeSpan duration)
    {
        // ≈ cubic-bezier(0.23, 1, 0.32, 1): moves immediately, so the panel answers the click at once.
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.23f, 1f), new Vector2(0.32f, 1f));

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0, fromOpacity);
        fade.InsertKeyFrame(1, toOpacity, ease);
        fade.Duration = duration;

        var slide = c.CreateVector3KeyFrameAnimation();
        slide.Target = "Translation";
        slide.InsertKeyFrame(0, from);
        slide.InsertKeyFrame(1, to, ease);
        slide.Duration = duration;

        var group = c.CreateAnimationGroup();
        group.Add(fade);
        group.Add(slide);
        return group;
    }
}
