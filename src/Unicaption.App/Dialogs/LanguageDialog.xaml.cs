using Microsoft.UI.Xaml.Controls;
using Unicaption.Core.Catalog;

namespace Unicaption.App.Dialogs;

public sealed record LanguageItem(string Code, string Name)
{
    public string Badge => Code.Length >= 2 ? Code[..2].ToUpperInvariant() : Code.ToUpperInvariant();
    public override string ToString() => $"{Name} ({Code})";
}

/// <summary>WS 01: single language selection, applied only on Confirm.</summary>
public sealed partial class LanguageDialog : ContentDialog
{
    private readonly List<LanguageItem> _all = Catalogs.Languages.Select(l => new LanguageItem(l.Code, l.Name)).ToList();

    public LanguageDialog(string selected, bool forTranslation)
    {
        InitializeComponent();
        TitleText.Text = forTranslation ? "Select Translation Language" : "Select Transcription Language";
        Selected = selected;
        Apply("");
    }

    public string Selected { get; private set; }

    private void Apply(string query)
    {
        var q = query.Trim().ToLowerInvariant();
        var items = q.Length == 0
            ? _all
            : _all.Where(l => l.Code.Contains(q) || l.Name.ToLowerInvariant().Contains(q)).ToList();
        LanguageList.ItemsSource = items;
        LanguageList.SelectedItem = items.FirstOrDefault(i => i.Code == Selected);
        if (LanguageList.SelectedItem is not null) LanguageList.ScrollIntoView(LanguageList.SelectedItem);
        SelectedText.Text = $"{Selected.ToUpperInvariant()} {Catalogs.LanguageName(Selected)}";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Apply(Search.Text);

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageList.SelectedItem is not LanguageItem item) return;
        Selected = item.Code;
        SelectedText.Text = $"{item.Code.ToUpperInvariant()} {item.Name}";
    }
}
