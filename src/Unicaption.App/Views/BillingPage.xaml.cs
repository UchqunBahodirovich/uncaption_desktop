using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Unicaption.App.Controls;
using Unicaption.Core.Api;
using Unicaption.Core.Catalog;

namespace Unicaption.App.Views;

/// <summary>
/// BILL 01/02 MVP scope: view the account and cancel with confirmation, pending, error and
/// reload. Checkout/top-up CTAs stay hidden until price_ids and the payment contract exist,
/// so no button on this page is a dead end (AC 28).
/// </summary>
public sealed partial class BillingPage : Page
{
    private readonly AppServices _s = AppServices.Current;

    public BillingPage()
    {
        InitializeComponent();
        PlanCards.SetBreakpoints((980, 4), (680, 2), (0, 1));
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
        var plan = acc.Plan;
        PlanName.Text = plan?.PlanName ?? (acc.State == AccountLoadState.Error ? "Unavailable" : "Loading…");
        PlanUsage.Text = plan is null
            ? acc.ErrorMessage ?? ""
            : $"Used: {plan.UsedMinutes:0.#} / {(plan.LimitMinutes is { } l ? $"{l:0.#}" : "∞")} min";
        PlanExpiry.Text = plan?.ExpiresAt is { } exp ? $"Expires: {exp.LocalDateTime:d}" : "";

        var paid = plan is not null && !plan.PlanName.Equals("Free", StringComparison.OrdinalIgnoreCase);
        ActiveBadge.Visibility = paid && plan!.IsActive ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = paid && plan!.IsActive ? Visibility.Visible : Visibility.Collapsed;

        if (acc.OnDemand is { HasBalance: true } od)
        {
            OnDemandCard.Visibility = Visibility.Visible;
            OnDemandBalance.Text = $"${od.BalanceDollars:0.00}";
            OnDemandDetail.Text = $"${od.UsedDollars:0.00} used of ${od.TotalDollars:0.00}";
        }
        else
        {
            OnDemandCard.Visibility = Visibility.Collapsed;
        }

        RenderPlans(plan?.PlanName);
    }

    private void RenderPlans(string? current)
    {
        PlanCards.Children.Clear();
        foreach (var p in Catalogs.Plans) PlanCards.Children.Add(PlanCard(p, current));
        PlanCards.Reflow();
    }

    private FrameworkElement PlanCard(PlanDefinition p, string? currentPlan)
    {
        var annual = AnnualSwitch.IsOn;
        var isCurrent = string.Equals(p.Name, currentPlan, StringComparison.OrdinalIgnoreCase);
        var stack = new StackPanel { Spacing = 4 };

        var header = new Grid();
        header.Children.Add(new TextBlock { Text = p.Name, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.ExtraBold, Foreground = Ui.ThemeBrush(this, "UcTextPrimary") });
        if (isCurrent || p.MostPopular)
        {
            header.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 3, 8, 3),
                Background = Ui.ThemeBrush(this, isCurrent ? "UcSuccessSurface" : "UcBrandSurface"),
                Child = new TextBlock
                {
                    Text = isCurrent ? "Current" : "MOST POPULAR",
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = Ui.ThemeBrush(this, isCurrent ? "UcSuccess" : "UcBrand"),
                },
            });
        }
        stack.Children.Add(header);
        stack.Children.Add(new TextBlock { Text = p.Description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Ui.ThemeBrush(this, "UcTextMuted") });

        var price = annual ? p.AnnualUsdPerMonth : p.MonthlyUsd;
        if (annual && p.AnnualUsdPerMonth != p.MonthlyUsd)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"${p.MonthlyUsd}",
                FontSize = 14,
                TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough,
                Foreground = Ui.ThemeBrush(this, "UcTextMuted"),
                Margin = new Thickness(0, 12, 0, 0),
            });
        }
        var priceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, annual ? 0 : 16, 0, 0) };
        priceRow.Children.Add(new TextBlock { Text = $"${price}", FontSize = 32, FontWeight = Microsoft.UI.Text.FontWeights.Black, Foreground = Ui.ThemeBrush(this, "UcTextPrimary") });
        priceRow.Children.Add(new TextBlock { Text = annual ? "/mo, billed yearly" : "/mo", FontSize = 14, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(2, 0, 0, 6), Foreground = Ui.ThemeBrush(this, "UcTextMuted") });
        stack.Children.Add(priceRow);

        if (isCurrent)
        {
            stack.Children.Add(new Button
            {
                Content = "Current Plan",
                IsEnabled = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Style = (Style)Application.Current.Resources["UcSecondaryButton"],
                Margin = new Thickness(0, 16, 0, 8),
            });
        }
        else
        {
            stack.Children.Add(new Border { Height = 16 });
        }

        foreach (var f in p.Features)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new FontIcon { Glyph = "", FontSize = 12, Foreground = Ui.ThemeBrush(this, "UcSuccess"), FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["UcIcons"] });
            row.Children.Add(new TextBlock { Text = f, FontSize = 13, Foreground = Ui.ThemeBrush(this, "UcTextSecondary") });
            stack.Children.Add(row);
        }

        return new Border
        {
            Padding = new Thickness(22),
            CornerRadius = new CornerRadius(16),
            Background = Ui.ThemeBrush(this, "UcSurfaceCard"),
            BorderBrush = Ui.ThemeBrush(this, isCurrent ? "UcBrand" : "UcBorderSubtle"),
            BorderThickness = new Thickness(isCurrent ? 2 : 1),
            Child = stack,
        };
    }

    private void OnPeriodToggled(object sender, RoutedEventArgs e) => RenderPlans(_s.Account.Plan?.PlanName);

    private async void OnCancelSubscription(object sender, RoutedEventArgs e)
    {
        var expires = _s.Account.Plan?.ExpiresAt is { } d ? $" (until {d.LocalDateTime:d})" : "";
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Cancel subscription?",
            Content = $"Your plan will stay active until the end of the billing period{expires}.",
            PrimaryButtonText = "Cancel subscription",
            CloseButtonText = "Keep plan",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await Ui.ShowThemed(confirm, this) != ContentDialogResult.Primary) return;

        CancelButton.IsEnabled = false;
        CancelProgress.IsActive = true;
        try
        {
            await _s.Api.CancelSubscriptionAsync();
            await _s.Account.RefreshAsync(); // end date comes from the server
            Show(InfoBarSeverity.Success, "Subscription cancelled. Your plan remains active until the end of the period.");
        }
        catch (Exception ex)
        {
            _s.Diagnostics.Warn("billing.cancel_failed", ("type", ex.GetType().Name));
            Show(InfoBarSeverity.Error, "Could not cancel the subscription. " + ErrorText.For(ex));
        }
        finally
        {
            CancelButton.IsEnabled = true;
            CancelProgress.IsActive = false;
        }
    }

    private void Show(InfoBarSeverity severity, string message)
    {
        MessageBar.Severity = severity;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
    }

    private void OnBack(object sender, RoutedEventArgs e) => App.MainWindow.GoBack();

    private void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        App.MainWindow.GoBack();
    }
}
