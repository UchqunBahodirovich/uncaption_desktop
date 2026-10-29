using System.Text.Json.Nodes;
using Unicaption.Core.Realtime;
using Unicaption.Core.Time;
using Unicaption.Core.Transcript;
using Xunit;

namespace Unicaption.Core.Tests;

public class SessionClockTests
{
    [Fact] // AC 10 / AC 11: elapsed comes from the clock source, not from tick counts
    public void Elapsed_FollowsMonotonicSource_RegardlessOfReads()
    {
        var clock = new FakeClock();
        var s = new SessionClock(clock);
        s.Start();
        clock.Advance(60 * 60 * 1000); // one hour with no UI reads at all
        Assert.Equal(3600, s.ElapsedWholeSeconds);
    }

    [Fact] // TIME 01: Stop snapshot excludes cleanup time
    public void Stop_FreezesElapsed()
    {
        var clock = new FakeClock();
        var s = new SessionClock(clock);
        s.Start();
        clock.Advance(2_999);
        var snapshot = s.Stop();
        clock.Advance(10_000);
        Assert.Equal(2_999, snapshot.TotalMilliseconds, 3);
        Assert.Equal(2, s.ElapsedWholeSeconds); // floored
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(59, "00:59")]
    [InlineData(61, "01:01")]
    [InlineData(3_725, "62:05")] // minutes keep growing past 59
    public void Format_IsMinutesSeconds(int seconds, string expected) =>
        Assert.Equal(expected, SessionClock.Format(seconds));

    [Fact] // AC 12: the real clock is QPC-based (Stopwatch), not wall time
    public void SystemClock_IsMonotonic()
    {
        var a = SystemMonotonicClock.Instance.NowMs;
        var b = SystemMonotonicClock.Instance.NowMs;
        Assert.True(b >= a);
    }
}

public class TokenParserTests
{
    [Fact]
    public void Parses_Aliases_Defaults_AndClamps()
    {
        var arr = JsonNode.Parse("""
        [
          {"text":"Hello","speaker":"2","language":"EN-us"},
          {"text":"  ","speaker":1},
          {"text":"Привет","is_final":false,"translation_status":"translation","speaker_id":1,"lang":"ru"},
          {"text":"x","speaker":500},
          {"text":"y"},
          42
        ]
        """)!.AsArray();
        var t = TokenParser.Parse(arr);

        Assert.Equal(4, t.Count);
        Assert.Equal(new SttToken("Hello", true, SttTokenType.Transcription, 1, "en"), t[0]);
        Assert.Equal(new SttToken("Привет", false, SttTokenType.Translation, 0, "ru"), t[1]);
        Assert.Equal(99, t[2].Speaker);
        Assert.Equal(0, t[3].Speaker);
        Assert.Null(t[3].Language);
    }

    [Fact]
    public void Config_MatchesExistingContract()
    {
        var json = JsonNode.Parse(new RealtimeConfig("stt-rt-v3", "en", "ru").ToJson())!.AsObject();
        Assert.Equal("config", (string?)json["type"]);
        Assert.Equal("stt-rt-v3", (string?)json["model"]);
        Assert.Equal("two_way", (string?)json["translation"]!["type"]);
        Assert.Equal("ru", (string?)json["translation"]!["language_b"]);
    }
}

public class TranscriptStoreTests
{
    private static SttToken Src(string text, int speaker = 0, bool final = true, string? lang = "en") =>
        new(text, final, SttTokenType.Transcription, speaker, lang);

    private static SttToken Tr(string text, int speaker = 0, bool final = true, string? lang = "ru") =>
        new(text, final, SttTokenType.Translation, speaker, lang);

    [Fact] // AC 14: partial is a snapshot; finals never duplicate
    public void Partial_ThenFinal_DoesNotDuplicate()
    {
        var store = new TranscriptStore(new FakeClock());
        store.Apply([Src("Hello"), Src(" wor", final: false)]);
        Assert.Equal("Hello", store.Source.Segments[0].FinalText);
        Assert.Equal(" wor", store.Source.Segments[0].TempText);

        store.Apply([Src(" world", final: false)]);
        Assert.Equal(" world", store.Source.Segments[0].TempText);

        store.Apply([Src(" world.")]);
        Assert.Single(store.Source.Segments);
        Assert.Equal("Hello world.", store.Source.Segments[0].FinalText);
        Assert.Equal("", store.Source.Segments[0].TempText);
    }

    [Fact] // AC 15
    public void SpeakerChange_CreatesSegments_WithoutWrongMerge()
    {
        var store = new TranscriptStore(new FakeClock());
        store.Apply([Src("Hi.", 0), Src("Hello.", 1), Src("How are you?", 0)]);
        Assert.Equal([0, 1, 0], store.Source.Segments.Select(s => s.Speaker));
    }

    [Fact] // AC 16
    public void GapOver3s_SameSpeaker_StartsResumedSegment()
    {
        var clock = new FakeClock();
        var store = new TranscriptStore(clock);
        store.Apply([Src("One.")]);
        clock.Advance(3_001);
        store.Apply([Src(" Two.")]);
        Assert.Equal(2, store.Source.Segments.Count);
        Assert.True(store.Source.Segments[1].Resumed);

        clock.Advance(2_000);
        store.Apply([Src(" Three.")]);
        Assert.Equal(2, store.Source.Segments.Count);
        Assert.Equal(" Two. Three.", store.Source.Segments[1].FinalText);
    }

    [Fact] // TEXT 02 / AC 17: direction change splits segments
    public void DirectionChange_SplitsSegment()
    {
        var store = new TranscriptStore(new FakeClock());
        store.Apply([Tr("Hello", lang: "en"), Tr("مرحبا", lang: "ar")]);
        Assert.Equal(2, store.Translation.Segments.Count);
        Assert.True(Rtl.IsRtl(store.Translation.Segments[1].Language));
        Assert.False(Rtl.IsRtl("en"));
        Assert.True(Rtl.IsRtl("he-IL"));
    }

    [Fact] // AC 18 + AC 19 + DATA 04: UI limits do not change usage word count
    public void ClearAndCap_DoNotAffectWordCount()
    {
        var store = new TranscriptStore(new FakeClock());
        for (var i = 0; i < 60; i++) store.Apply([Src("word one ", i % 2)]);
        Assert.Equal(TranscriptPane.MaxSegments, store.Source.Segments.Count);
        store.Source.Clear();
        Assert.Empty(store.Source.Segments);
        Assert.Equal(120, store.SessionWordCount);
    }

    [Fact]
    public void WordCount_JoinsSubwordTokens_AndIgnoresPartials()
    {
        var store = new TranscriptStore(new FakeClock());
        store.Apply([Src("Hel"), Src("lo"), Src(" there"), Src(" partial words", final: false)]);
        Assert.Equal(2, store.SessionWordCount);
    }

    [Fact]
    public void Translation_RoutesToRightPane()
    {
        var store = new TranscriptStore(new FakeClock());
        var changed = store.Apply([Tr("Привет")]);
        Assert.Equal((false, true), changed);
        Assert.Empty(store.Source.Segments);
        Assert.Single(store.Translation.Segments);
    }
}
