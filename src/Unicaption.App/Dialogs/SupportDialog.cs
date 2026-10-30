using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Unicaption.App.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Unicaption.App.Dialogs;

/// <summary>
/// WS 05 Support: only actions with a real destination are shown (UI 06). "Start tour" closes
/// the dialog and replays onboarding. "Copy diagnostics" gives support a secret-free summary (DIAG 01).
/// </summary>
public sealed class SupportDialog : ContentDialog
{
    private readonly DialogHeader _header;

    public SupportDialog(XamlRoot root)
    {
        XamlRoot = root;
        Style = (Style)Application.Current.Resources["UcDialogStyle"];
        _header = new DialogHeader { Glyph = "", Heading = "Help & Support", Subtitle = "Get help, or replay the workspace tour." };
        Title = _header;
        CloseButtonText = "Close";

        var cfg = AppServices.Current.Config;
        var list = new StackPanel { Spacing = 2, Width = 380 };
        list.Children.Add(Row("", "Start tour", "A one-minute walk through the workspace", () =>
        {
            TourRequested = true;
            Hide();
        }));
        if (!string.IsNullOrEmpty(cfg.SupportEmail))
            list.Children.Add(Row("", cfg.SupportEmail, "Email support", () => Open("mailto:" + cfg.SupportEmail)));
        if (!string.IsNullOrEmpty(cfg.BookCallUrl))
            list.Children.Add(Row("", "Book a call", "Opens Calendly", () => Open(cfg.BookCallUrl)));
        if (!string.IsNullOrEmpty(cfg.WhatsAppUrl))
            list.Children.Add(Row("", "WhatsApp Group", "Product news and community help", () => Open(cfg.WhatsAppUrl)));
        list.Children.Add(Row("", "Copy diagnostics", "No tokens, audio or transcript text", CopyDiagnostics));
        Content = list;
    }

    /// <summary>True when the dialog closed because the user chose "Start tour".</summary>
    public bool TourRequested { get; private set; }

    private static Button Row(string glyph, string title, string subtitle, Action onClick)
    {
        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            Style = (Style)Application.Current.Resources["UcText"],
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock { Text = subtitle, Style = (Style)Application.Current.Resources["UcMutedText"] });

        var tile = new Border
        {
            Style = (Style)Application.Current.Resources["UcIconTile"],
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(10),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new FontIcon { Glyph = glyph, FontSize = 16, Style = (Style)Application.Current.Resources["UcTileIcon"] },
        };

        var content = new Grid { ColumnSpacing = 14 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(tile);
        Grid.SetColumn(text, 1);
        content.Children.Add(text);

        var button = new Button
        {
            Content = content,
            Style = (Style)Application.Current.Resources["UcRowButton"],
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(button, $"{title}, {subtitle}");
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
        _header.Subtitle = "Diagnostics copied to the clipboard.";
    }
}
