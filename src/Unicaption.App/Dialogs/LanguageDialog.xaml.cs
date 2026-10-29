using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Unicaption.Core.Catalog;
using Unicaption.Core.Settings;

namespace Unicaption.App.Dialogs;

public enum LanguageRole { None, Primary, Hint, Selected }

public sealed class LanguageItem(string code, string name) : INotifyPropertyChanged
{
    private LanguageRole _role;

    public string Code { get; } = code;
    public string Name { get; } = name;
    public string Badge => Code.Length >= 2 ? Code[..2].ToUpperInvariant() : Code.ToUpperInvariant();

    public LanguageRole Role
    {
        get => _role;
        set
        {
            if (_role == value) return;
            _role = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PrimaryVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HintVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedVisibility)));
        }
    }

    public Visibility PrimaryVisibility => Role == LanguageRole.Primary ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HintVisibility => Role == LanguageRole.Hint ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SelectedVisibility => Role == LanguageRole.Selected ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Role switch
    {
        LanguageRole.Primary => $"{Name} ({Code}), primary",
        LanguageRole.Hint => $"{Name} ({Code}), hint",
        LanguageRole.Selected => $"{Name} ({Code}), selected",
        _ => $"{Name} ({Code})",
    };
}

/// <summary>
/// WS 01, applied only on Confirm.
/// Transcription: the first language picked is the primary one; later picks are added as hints
/// (other languages that may be spoken). Clicking a picked language removes it; removing the
/// primary promotes the first hint. Translation: one target language, no primary/hint roles.
/// </summary>
public sealed partial class LanguageDialog : ContentDialog
{
    private readonly List<LanguageItem> _all = Catalogs.Languages.Select(l => new LanguageItem(l.Code, l.Name)).ToList();
    private readonly bool _forTranslation;
    private readonly List<string> _hints;
    private string? _primary;

    public LanguageDialog(string selected, bool forTranslation, IEnumerable<string>? hints = null)
    {
        InitializeComponent();
        _forTranslation = forTranslation;
        _primary = selected;
        _hints = forTranslation ? [] : (hints ?? []).Where(h => h != selected).Distinct().ToList();

        Header.Heading = forTranslation ? "Select Translation Language" : "Select Transcription Language";
        Header.Subtitle = forTranslation
            ? "Choose the language to translate into"
            : "First pick is the primary language; more picks are added as hints";

        Apply("");
        Refresh();
        if (_all.FirstOrDefault(i => i.Code == _primary) is { } current) LanguageList.ScrollIntoView(current);
    }

    /// <summary>Primary (transcription) or target (translation) language.</summary>
    public string Selected => _primary ?? "";

    public IReadOnlyList<string> Hints => _hints;

    private void Apply(string query)
    {
        var q = query.Trim().ToLowerInvariant();
        LanguageList.ItemsSource = q.Length == 0
            ? _all
            : _all.Where(l => l.Code.Contains(q) || l.Name.ToLowerInvariant().Contains(q)).ToList();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Apply(Search.Text);

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is LanguageItem item) Toggle(item.Code);
    }

    private void Toggle(string code)
    {
        LimitText.Text = "";
        if (_forTranslation)
        {
            _primary = code;
        }
        else if (code == _primary)
        {
            _primary = _hints.FirstOrDefault();
            if (_primary is not null) _hints.RemoveAt(0);
        }
        else if (!_hints.Remove(code))
        {
            if (_primary is null) _primary = code;
            else if (_hints.Count < WorkspaceSettings.MaxLanguageHints) _hints.Add(code);
            else LimitText.Text = $"Up to {WorkspaceSettings.MaxLanguageHints} hints";
        }
        Refresh();
    }

    private void Refresh()
    {
        foreach (var item in _all)
        {
            item.Role = item.Code == _primary
                ? (_forTranslation ? LanguageRole.Selected : LanguageRole.Primary)
                : _hints.Contains(item.Code) ? LanguageRole.Hint : LanguageRole.None;
        }

        SelectionChips.Children.Clear();
        if (_primary is not null) SelectionChips.Children.Add(Chip(_primary, _forTranslation ? null : "PRIMARY", primary: true));
        foreach (var h in _hints) SelectionChips.Children.Add(Chip(h, "HINT", primary: false));
        EmptySelection.Visibility = _primary is null ? Visibility.Visible : Visibility.Collapsed;
        IsPrimaryButtonEnabled = _primary is not null;
    }

    private Button Chip(string code, string? role, bool primary)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new TextBlock
        {
            Text = $"{code.ToUpperInvariant()} {Catalogs.LanguageName(code)}",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (role is not null)
        {
            content.Children.Add(new TextBlock
            {
                Text = role,
                FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.ExtraBold,
                CharacterSpacing = 50,
                Opacity = 0.75,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        if (!_forTranslation)
        {
            content.Children.Add(new FontIcon
            {
                Glyph = "",
                FontSize = 9,
                FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["UcIcons"],
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var chip = new Button
        {
            Content = content,
            Style = (Style)Application.Current.Resources[primary ? "UcPrimaryChipButton" : "UcChipButton"],
            IsHitTestVisible = !_forTranslation,
            IsTabStop = !_forTranslation,
        };
        AutomationProperties.SetName(chip, _forTranslation ? Catalogs.LanguageName(code) : $"Remove {Catalogs.LanguageName(code)}");
        chip.Click += (_, _) => Toggle(code);
        return chip;
    }
}
