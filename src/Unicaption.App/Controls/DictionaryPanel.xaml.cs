using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Unicaption.Core.Api;
using Unicaption.Core.Catalog;
using Windows.System;

namespace Unicaption.App.Controls;

/// <summary>
/// WS 05: one-shot translate lookup. Submit (Enter) or 300 ms debounce; loading, result,
/// empty, error + retry. A newer query always wins: older responses are discarded.
/// </summary>
public sealed partial class DictionaryPanel : UserControl
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);
    private CancellationTokenSource? _pending;
    private int _requestId;
    private string _source = "en";
    private string _target = "ru";

    public DictionaryPanel() => InitializeComponent();

    public event EventHandler? CloseRequested;

    public void SetLanguages(string source, string target)
    {
        _source = source;
        _target = target;
        DirectionText.Text = $"{Catalogs.LanguageName(source)} → {Catalogs.LanguageName(target)}";
    }

    public void FocusQuery() => Query.Focus(FocusState.Programmatic);

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnQueryChanged(object sender, TextChangedEventArgs e) => _ = SearchAsync(debounce: true);

    private void OnQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        _ = SearchAsync(debounce: false);
    }

    private void OnRetry(object sender, RoutedEventArgs e) => _ = SearchAsync(debounce: false);

    private async Task SearchAsync(bool debounce)
    {
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        var id = ++_requestId;
        var text = Query.Text.Trim();

        if (text.Length == 0)
        {
            Show(empty: true);
            return;
        }
        try
        {
            if (debounce) await Task.Delay(Debounce, cts.Token);
            Show(loading: true);
            var result = await AppServices.Current.Api.TranslateAsync(text, _source, _target, cts.Token);
            if (id != _requestId) return; // stale response
            if (string.IsNullOrWhiteSpace(result))
            {
                EmptyText.Text = "No translation found";
                Show(empty: true);
                return;
            }
            ResultSource.Text = text;
            ResultText.Text = result;
            Show(result: true);
        }
        catch (OperationCanceledException)
        {
            // superseded by a newer query
        }
        catch (Exception ex)
        {
            if (id != _requestId) return;
            AppServices.Current.Diagnostics.Warn("dictionary.failed", ("type", ex.GetType().Name));
            ErrorMessage.Text = ErrorText.For(ex);
            Show(error: true);
        }
    }

    private void Show(bool empty = false, bool loading = false, bool error = false, bool result = false)
    {
        if (empty && Query.Text.Trim().Length == 0) EmptyText.Text = "Search for a word to see its translation";
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        BusyRing.IsActive = loading;
        BusyRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ErrorState.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        ResultCard.Visibility = result ? Visibility.Visible : Visibility.Collapsed;
    }
}
