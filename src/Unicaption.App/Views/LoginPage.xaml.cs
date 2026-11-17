using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Unicaption.App.Controls;
using Unicaption.Core.Api;
using Unicaption.Core.Auth;
using Windows.System;

namespace Unicaption.App.Views;

/// <summary>
/// AUTH 01: email + password (sign in, create account, reset) and Google in the system browser.
/// Google is offered only when the build has a Desktop OAuth client. Cancel returns to idle.
/// </summary>
public sealed partial class LoginPage : Page
{
    private CancellationTokenSource? _cts;
    private bool _signUp;

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

        var google = AppServices.Current.Auth.IsGoogleConfigured ? Visibility.Visible : Visibility.Collapsed;
        GoogleButton.Visibility = OrDivider.Visibility = google;
        ApplyMode();
    }

    // ═══ Email + password ════════════════════════════════════════════════════

    private void OnToggleMode(object sender, RoutedEventArgs e)
    {
        _signUp = !_signUp;
        ApplyMode();
        (_signUp ? NameBox : EmailBox).Focus(FocusState.Keyboard);
    }

    private void ApplyMode()
    {
        NameBox.Visibility = _signUp ? Visibility.Visible : Visibility.Collapsed;
        ForgotLink.Visibility = _signUp ? Visibility.Collapsed : Visibility.Visible;
        EmailLabel.Text = _signUp ? "Create account" : "Sign in";
        AutomationProperties.SetName(EmailButton, EmailLabel.Text);
        PasswordInput.PlaceholderText = _signUp ? $"Password (min. {AuthService.MinPasswordLength} characters)" : "Password";
        ModePrompt.Text = _signUp ? "Already have an account?" : "New to Unicaption?";
        ModeLink.Content = _signUp ? "Sign in" : "Create account";
        ClearMessages();
    }

    private void OnFieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        OnEmailSubmit(sender, e);
    }

    private async void OnEmailSubmit(object sender, RoutedEventArgs e)
    {
        if (!EmailButton.IsEnabled) return;
        var email = EmailBox.Text.Trim();
        var password = PasswordInput.Password;
        if (!LooksLikeEmail(email))
        {
            ShowError("Enter a valid email address.");
            EmailBox.Focus(FocusState.Keyboard);
            return;
        }
        if (password.Length == 0)
        {
            ShowError("Enter your password.");
            PasswordInput.Focus(FocusState.Keyboard);
            return;
        }

        var s = AppServices.Current;
        SetBusy(true, emailFlow: true);
        try
        {
            var user = _signUp
                ? await s.Auth.SignUpWithPasswordAsync(email, password, NameBox.Text, CancellationToken.None)
                : await s.Auth.SignInWithPasswordAsync(email, password, CancellationToken.None);
            s.Diagnostics.Info("auth.signed_in", ("method", _signUp ? "email_signup" : "email"));
            await FinishSignInAsync(user);
        }
        catch (AuthFailedException ex)
        {
            s.Diagnostics.Warn("auth.email_failed", ("code", ex.Code));
            ShowError(ex.Message);
            PasswordInput.Password = "";
        }
        catch (Exception ex)
        {
            s.Diagnostics.Error("auth.email_failed", ex);
            ShowError(NetworkOrGeneric(ex));
        }
        finally
        {
            SetBusy(false, emailFlow: true);
        }
    }

    private async void OnForgot(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        if (!LooksLikeEmail(email))
        {
            ShowError("Enter your email above, then choose \"Forgot password?\" again.");
            EmailBox.Focus(FocusState.Keyboard);
            return;
        }
        var s = AppServices.Current;
        SetBusy(true, emailFlow: true);
        try
        {
            await s.Auth.SendPasswordResetAsync(email, CancellationToken.None);
            ShowInfo($"If an account exists for {email}, a password reset link has been sent.");
        }
        catch (AuthFailedException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            s.Diagnostics.Error("auth.reset_failed", ex);
            ShowError(NetworkOrGeneric(ex));
        }
        finally
        {
            SetBusy(false, emailFlow: true);
        }
    }

    private static bool LooksLikeEmail(string email)
    {
        var at = email.IndexOf('@');
        return at > 0 && at < email.Length - 3 && email.IndexOf('.', at) > at + 1 && !email.Contains(' ');
    }

    // ═══ Google ══════════════════════════════════════════════════════════════

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        var s = AppServices.Current;
        SetBusy(true, emailFlow: false);
        _cts = new CancellationTokenSource();
        try
        {
            var user = await s.Auth.SignInAsync(_cts.Token);
            s.Diagnostics.Info("auth.signed_in", ("method", "google"));
            await FinishSignInAsync(user);
        }
        catch (AuthCancelledException)
        {
            ShowError("Sign in cancelled.");
        }
        catch (Exception ex)
        {
            s.Diagnostics.Error("auth.sign_in_failed", ex);
            ShowError(NetworkOrGeneric(ex));
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false, emailFlow: false);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ═══ Shared ══════════════════════════════════════════════════════════════

    private static async Task FinishSignInAsync(UserProfile user)
    {
        var s = AppServices.Current;
        try
        {
            await s.Api.LoginSyncAsync(user);
        }
        catch (Exception ex)
        {
            s.Diagnostics.Warn("auth.login_sync_failed", ("type", ex.GetType().Name));
        }
        App.MainWindow.ShowPlayground();
    }

    private static string NetworkOrGeneric(Exception ex) =>
        ex is ApiException or HttpRequestException or TimeoutException or TaskCanceledException
            ? "Could not sign in. Check your connection and try again."
            : "Sign in failed. Please try again.";

    private void OnTerms(object sender, RoutedEventArgs e) => Open(AppServices.Current.Config.TermsUrl);

    private void OnPrivacy(object sender, RoutedEventArgs e) => Open(AppServices.Current.Config.PrivacyUrl);

    private static void Open(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) AppServices.Current.Browser.Open(uri);
    }

    private void SetBusy(bool busy, bool emailFlow)
    {
        foreach (var c in new Control[] { NameBox, EmailBox, PasswordInput, EmailButton, GoogleButton, ForgotLink, ModeLink })
            c.IsEnabled = !busy;
        EmailLabel.Text = busy && emailFlow
            ? (_signUp ? "Creating account..." : "Please wait...")
            : (_signUp ? "Create account" : "Sign in");
        GoogleLabel.Text = busy && !emailFlow ? "Signing in..." : "Continue with Google";
        CancelLink.Visibility = busy && !emailFlow ? Visibility.Visible : Visibility.Collapsed;
        if (busy) ClearMessages();
    }

    private void ClearMessages()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        InfoText.Visibility = Visibility.Collapsed;
    }

    private void ShowError(string text)
    {
        InfoText.Visibility = Visibility.Collapsed;
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void ShowInfo(string text)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        InfoText.Text = text;
        InfoText.Visibility = Visibility.Visible;
    }
}
