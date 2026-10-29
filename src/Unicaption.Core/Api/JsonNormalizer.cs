using System.Globalization;
using System.Text.Json.Nodes;

namespace Unicaption.Core.Api;

/// <summary>
/// The single normalisation layer for backend JSON (DATA 02). The UI never parses
/// raw payloads. The deployed backend's field names come first (verified against
/// views.py and live responses); older aliases are kept for compatibility.
/// </summary>
public static class JsonNormalizer
{
    public static UserPlan ParsePlan(JsonObject json)
    {
        // minutes_limit already includes on-demand minutes (subscription limit + ondemand_remaining).
        var limitMinutes = OptionalNumber(json, "minutes_limit", "limit_minutes", "limit");
        var limitTranslations = OptionalNumber(json, "limit_translations", "translations_limit");
        var used = OptionalNumber(json, "minutes_used", "used_minutes", "used") ?? 0;
        var usedTr = OptionalNumber(json, "used_translations", "translations_used") ?? 0;
        if (limitMinutes < 0 || limitTranslations < 0 || used < 0 || usedTr < 0)
            throw new ApiSchemaException("Plan contains negative values.");

        return new UserPlan
        {
            PlanName = OptionalString(json, "plan", "plan_name") ?? "Free",
            UsedMinutes = used,
            LimitMinutes = limitMinutes,
            ExpiresAt = ParseDate(OptionalString(json, "expiry_date", "expires_at")),
            IsActive = OptionalBool(json, "is_active")
                       ?? OptionalString(json, "status") is null or "active" or "trialing",
            UsedTranslations = usedTr,
            LimitTranslations = limitTranslations,
        };
    }

    public static OnDemandUsage ParseOnDemand(JsonObject json)
    {
        var balance = OptionalNumber(json, "balance", "balance_dollars") ?? 0;
        var used = OptionalNumber(json, "used_dollars", "used_amount") ?? 0;
        var total = OptionalNumber(json, "total_dollars", "total_amount") ?? 0;
        var remaining = OptionalNumber(json, "ondemand_remaining", "remaining_minutes", "remaining");
        if (balance < 0 || used < 0 || total < 0 || remaining < 0)
            throw new ApiSchemaException("On-demand usage contains negative values.");

        return new OnDemandUsage
        {
            BalanceDollars = balance,
            UsedDollars = used,
            TotalDollars = total > 0 ? total : balance + used,
            RemainingMinutes = remaining,
        };
    }

    public static ContextTerm ParseTerm(JsonObject json) => new(
        OptionalString(json, "term", "text") ?? "",
        OptionalString(json, "description", "category"));

    public static IReadOnlyList<ContextTerm> ParseTerms(JsonObject json)
    {
        var list = FirstArray(json, "terms", "results", "data");
        return list?.OfType<JsonObject>().Select(ParseTerm).Where(t => t.Term.Length > 0).ToList()
               ?? (IReadOnlyList<ContextTerm>)Array.Empty<ContextTerm>();
    }

    public static string ParseTranslation(JsonObject json) =>
        OptionalString(json, "translatedText", "translated_text", "translation") ?? "";

    public static string ParseCheckoutUrl(JsonObject json) =>
        OptionalString(json, "url", "session_url") ?? "";

    public static JsonArray? FirstArray(JsonObject json, params string[] keys)
    {
        foreach (var k in keys)
            if (json[k] is JsonArray a) return a;
        return null;
    }

    internal static double? OptionalNumber(JsonObject json, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!json.TryGetPropertyValue(k, out var node) || node is null) continue;
            if (node is JsonValue v)
            {
                if (v.TryGetValue<double>(out var d)) return d;
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<string>(out var s))
                {
                    if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return p;
                    continue; // e.g. "unlimited" → treated as no finite value
                }
            }
            throw new ApiSchemaException($"Field '{k}' is not a number.");
        }
        return null;
    }

    internal static string? OptionalString(JsonObject json, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (!json.TryGetPropertyValue(k, out var node) || node is null) continue;
            var s = node is JsonValue v && v.TryGetValue<string>(out var str) ? str : node.ToJsonString();
            if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
        }
        return null;
    }

    internal static bool? OptionalBool(JsonObject json, string key) =>
        json[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static DateTimeOffset? ParseDate(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
