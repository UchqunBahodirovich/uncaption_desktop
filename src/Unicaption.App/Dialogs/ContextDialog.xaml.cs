using Microsoft.UI.Xaml.Controls;
using Unicaption.Core.Catalog;
using Unicaption.Core.Settings;

namespace Unicaption.App.Dialogs;

/// <summary>
/// WS 03: Save commits, Cancel/Escape leaves the stored values untouched (AC 25).
/// The community catalogue is out of MVP scope and therefore not shown.
/// </summary>
public sealed partial class ContextDialog : ContentDialog
{
    public ContextDialog(WorkspaceSettings settings, string domain)
    {
        InitializeComponent();
        DomainBadge.Text = domain.ToUpperInvariant();
        SuggestedLabel.Text = $"SUGGESTED FOR {domain.ToUpperInvariant()}";
        ContextBox.Text = settings.CustomContext;
        PromptBox.Text = settings.TranslationPrompt;
        UpdateCount();

        foreach (var term in Catalogs.ContextSuggestions)
        {
            var chip = new Button { Content = "+ " + term, Style = (Microsoft.UI.Xaml.Style)Microsoft.UI.Xaml.Application.Current.Resources["UcChipButton"] };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, $"Add {term}");
            chip.Click += (_, _) => AddTerm(term);
            Chips.Children.Add(chip);
        }

        PrimaryButtonClick += (_, _) =>
        {
            settings.CustomContext = ContextBox.Text.Trim();
            settings.TranslationPrompt = PromptBox.Text.Trim();
            settings.Save();
        };
    }

    private void AddTerm(string term)
    {
        var current = ContextBox.Text.TrimEnd();
        var next = current.Length == 0 ? term : $"{current}, {term}";
        if (next.Length <= WorkspaceSettings.MaxContextLength) ContextBox.Text = next;
    }

    private void OnContextChanged(object sender, TextChangedEventArgs e) => UpdateCount();

    private void UpdateCount() => CountText.Text = $"{ContextBox.Text.Length}/{WorkspaceSettings.MaxContextLength}";
}
