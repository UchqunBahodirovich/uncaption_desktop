namespace Unicaption.Core.Api;

/// <summary>View models from DATA 01. All JSON variants are normalised in <see cref="JsonNormalizer"/>.</summary>
public sealed record UserProfile(string Uid, string Email, string? DisplayName);

public sealed record UserPlan
{
    public required string PlanName { get; init; }
    public double UsedMinutes { get; init; }
    /// <summary>null = unknown or unlimited (never assumed to be 0).</summary>
    public double? LimitMinutes { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool IsActive { get; init; } = true;
    public double UsedTranslations { get; init; }
    public double? LimitTranslations { get; init; }

    public double UsedHours => UsedMinutes / 60;
    public double? LimitHours => LimitMinutes / 60;

    /// <summary>0..1 progress, or null when there is no finite limit.</summary>
    public double? MinutesProgress =>
        LimitMinutes is > 0 ? Math.Clamp(UsedMinutes / LimitMinutes.Value, 0, 1) : null;

    public double? TranslationsProgress =>
        LimitTranslations is > 0 ? Math.Clamp(UsedTranslations / LimitTranslations.Value, 0, 1) : null;

    public bool IsMinutesLimitReached => LimitMinutes is > 0 && UsedMinutes >= LimitMinutes.Value;
}

public sealed record OnDemandUsage
{
    public double BalanceDollars { get; init; }
    public double UsedDollars { get; init; }
    public double TotalDollars { get; init; }
    /// <summary>null when the server does not report it; no hardcoded price estimate (DATA 02).</summary>
    public double? RemainingMinutes { get; init; }

    public bool HasBalance => TotalDollars > 0 || BalanceDollars > 0;
    public double UsedPercent => TotalDollars <= 0 ? 0 : Math.Clamp(UsedDollars / TotalDollars, 0, 1);
}

public sealed record UserStats(int SessionsCount, double TotalMinutes, int ActiveStreak);

public sealed record ContextTerm(string Term, string? Description);

public sealed record UsageReport(int Seconds, int WordsCount);
