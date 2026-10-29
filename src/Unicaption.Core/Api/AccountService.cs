using Unicaption.Core.Diagnostics;

namespace Unicaption.Core.Api;

public enum AccountLoadState { NotLoaded, Loading, Loaded, Cached, Error }

/// <summary>
/// Plan, on-demand balance and stats with explicit loading/loaded/cached/error states
/// (ACCOUNT 01). On failure the last good values stay visible as "cached"; the client
/// never substitutes a fake Free plan or zero balance.
/// </summary>
public sealed class AccountService
{
    private readonly ApiClient _api;
    private readonly IDiagnostics _diag;
    private int _generation;

    public AccountService(ApiClient api, IDiagnostics diag)
    {
        _api = api;
        _diag = diag;
    }

    public AccountLoadState State { get; private set; } = AccountLoadState.NotLoaded;
    public UserPlan? Plan { get; private set; }
    public OnDemandUsage? OnDemand { get; private set; }
    public UserStats? Stats { get; private set; }
    public string? ErrorMessage { get; private set; }
    public DateTimeOffset? LoadedAt { get; private set; }

    public event EventHandler? Changed;

    public void Reset()
    {
        _generation++;
        Plan = null;
        OnDemand = null;
        Stats = null;
        ErrorMessage = null;
        LoadedAt = null;
        State = AccountLoadState.NotLoaded;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var gen = ++_generation;
        State = AccountLoadState.Loading;
        ErrorMessage = null;
        Changed?.Invoke(this, EventArgs.Empty);

        var plan = Capture(_api.GetPlanAsync(ct));
        var onDemand = Capture(_api.GetOnDemandAsync(ct));
        var stats = Capture(_api.GetStatsAsync(ct));
        await Task.WhenAll(plan, onDemand, stats).ConfigureAwait(true);
        if (gen != _generation) return; // signed out or superseded meanwhile

        Exception? firstError = null;
        if (plan.Result.Error is null) Plan = plan.Result.Value; else firstError ??= plan.Result.Error;
        if (onDemand.Result.Error is null) OnDemand = onDemand.Result.Value; else firstError ??= onDemand.Result.Error;
        if (stats.Result.Error is null) Stats = stats.Result.Value; else firstError ??= stats.Result.Error;

        if (firstError is null)
        {
            State = AccountLoadState.Loaded;
            LoadedAt = DateTimeOffset.Now;
        }
        else
        {
            _diag.Warn("account.refresh_failed", ("type", firstError.GetType().Name));
            ErrorMessage = ErrorText.For(firstError);
            State = Plan is not null ? AccountLoadState.Cached : AccountLoadState.Error;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Client-side hint only; the server remains the authority on limits.</summary>
    public bool IsOutOfMinutes =>
        Plan is { IsMinutesLimitReached: true } && !(OnDemand is { BalanceDollars: > 0 } || OnDemand?.RemainingMinutes > 0);

    private static async Task<(T? Value, Exception? Error)> Capture<T>(Task<T> task)
    {
        try
        {
            return (await task.ConfigureAwait(false), null);
        }
        catch (Exception e)
        {
            return (default, e);
        }
    }
}
