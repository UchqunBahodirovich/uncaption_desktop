using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Unicaption.App.Controls;
using Unicaption.Core.Api;

namespace Unicaption.App.Views;

/// <summary>
/// ACCOUNT 01: loading / loaded / cached / error + retry. Failures never turn into a fake
/// Free plan or zero balance: the last good values stay, marked as cached (AC 27).
/// </summary>
public sealed partial class UsagePage : Page
{
    private readonly AppServices _s = AppServices.Current;

    public UsagePage()
    {
        InitializeComponent();
        UsageCards.SetBreakpoints((760, 2), (0, 1));
        MetricCards.SetBreakpoints((760, 3), (0, 1));
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _s.Account.Changed += OnAccountChanged;
        Render();
        _ = _s.Account.RefreshAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => _s.Account.Changed -= OnAccountChanged;

    private void OnAccountChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        var acc = _s.Account;
        BusyRing.IsActive = acc.State == AccountLoadState.Loading;

        StateBar.IsOpen = acc.State is AccountLoadState.Error or AccountLoadState.Cached;
        StateBar.Severity = acc.State == AccountLoadState.Error ? InfoBarSeverity.Error : InfoBarSeverity.Warning;
        StateBar.Message = acc.State == AccountLoadState.Cached
            ? $"Showing data from {acc.LoadedAt:t}. {acc.ErrorMessage}"
            : acc.ErrorMessage ?? "";

        var plan = acc.Plan;
        PlanName.Text = plan?.PlanName ?? (acc.State == AccountLoadState.Error ? "Unavailable" : "Loading…");
        PlanExpiry.Text = plan?.ExpiresAt is { } exp ? $"Renews or expires on {exp.LocalDateTime:d}" : "";

        if (plan is not null)
        {
            VoiceValue.Text = plan.LimitHours is { } lh ? $"{plan.UsedHours:0.#} / {lh:0.#} h" : $"{plan.UsedHours:0.#} h";
            VoiceBar.Value = plan.MinutesProgress ?? 0;
            VoiceBar.Foreground = Ui.ThemeBrush(this, plan.IsMinutesLimitReached ? "UcError" : "UcBrand");
            VoiceRemaining.Text = plan.IsMinutesLimitReached
                ? "Limit reached"
                : plan.LimitMinutes is { } lm ? $"{Math.Max(0, lm - plan.UsedMinutes) / 60:0.#} h remaining" : "No fixed limit";

            TranslationsValue.Text = plan.LimitTranslations is { } lt ? $"{plan.UsedTranslations:N0} / {lt:N0}" : $"{plan.UsedTranslations:N0}";
            TranslationsBar.Value = plan.TranslationsProgress ?? 0;
            TranslationsBar.Foreground = Ui.ThemeBrush(this, "UcBrand");
            TranslationsRemaining.Text = plan.LimitTranslations is null ? "No fixed limit" : "";
        }
        else
        {
            VoiceValue.Text = TranslationsValue.Text = "—";
            VoiceRemaining.Text = TranslationsRemaining.Text = "";
        }

        if (acc.OnDemand is { HasBalance: true } od)
        {
            OnDemandCard.Visibility = Visibility.Visible;
            OnDemandBalance.Text = $"${od.BalanceDollars:0.00} left";
            OnDemandDetail.Text = $"${od.UsedDollars:0.00} used of ${od.TotalDollars:0.00}" +
                                  (od.RemainingMinutes is { } rm ? $" · ≈{rm / 60:0.#} h remaining" : "");
            OnDemandBar.Value = od.UsedPercent;
            OnDemandBar.Foreground = Ui.ThemeBrush(this, "UcBrand");
        }
        else
        {
            OnDemandCard.Visibility = Visibility.Collapsed;
        }

        var stats = acc.Stats;
        SessionsValue.Text = stats is null ? "—" : stats.SessionsCount.ToString("N0");
        MinutesValue.Text = stats is null ? "—" : stats.TotalMinutes.ToString("N0");
        StreakValue.Text = stats is null ? "—" : $"{stats.ActiveStreak} d";
    }

    private void OnBack(object sender, RoutedEventArgs e) => App.MainWindow.GoBack();

    private void OnUpgrade(object sender, RoutedEventArgs e) => App.MainWindow.ShowBilling();

    private void OnRetry(object sender, RoutedEventArgs e) => _ = _s.Account.RefreshAsync();

    private void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        App.MainWindow.GoBack();
    }
}
