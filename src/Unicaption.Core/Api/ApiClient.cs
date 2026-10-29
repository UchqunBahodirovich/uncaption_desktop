using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace Unicaption.Core.Api;

/// <summary>Supplies the Firebase ID token used as Bearer token.</summary>
public interface IAccessTokenProvider
{
    /// <param name="forceRefresh">true after a 401 to obtain a fresh token (single-flight).</param>
    Task<string?> GetTokenAsync(bool forceRefresh, CancellationToken ct);
}

/// <summary>
/// REST client for the routes in section 9. Timeouts: connect 10 s, request 30 s.
/// GET is retried a bounded number of times; POST/DELETE are never retried
/// automatically except once after a 401 token refresh (HTTP 01).
/// </summary>
public sealed class ApiClient
{
    private const int MaxGetRetries = 2;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly IAccessTokenProvider _tokens;

    public ApiClient(string baseUrl, IAccessTokenProvider tokens, HttpMessageHandler? handler = null)
    {
        _tokens = tokens;
        _http = new HttpClient(handler ?? CreateHandler(), disposeHandler: true)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan, // enforced per request via CTS
        };
    }

    public static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public Task LoginSyncAsync(UserProfile user, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "auth/login-sync/",
            new JsonObject { ["uid"] = user.Uid, ["email"] = user.Email, ["display_name"] = user.DisplayName }, ct);

    public async Task<UserPlan> GetPlanAsync(CancellationToken ct = default) =>
        JsonNormalizer.ParsePlan(await SendAsync(HttpMethod.Get, "api/user/plan/", null, ct));

    public async Task<OnDemandUsage> GetOnDemandAsync(CancellationToken ct = default) =>
        JsonNormalizer.ParseOnDemand(await SendAsync(HttpMethod.Get, "api/user/ondemand/", null, ct));

    public async Task<string> CreateCheckoutSessionAsync(string priceId, string mode = "subscription", CancellationToken ct = default) =>
        JsonNormalizer.ParseCheckoutUrl(await SendAsync(HttpMethod.Post, "create-checkout-session/",
            new JsonObject { ["price_id"] = priceId, ["mode"] = mode }, ct));

    public Task CancelSubscriptionAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "api/cancel-subscription/", new JsonObject(), ct);

    /// <summary>Final usage for a session. Not retried automatically: no idempotency contract yet (SEC 02).</summary>
    public Task UpdateUsageAsync(UsageReport report, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "usage/update/",
            new JsonObject { ["seconds"] = report.Seconds, ["words_count"] = report.WordsCount }, ct);

    public async Task<IReadOnlyList<ContextTerm>> GetContextTermsAsync(CancellationToken ct = default) =>
        JsonNormalizer.ParseTerms(await SendAsync(HttpMethod.Get, "api/context-terms/", null, ct));

    public async Task<ContextTerm> AddContextTermAsync(string term, CancellationToken ct = default) =>
        JsonNormalizer.ParseTerm(await SendAsync(HttpMethod.Post, "api/context-terms/", new JsonObject { ["term"] = term }, ct));

    public async Task<string> TranslateAsync(string text, string source, string target, CancellationToken ct = default) =>
        JsonNormalizer.ParseTranslation(await SendAsync(HttpMethod.Post, "api/translate/",
            new JsonObject { ["text"] = text, ["source_lang"] = source, ["target_lang"] = target }, ct));

    internal async Task<JsonObject> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        var isGet = method == HttpMethod.Get;
        var refreshed = false;
        var forceRefresh = false;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var token = await _tokens.GetTokenAsync(forceRefresh, ct).ConfigureAwait(false);
                using var req = new HttpRequestMessage(method, path);
                forceRefresh = false;
                if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(RequestTimeout);
                HttpResponseMessage resp;
                try
                {
                    resp = await _http.SendAsync(req, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"{method} request timed out.");
                }

                using (resp)
                {
                    var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                    if (resp.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
                    {
                        // Rejected request had no effect server-side, so one resend is safe for any verb.
                        refreshed = true;
                        forceRefresh = true;
                        attempt--;
                        continue;
                    }
                    if (!resp.IsSuccessStatusCode) throw new ApiException((int)resp.StatusCode, text);
                    return Decode(text);
                }
            }
            catch (Exception e) when (isGet && attempt < MaxGetRetries && IsTransient(e) && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), ct).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception e) =>
        e is HttpRequestException or TimeoutException || e is ApiException { StatusCode: >= 500 or 429 };

    internal static JsonObject Decode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ApiSchemaException("Response is not valid JSON.");
        }
        return node switch
        {
            JsonObject o => o,
            null => new JsonObject(),
            _ => new JsonObject { ["data"] = node },
        };
    }
}
