using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Unicaption.App.Controls;
using Unicaption.Core.Api;
using Unicaption.Core.Auth;

namespace Unicaption.App.Views;

/// <summary>AUTH 01: Google sign-in in the system browser; cancel returns to the idle login state.</summary>
public sealed partial class LoginPage : Page
{
    private CancellationTokenSource? _cts;

    public LoginPage()
    {
        InitializeComponent();
        Motion.AttachShowHide(Card, 0, 12); // seen once per sign-in, so it may arrive gently
        var cfg = AppServices.Current.Config;
        // UI 06: links without a configured URL are not shown as active links.
        TermsLink.Visibility = string.IsNullOrEmpty(cfg.TermsUrl) ? Visibility.Collapsed : Visibility.Visible;
        PrivacyLink.Visibility = string.IsNullOrEmpty(cfg.PrivacyUrl) ? Visibility.Collapsed : Visibility.Visible;
        AndText.Visibility = TermsLink.Visibility == Visibility.Visible && PrivacyLink.Visibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (TermsLink.Visibility == Visibility.Collapsed && PrivacyLink.Visibility == Visibility.Collapsed)
            LegalPanel.Visibility = Visibility.Collapsed;
    }

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        var s = AppServices.Current;
        SetBusy(true);
        _cts = new CancellationTokenSource();
        try
        {
            var user = await s.Auth.SignInAsync(_cts.Token);
            try
            {
                await s.Api.LoginSyncAsync(user);
            }
            catch (Exception ex)
            {
                s.Diagnostics.Warn("auth.login_sync_failed", ("type", ex.GetType().Name));
            }
            s.Diagnostics.Info("auth.signed_in");
            App.MainWindow.ShowPlayground();
        }
        catch (AuthCancelledException)
        {
            ShowError("Sign in cancelled.");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("not configured"))
        {
            ShowError("Google sign-in is not configured for this build.");
        }
        catch (Exception ex)
        {
            s.Diagnostics.Error("auth.sign_in_failed", ex);
            ShowError(ex is ApiException or HttpRequestException or TimeoutException
                ? "Could not sign in. Check your connection and try again."
                : "Sign in failed. Please try again.");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnTerms(object sender, RoutedEventArgs e) => Open(AppServices.Current.Config.TermsUrl);

    private void OnPrivacy(object sender, RoutedEventArgs e) => Open(AppServices.Current.Config.PrivacyUrl);

    private static void Open(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) AppServices.Current.Browser.Open(uri);
    }

    private void SetBusy(bool busy)
    {
        GoogleButton.IsEnabled = !busy;
        GoogleLabel.Text = busy ? "Signing in..." : "Continue with Google";
        CancelLink.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) ErrorText.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }
}
