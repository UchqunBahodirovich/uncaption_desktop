using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Unicaption.Core.Api;
using Unicaption.Core.Auth;
using Unicaption.Core.Config;
using Unicaption.Core.Diagnostics;
using Unicaption.Core.Update;
using Xunit;

namespace Unicaption.Core.Tests;

public class NormalizerTests
{
    private static JsonObject J(string s) => JsonNode.Parse(s)!.AsObject();

    [Fact]
    public void Plan_Aliases()
    {
        var p = JsonNormalizer.ParsePlan(J("""{"plan_name":"Pro","used":30,"limit":"13200","translations_used":5,"translations_limit":50000,"expires_at":"2026-12-01T00:00:00Z"}"""));
        Assert.Equal("Pro", p.PlanName);
        Assert.Equal(30, p.UsedMinutes);
        Assert.Equal(13_200, p.LimitMinutes);
        Assert.Equal(50_000, p.LimitTranslations);
        Assert.NotNull(p.ExpiresAt);
    }

    [Fact] // shapes returned by the deployed backend (views.get_user_plan / get_ondemand_stats / translate_text)
    public void Plan_And_OnDemand_DeployedBackendShape()
    {
        var p = JsonNormalizer.ParsePlan(J("""{"plan": "ultra", "status": "active", "minutes_used": 72.4, "minutes_limit": 19573.8, "words_translated": 9597, "expiry_date": "2027-01-30T21:51:12.486055+00:00", "has_subscription": false, "cancel_at_period_end": false}"""));
        Assert.Equal("ultra", p.PlanName);
        Assert.Equal(72.4, p.UsedMinutes, 3);
        Assert.Equal(19573.8, p.LimitMinutes!.Value, 3);
        Assert.NotNull(p.ExpiresAt);
        Assert.True(p.IsActive);
        Assert.False(JsonNormalizer.ParsePlan(J("""{"plan": "pro", "status": "past_due"}""")).IsActive);

        var o = JsonNormalizer.ParseOnDemand(J("""{"ondemand_purchased": 1750.9, "ondemand_remaining": 1573.8, "ondemand_used": 179.6, "subscription_limit": 18000.0, "plan": "ultra"}"""));
        Assert.Equal(1573.8, o.RemainingMinutes!.Value, 3);

        Assert.Equal("привет", JsonNormalizer.ParseTranslation(J("""{"translatedText": "привет", "detectedSourceLang": "en", "context": ""}""")));
        Assert.Equal("Medical", JsonNormalizer.ParseTerms(J("""{"terms": [{"id": "cigna", "term": "Cigna", "category": "Medical"}]}"""))[0].Description);
    }

    [Fact] // DATA 02: unlimited is not assumed to be 0
    public void Plan_MissingOrUnlimited_IsNull()
    {
        var p = JsonNormalizer.ParsePlan(J("""{"plan":"Ultra","limit_translations":"unlimited"}"""));
        Assert.Null(p.LimitMinutes);
        Assert.Null(p.LimitTranslations);
        Assert.Null(p.MinutesProgress);
    }

    [Fact]
    public void Plan_Negative_IsSchemaError() =>
        Assert.Throws<ApiSchemaException>(() => JsonNormalizer.ParsePlan(J("""{"plan":"x","limit_minutes":-1}""")));

    [Fact] // no hardcoded balance × 60 / 0.55 estimate
    public void OnDemand_NoPriceGuess()
    {
        var o = JsonNormalizer.ParseOnDemand(J("""{"balance_dollars":10,"used_amount":5}"""));
        Assert.Null(o.RemainingMinutes);
        Assert.Equal(15, o.TotalDollars);
        Assert.Equal(1.0 / 3, o.UsedPercent, 3);
    }

    [Fact]
    public void Terms()
    {
        var terms = JsonNormalizer.ParseTerms(J("""{"results":[{"text":"EMS"},{"term":"CPR","description":"d"},{}]}"""));
        Assert.Equal(2, terms.Count);
        Assert.Equal("d", terms[1].Description);
    }
}

public class ApiClientTests
{
    [Fact] // HTTP 01: empty body → {}, list → {data: [...]}
    public async Task Decodes_EmptyAndList()
    {
        var handler = new StubHandler(r => r.RequestUri!.AbsolutePath.Contains("context")
            ? StubHandler.Json("""[{"term":"a"}]""")
            : StubHandler.Json(""));
        var api = new ApiClient("https://api.test", new FakeTokens(), handler);
        await api.UpdateUsageAsync(new UsageReport(10, 3));
        Assert.Single(await api.GetContextTermsAsync());
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
    }

    [Fact] // AUTH 02: 401 → exactly one forced refresh and one resend
    public async Task Unauthorized_RefreshesOnce()
    {
        var calls = 0;
        var handler = new StubHandler(_ => ++calls == 1 ? StubHandler.Json("", HttpStatusCode.Unauthorized) : StubHandler.Json("""{"plan":"Pro"}"""));
        var tokens = new FakeTokens();
        var api = new ApiClient("https://api.test", tokens, handler);
        Assert.Equal("Pro", (await api.GetPlanAsync()).PlanName);
        Assert.Equal(1, tokens.ForcedRefreshes);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Get_RetriesBounded_PostNever()
    {
        var handler = new StubHandler(_ => StubHandler.Json("boom", HttpStatusCode.InternalServerError));
        var api = new ApiClient("https://api.test", new FakeTokens(), handler);
        await Assert.ThrowsAsync<ApiException>(() => api.GetOnDemandAsync());
        Assert.Equal(3, handler.Requests.Count);

        handler.Requests.Clear();
        await Assert.ThrowsAsync<ApiException>(() => api.UpdateUsageAsync(new UsageReport(1, 1)));
        Assert.Single(handler.Requests);
    }

    [Fact] // UI 06: messages never include raw bodies
    public void ErrorText_HidesBody()
    {
        var text = ErrorText.For(new ApiException(500, "{\"secret\":\"token-abc\"}"));
        Assert.DoesNotContain("token", text);
    }
}

public class AuthServiceTests
{
    private sealed class MemorySecrets : ISecretStore
    {
        public readonly Dictionary<string, string> Data = [];
        public string? Read(string key) => Data.GetValueOrDefault(key);
        public void Write(string key, string value) => Data[key] = value;
        public void Delete(string key) => Data.Remove(key);
    }

    private sealed class NoBrowser : IBrowserLauncher
    {
        public void Open(Uri uri) { }
    }

    private static MemorySecrets StoredSession() => new()
    {
        Data = { ["unicaption.session"] = """{"uid":"u1","email":"a@b.c","displayName":"A","refreshToken":"r1"}""" },
    };

    [Fact] // AC 02: concurrent callers share a single refresh
    public async Task ConcurrentRefresh_SingleFlight()
    {
        var refreshes = 0;
        var handler = new StubHandler(r =>
        {
            Interlocked.Increment(ref refreshes);
            Thread.Sleep(50);
            return StubHandler.Json("""{"id_token":"fresh","refresh_token":"r2","expires_in":"3600"}""");
        });
        var secrets = StoredSession();
        var auth = new AuthService(new AppConfig(), secrets, new NoBrowser(), handler);
        Assert.True(auth.TryRestore());

        var tokens = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => auth.GetTokenAsync(false, CancellationToken.None)));
        Assert.All(tokens, t => Assert.Equal("fresh", t));
        Assert.Equal(1, refreshes);
        Assert.Contains("r2", secrets.Data["unicaption.session"]);
    }

    [Fact] // AC 02: refresh impossible → sign in again
    public async Task RevokedRefresh_SignsOut()
    {
        var handler = new StubHandler(_ => StubHandler.Json("""{"error":{"message":"TOKEN_EXPIRED"}}""", HttpStatusCode.BadRequest));
        var secrets = StoredSession();
        var auth = new AuthService(new AppConfig(), secrets, new NoBrowser(), handler);
        auth.TryRestore();
        var signedOut = false;
        auth.SignedOut += (_, _) => signedOut = true;

        await Assert.ThrowsAsync<ReauthenticationRequiredException>(() => auth.GetTokenAsync(false, CancellationToken.None));
        Assert.True(signedOut);
        Assert.False(auth.IsSignedIn);
        Assert.Empty(secrets.Data);
    }

    [Fact]
    public void CorruptStoredSession_IsDiscarded()
    {
        var secrets = new MemorySecrets { Data = { ["unicaption.session"] = "{not json" } };
        var auth = new AuthService(new AppConfig(), secrets, new NoBrowser());
        Assert.False(auth.TryRestore());
        Assert.Empty(secrets.Data);
    }

    [Fact]
    public void LoopbackQuery_Parsed()
    {
        var q = LoopbackRedirect.ParseQuery("/?state=abc&code=4%2F0Ab&scope=email+profile");
        Assert.Equal("4/0Ab", q["code"]);
        Assert.Equal("email profile", q["scope"]);
    }

    [Fact]
    public void Pkce_IsBase64Url()
    {
        var s = AuthService.Base64Url(RandomNumberGenerator.GetBytes(32));
        Assert.Equal(43, s.Length);
        Assert.DoesNotContain('+', s);
        Assert.DoesNotContain('/', s);
        Assert.DoesNotContain('=', s);
    }
}

public class UpdateServiceTests
{
    private const string Hash = "0000000000000000000000000000000000000000000000000000000000000000";

    [Theory] // AC 33 / AC 34
    [InlineData("""{"version":"0.2.0","download_url":"https://x/s.exe","sha256":"HASH"}""", false)]
    [InlineData("""{"version":"0.1.9","download_url":"https://x/s.exe","sha256":"HASH"}""", false)]
    [InlineData("""{"version":"0.2.1","download_url":"http://x/s.exe","sha256":"HASH"}""", false)]
    [InlineData("""{"version":"0.2.1","download_url":"https://x/s.exe","sha256":"abc"}""", false)]
    [InlineData("""{"version":"0.2.1","installer_url":"https://x/s.exe","sha256":"HASH"}""", true)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    [InlineData("[]", false)]
    public void Manifest_Validation(string json, bool expected) =>
        Assert.Equal(expected, UpdateService.ParseManifest(json.Replace("HASH", Hash), "0.2.0") is not null);

    [Fact]
    public void VersionCompare() => Assert.True(UpdateService.CompareVersions("0.10.0", "0.9.9") > 0);

    [Fact] // AC 35: wrong checksum never yields an installer path
    public async Task Download_ChecksumMismatch_Throws()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        var svc = new UpdateService(new Uri("https://x/latest.json"), "0.2.0", NullDiagnostics.Instance, handler);
        var update = new WindowsUpdate("0.2.1", new Uri("https://x/s.exe"), Hash, null);
        await Assert.ThrowsAsync<UpdateException>(() => svc.DownloadAsync(update, null, CancellationToken.None));
    }

    [Fact]
    public async Task Download_MatchingChecksum_ReturnsFile()
    {
        byte[] payload = [1, 2, 3, 4];
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        var svc = new UpdateService(new Uri("https://x/latest.json"), "0.2.0", NullDiagnostics.Instance, handler);
        var path = await svc.DownloadAsync(new WindowsUpdate("0.2.1", new Uri("https://x/s.exe"), hash, null), null, CancellationToken.None);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }
}

public class DiagnosticsTests
{
    [Fact] // AC 39: exception messages (which may embed URLs/bodies) are not logged
    public void LogsOnlyExceptionType()
    {
        var log = new DiagnosticsLog("0.2.0");
        log.Error("x", new Exception("Bearer secret-token"), ("k", "v"));
        Assert.DoesNotContain("secret-token", log.Summary());
        Assert.Contains("exception=Exception", log.Summary());
    }
}
