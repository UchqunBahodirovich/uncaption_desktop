namespace Unicaption.Core.Catalog;

public sealed record Language(string Code, string Name);

public enum Tier { Free, Pro, Ultra }

public sealed record ModelOption(string Name, string Subtitle, Tier Tier);

public sealed record DomainOption(string Name, string Subtitle, Tier Tier);

public sealed record PlanDefinition(string Name, int MonthlyUsd, int AnnualUsdPerMonth, string Description,
    bool MostPopular, IReadOnlyList<string> Features);

/// <summary>Static UI catalogues ported from demo_data.dart / billing_screen.dart.</summary>
public static class Catalogs
{
    public static readonly IReadOnlyList<Language> Languages =
    [
        new("af", "Afrikaans"), new("sq", "Albanian"), new("ar", "Arabic"), new("az", "Azerbaijani"),
        new("eu", "Basque"), new("be", "Belarusian"), new("bn", "Bengali"), new("bs", "Bosnian"),
        new("bg", "Bulgarian"), new("ca", "Catalan"), new("zh", "Chinese"), new("hr", "Croatian"),
        new("cs", "Czech"), new("da", "Danish"), new("nl", "Dutch"), new("en", "English"),
        new("et", "Estonian"), new("fil", "Filipino"), new("fi", "Finnish"), new("fr", "French"),
        new("gl", "Galician"), new("ka", "Georgian"), new("de", "German"), new("el", "Greek"),
        new("gu", "Gujarati"), new("he", "Hebrew"), new("hi", "Hindi"), new("hu", "Hungarian"),
        new("is", "Icelandic"), new("id", "Indonesian"), new("it", "Italian"), new("ja", "Japanese"),
        new("kn", "Kannada"), new("kk", "Kazakh"), new("ko", "Korean"), new("lv", "Latvian"),
        new("lt", "Lithuanian"), new("mk", "Macedonian"), new("ms", "Malay"), new("mt", "Maltese"),
        new("mr", "Marathi"), new("no", "Norwegian"), new("fa", "Persian"), new("pl", "Polish"),
        new("pt", "Portuguese"), new("pa", "Punjabi"), new("ro", "Romanian"), new("ru", "Russian"),
        new("sr", "Serbian"), new("sk", "Slovak"), new("sl", "Slovenian"), new("es", "Spanish"),
        new("sw", "Swahili"), new("sv", "Swedish"), new("ta", "Tamil"), new("te", "Telugu"),
        new("th", "Thai"), new("tr", "Turkish"), new("uk", "Ukrainian"), new("ur", "Urdu"),
        new("vi", "Vietnamese"), new("cy", "Welsh"),
    ];

    public static string LanguageName(string code) =>
        Languages.FirstOrDefault(l => l.Code == code)?.Name ?? code.ToUpperInvariant();

    /// <summary>UI labels only; not server model ids (WS 02). Shown as "Coming soon" until mapped.</summary>
    public static readonly IReadOnlyList<ModelOption> Models =
    [
        new("Apex Gen-1", "Fast, reliable transcription", Tier.Free),
        new("Apex Gen-2", "Higher accuracy, complex audio", Tier.Pro),
        new("Apex Gen-2.5", "Max quality, AI post-processing", Tier.Ultra),
    ];

    public static readonly IReadOnlyList<DomainOption> Domains =
    [
        new("General", "Works across all domains", Tier.Free),
        new("Healthcare", "Medical terminology", Tier.Pro),
        new("Legal", "Legal & court terminology", Tier.Pro),
        new("Business", "Corporate & finance terms", Tier.Pro),
        new("Education", "Academic language", Tier.Pro),
        new("Public Safety", "Emergency & 911 context", Tier.Pro),
    ];

    public static readonly IReadOnlyList<string> ContextSuggestions =
    [
        "triage", "dispatch", "EMS", "CPR", "AED", "EMT", "paramedic", "incident command", "hazmat",
        "code 3", "10-4", "APB", "BOLO",
    ];

    public static readonly IReadOnlyList<(string Label, double Scale)> TextSizes =
    [
        ("Compact", 0.85), ("Default", 1.0), ("Comfortable", 1.15), ("Large", 1.30),
    ];

    /// <summary>Static plan copy (BILL 01). Not authoritative prices; server/remote config must replace it.</summary>
    public static readonly IReadOnlyList<PlanDefinition> Plans =
    [
        new("Instant", 9, 7, "2 hours free trial, then monthly.", false,
            ["2 hours free trial", "Apex Gen-1 model", "5,000 translations", "18 hours/month"]),
        new("Basic", 38, 29, "Perfect for freelance interpreters.", false,
            ["Everything in Instant +", "Apex Gen-2 model", "15,000 translations", "80 hours/month"]),
        new("Pro", 65, 55, "For professionals & agencies.", true,
            ["Everything in Basic +", "Apex Gen-2 model", "220 hours/month", "50,000 translations", "Live AI Translation"]),
        new("Ultra", 105, 90, "Maximum power for heavy users.", false,
            ["Everything in Pro +", "Apex Gen-2.5 model", "450 hours/month", "Unlimited translations"]),
    ];
}
