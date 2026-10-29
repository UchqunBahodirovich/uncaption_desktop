using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Unicaption.App.Controls;

/// <summary>Title block for every <see cref="ContentDialog"/> (set as its Title) and tour step.</summary>
public sealed partial class DialogHeader : UserControl
{
    public DialogHeader()
    {
        InitializeComponent();
    }

    public string Glyph
    {
        get => BrandGlyph.Glyph;
        set => BrandGlyph.Glyph = DangerGlyph.Glyph = value;
    }

    public string Heading
    {
        get => HeadingText.Text;
        set => HeadingText.Text = value;
    }

    public string? Subtitle
    {
        get => SubtitleText.Text;
        set
        {
            SubtitleText.Text = value ?? "";
            SubtitleText.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>Red tile for destructive confirmations.</summary>
    public bool IsDestructive
    {
        get => DangerTile.Visibility == Visibility.Visible;
        set
        {
            DangerTile.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            BrandTile.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
