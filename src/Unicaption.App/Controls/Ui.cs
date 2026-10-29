using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Unicaption.App.Controls;

internal static class Ui
{
    /// <summary>Resolves a token brush (Themes/Tokens.xaml) for the element's current theme.</summary>
    public static Brush ThemeBrush(FrameworkElement element, string key) =>
        Application.Current.Resources.ThemeDictionaries.TryGetValueFor(element.ActualTheme, key) as Brush
        ?? new SolidColorBrush(Colors.Gray);

    /// <summary>Shows a dialog in the window's current theme (popups do not always inherit it).</summary>
    public static Windows.Foundation.IAsyncOperation<ContentDialogResult> ShowThemed(ContentDialog dialog, FrameworkElement owner)
    {
        dialog.XamlRoot ??= owner.XamlRoot;
        if (dialog.XamlRoot?.Content is FrameworkElement root) dialog.RequestedTheme = root.ActualTheme;
        return dialog.ShowAsync();
    }

    /// <summary>Short status for screen readers (UI 05); never used per transcript token.</summary>
    public static void Announce(TextBlock liveRegion, string text)
    {
        liveRegion.Text = text;
        var peer = FrameworkElementAutomationPeer.FromElement(liveRegion)
                   ?? FrameworkElementAutomationPeer.CreatePeerForElement(liveRegion);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
