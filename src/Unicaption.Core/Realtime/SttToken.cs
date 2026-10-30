using System.Text.Json.Nodes;
using Unicaption.Core.Api;
using Unicaption.Core.Audio;

namespace Unicaption.Core.Realtime;

public enum SttTokenType { Transcription, Translation }

/// <param name="Speaker">0-based internal speaker index (UI shows S1, S2…).</param>
public sealed record SttToken(string Text, bool IsFinal, SttTokenType Type, int Speaker, string? Language);

/// <summary>Token parsing rules from RT 02. Malformed entries are skipped, never thrown.</summary>
public static class TokenParser
{
    public static IReadOnlyList<SttToken> Parse(JsonArray tokens)
    {
        var batch = new List<SttToken>(tokens.Count);
        foreach (var node in tokens)
        {
            if (node is not JsonObject t) continue;
            var text = AsString(t["text"]);
            if (string.IsNullOrWhiteSpace(text)) continue;

            var isFinal = t["is_final"] is JsonValue fv && fv.TryGetValue<bool>(out var f) ? f : true;
            var status = AsString(t["translation_status"]) ?? "transcription";
            var rawSpeaker = AsString(t["speaker"]) ?? AsString(t["speaker_id"]);
            var speaker = rawSpeaker is null ? 0 : (int.TryParse(rawSpeaker, out var s) ? s : 1) - 1;
            var lang = AsString(t["language"]) ?? AsString(t["language_code"]) ?? AsString(t["lang"]);

            batch.Add(new SttToken(
                text,
                isFinal,
                status == "translation" ? SttTokenType.Translation : SttTokenType.Transcription,
                Math.Clamp(speaker, 0, 99),
                NormalizeLanguage(lang)));
        }
        return batch;
    }

    public static string? NormalizeLanguage(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return null;
        return lang.Trim().ToLowerInvariant().Split('-', '_')[0];
    }

    private static string? AsString(JsonNode? n) => n switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v => v.ToJsonString(),
        _ => null,
    };
}

/// <summary>
/// Builds the config message from RT 01, matched to the deployed consumer (customers.py):
/// it reads <c>language</c> first and only falls back to <c>language_hints</c>, so with hints the
/// list (primary first) is sent and <c>language</c> is left out. <c>context</c> is forwarded to
/// Soniox unchanged: the domain goes in <c>general</c>, Custom Context in <c>terms</c> (both
/// verified against the live service). The raw-PCM fields describe what the app streams; the
/// consumer still sends <c>audio_format: auto</c> upstream until it honours them.
/// </summary>
public sealed record RealtimeConfig(string Model, string SourceLanguage, string TargetLanguage,
    IReadOnlyList<string>? LanguageHints = null, string? Domain = null, IReadOnlyList<string>? ContextTerms = null)
{
    /// <summary>Catalog value meaning "no domain"; it is not sent.</summary>
    public const string GeneralDomain = "General";

    public string ToJson()
    {
        var json = new JsonObject
        {
            ["type"] = "config",
            ["model"] = Model,
            ["audio_format"] = "pcm_s16le",
            ["sample_rate"] = AudioFormat.SampleRate,
            ["num_channels"] = AudioFormat.Channels,
            ["enable_speaker_diarization"] = true,
            ["enable_streaming_translation"] = true,
            ["translation"] = new JsonObject
            {
                ["type"] = "two_way",
                ["language_a"] = SourceLanguage,
                ["language_b"] = TargetLanguage,
            },
        };
        var hints = (LanguageHints ?? []).Where(h => h != SourceLanguage).Distinct().ToList();
        if (hints.Count > 0)
        {
            var array = new JsonArray { SourceLanguage };
            foreach (var h in hints) array.Add(h);
            json["language_hints"] = array;
        }
        else
        {
            json["language"] = SourceLanguage;
        }

        var terms = (ContextTerms ?? []).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        var domain = string.IsNullOrWhiteSpace(Domain) || Domain == GeneralDomain ? null : Domain;
        if (domain is not null || terms.Count > 0)
        {
            var context = new JsonObject();
            if (domain is not null)
                context["general"] = new JsonArray { new JsonObject { ["key"] = "domain", ["value"] = domain } };
            if (terms.Count > 0)
            {
                var list = new JsonArray();
                foreach (var t in terms) list.Add(t);
                context["terms"] = list;
            }
            json["context"] = context;
        }
        return json.ToJsonString();
    }

    internal static string AuthJson(string token) =>
        new JsonObject { ["type"] = "auth", ["token"] = token }.ToJsonString();

    internal static JsonObject? TryParseMessage(string raw)
    {
        try
        {
            return JsonNode.Parse(raw) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    internal static string? TypeOf(JsonObject msg) => JsonNormalizer.OptionalString(msg, "type");
}
