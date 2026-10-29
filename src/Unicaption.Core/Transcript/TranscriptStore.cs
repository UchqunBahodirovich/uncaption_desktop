using Unicaption.Core.Realtime;
using Unicaption.Core.Time;

namespace Unicaption.Core.Transcript;

public sealed class Segment
{
    public required int Speaker { get; init; }
    public string FinalText { get; set; } = "";
    public string TempText { get; set; } = "";
    public required double CreatedAtMs { get; init; }
    public string? Language { get; set; }
    public bool Resumed { get; init; }

    /// <summary>Monotonic revision so views can skip unchanged segments.</summary>
    public int Revision { get; internal set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(FinalText + TempText);
}

public static class Rtl
{
    private static readonly HashSet<string> Languages = ["ar", "he", "fa", "ur", "yi", "dv", "ps", "sd"];

    public static bool IsRtl(string? lang) =>
        lang is not null && Languages.Contains(TokenParser.NormalizeLanguage(lang) ?? "");
}

/// <summary>One panel's segment list with the grouping rules of TEXT 02.</summary>
public sealed class TranscriptPane
{
    public const double ResumeGapMs = 3000;
    public const int MaxSegments = 50;

    private readonly List<Segment> _segments = [];
    private readonly Dictionary<int, double> _lastSeen = [];

    public IReadOnlyList<Segment> Segments => _segments;

    /// <summary>Incremented on every structural change (add/remove/clear).</summary>
    public int Version { get; private set; }

    public void Clear()
    {
        _segments.Clear();
        _lastSeen.Clear();
        Version++;
    }

    internal void ClearTemp()
    {
        foreach (var s in _segments)
        {
            if (s.TempText.Length == 0) continue;
            s.TempText = "";
            s.Revision++;
        }
    }

    internal void Add(SttToken token, double nowMs)
    {
        var speaker = token.Speaker;
        var last = _segments.Count > 0 ? _segments[^1] : null;
        var speakerChanged = last is not null && last.Speaker != speaker;
        var longPause = _lastSeen.TryGetValue(speaker, out var seen) && nowMs - seen > ResumeGapMs;
        _lastSeen[speaker] = nowMs;
        var dirChanged = token.Language is not null && last?.Language is not null &&
                         Rtl.IsRtl(token.Language) != Rtl.IsRtl(last.Language);

        if (last is null || speakerChanged || longPause || dirChanged)
        {
            if (last is not null && last.TempText.Length > 0)
            {
                last.TempText = "";
                last.Revision++;
            }
            _segments.Add(new Segment
            {
                Speaker = speaker,
                FinalText = token.IsFinal ? token.Text : "",
                TempText = token.IsFinal ? "" : token.Text,
                CreatedAtMs = nowMs,
                Language = token.Language,
                Resumed = last is not null && longPause && !speakerChanged && !dirChanged,
            });
            if (_segments.Count > MaxSegments) _segments.RemoveRange(0, _segments.Count - MaxSegments);
            Version++;
            return;
        }

        if (token.Language is not null) last.Language = token.Language;
        if (token.IsFinal)
        {
            last.FinalText += token.Text;
            last.TempText = "";
        }
        else
        {
            last.TempText += token.Text;
        }
        last.Revision++;
    }

    /// <summary>Merges consecutive same-speaker, non-resumed, same-direction segments.</summary>
    internal void MergeConsecutive()
    {
        for (var j = _segments.Count - 1; j > 0; j--)
        {
            var cur = _segments[j];
            var prev = _segments[j - 1];
            var dirChange = cur.Language is not null && prev.Language is not null &&
                            Rtl.IsRtl(cur.Language) != Rtl.IsRtl(prev.Language);
            if (cur.Speaker != prev.Speaker || cur.Resumed || dirChange) continue;
            prev.FinalText += cur.FinalText;
            prev.TempText = cur.TempText;
            if (cur.Language is not null) prev.Language = cur.Language;
            prev.Revision++;
            _segments.RemoveAt(j);
            Version++;
        }
    }
}

/// <summary>
/// TranscriptStore: routes token batches to the source/translation panes and keeps the
/// session word count independently of the UI (DATA 04): Clear and the 50-segment cap
/// do not affect usage. Not thread-safe; call from the UI thread.
/// </summary>
public sealed class TranscriptStore
{
    private readonly IMonotonicClock _clock;
    private bool _prevFinalEndedInSpace = true;

    public TranscriptStore(IMonotonicClock clock) => _clock = clock;

    public TranscriptPane Source { get; } = new();
    public TranscriptPane Translation { get; } = new();

    /// <summary>Words in final transcription tokens for the whole session.</summary>
    public int SessionWordCount { get; private set; }

    public void ResetForNewSession()
    {
        Source.Clear();
        Translation.Clear();
        SessionWordCount = 0;
        _prevFinalEndedInSpace = true;
    }

    /// <returns>(sourceChanged, translationChanged)</returns>
    public (bool Source, bool Translation) Apply(IReadOnlyList<SttToken> batch)
    {
        // Non-final tokens are a snapshot that the server re-sends with each batch.
        Source.ClearTemp();
        Translation.ClearTemp();

        var now = _clock.NowMs;
        bool src = false, tr = false;
        foreach (var token in batch)
        {
            if (string.IsNullOrWhiteSpace(token.Text)) continue;
            if (token.Type == SttTokenType.Translation)
            {
                Translation.Add(token, now);
                tr = true;
            }
            else
            {
                Source.Add(token, now);
                src = true;
                if (token.IsFinal) CountWords(token.Text);
            }
        }
        if (src) Source.MergeConsecutive();
        if (tr) Translation.MergeConsecutive();
        return (src, tr);
    }

    private void CountWords(string text)
    {
        var inWord = !_prevFinalEndedInSpace;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                SessionWordCount++;
            }
        }
        _prevFinalEndedInSpace = text.Length == 0 ? _prevFinalEndedInSpace : char.IsWhiteSpace(text[^1]);
    }
}
