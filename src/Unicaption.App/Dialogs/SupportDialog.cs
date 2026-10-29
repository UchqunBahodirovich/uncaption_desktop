using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Unicaption.App.Dialogs;

/// <summary>
/// WS 05 Support: only actions with a real destination are shown (UI 06). Start Tour has no
/// defined steps yet and is omitted. "Copy diagnostics" gives support a secret-free summary (DIAG 01).
/// </summary>
public sealed class SupportDialog : ContentDialog
{
    public SupportDialog(XamlRoot root)
    {
        XamlRoot = root;
        Title = "Help & Support";
        CloseButtonText = "Close";
        CornerRadius = new CornerRadius(18);

        var cfg = AppServices.Current.Config;
        var list = new StackPanel { Spacing = 4, Width = 360 };
        if (!string.IsNullOrEmpty(cfg.SupportEmail))
            list.Children.Add(Row("", cfg.SupportEmail, "Email support", () => Open("mailto:" + cfg.SupportEmail)));
        if (!string.IsNullOrEmpty(cfg.BookCallUrl))
            list.Children.Add(Row("", "Book a call", "Opens Calendly", () => Open(cfg.BookCallUrl)));
        if (!string.IsNullOrEmpty(cfg.WhatsAppUrl))
            list.Children.Add(Row("", "WhatsApp Group", null, () => Open(cfg.WhatsAppUrl)));
        list.Children.Add(Row("", "Copy diagnostics", "No tokens, audio or transcript text", CopyDiagnostics));
        Content = list;
    }

    private static Button Row(string glyph, string title, string? subtitle, Action onClick)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        if (subtitle is not null)
            text.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Opacity = 0.7 });
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18, FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["UcIcons"] });
        content.Children.Add(text);
        var button = new Button
        {
            Content = content,
            Style = (Style)Application.Current.Resources["UcRowButton"],
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static void Open(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) AppServices.Current.Browser.Open(uri);
    }

    private void CopyDiagnostics()
    {
        var pkg = new DataPackage();
        pkg.SetText(AppServices.Current.Diagnostics.Summary());
        Clipboard.SetContent(pkg);
        Title = "Diagnostics copied";
    }
}
