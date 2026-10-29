using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Unicaption.Core.Api;
using Unicaption.Core.Config;

namespace Unicaption.Core.Auth;

/// <summary>OS-protected secret storage (Windows: DPAPI / Credential Locker).</summary>
public interface ISecretStore
{
    string? Read(string key);
    void Write(string key, string value);
    void Delete(string key);
}

/// <summary>Opens a URL in the user's default browser (never an embedded web view).</summary>
public interface IBrowserLauncher
{
    void Open(Uri uri);
}

public sealed class AuthCancelledException() : Exception("Sign in cancelled.");

public sealed class ReauthenticationRequiredException() : Exception("Sign in again to continue.");

/// <summary>
/// AUTH 01/02: Google sign-in in the system browser using an RFC 8252 loopback redirect
/// with state + PKCE, then a Firebase ID token via identitytoolkit. The Firebase refresh
/// token lives in <see cref="ISecretStore"/>; ID tokens are refreshed before expiry and
/// concurrent refreshes collapse into one request.
/// </summary>
public sealed class AuthService : IAccessTokenProvider
{
    private const string SecretKey = "unicaption.session";
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(5);

    private readonly AppConfig _config;
    private readonly ISecretStore _secrets;
    private readonly IBrowserLauncher _browser;
    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private StoredSession? _session;
    private string? _idToken;
    private DateTimeOffset _idTokenExpiry;

    public AuthService(AppConfig config, ISecretStore secrets, IBrowserLauncher browser,
        HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null)
    {
        _config = config;
        _secrets = secrets;
        _browser = browser;
        _http = new HttpClient(handler ?? ApiClient.CreateHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Raised when the session can no longer be refreshed; the UI returns to Login.</summary>
    public event EventHandler? SignedOut;

    public UserProfile? User => _session?.ToProfile();
    public bool IsSignedIn => _session is not null;

    /// <summary>Restores a stored session. Returns false when the user must sign in.</summary>
    public bool TryRestore()
    {
        var raw = _secrets.Read(SecretKey);
        _session = raw is null ? null : StoredSession.TryParse(raw);
        if (raw is not null && _session is null) _secrets.Delete(SecretKey);
        return _session is not null;
    }

    public async Task<UserProfile> SignInAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.GoogleDesktopClientId))
            throw new InvalidOperationException("Google sign-in is not configured for Windows (GoogleDesktopClientId).");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(LoginTimeout);

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirectUri = $"http://127.0.0.1:{port}/";

        var authUrl = "https://accounts.google.com/o/oauth2/v2/auth" +
                      $"?client_id={Uri.EscapeDataString(_config.GoogleDesktopClientId)}" +
                      $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                      "&response_type=code&scope=openid%20email%20profile" +
                      $"&code_challenge={challenge}&code_challenge_method=S256" +
                      $"&state={state}&prompt=select_account";
        _browser.Open(new Uri(authUrl));

        Dictionary<string, string> query;
        try
        {
            query = await LoopbackRedirect.WaitAsync(listener, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new AuthCancelledException();
        }

        if (!query.TryGetValue("state", out var returnedState) || returnedState != state)
            throw new InvalidOperationException("Sign-in response did not match this request.");
        if (query.ContainsKey("error") || !query.TryGetValue("code", out var code))
            throw new AuthCancelledException();

        var googleIdToken = await ExchangeCodeAsync(code, verifier, redirectUri, timeout.Token).ConfigureAwait(false);
        var firebase = await SignInWithIdpAsync(googleIdToken, timeout.Token).ConfigureAwait(false);

        _session = firebase.session;
        _idToken = firebase.idToken;
        _idTokenExpiry = firebase.expiry;
        _secrets.Write(SecretKey, _session.Serialize());
        return _session.ToProfile();
    }

    /// <summary>Clears credentials. Preferences (theme etc.) are kept by the caller.</summary>
    public void SignOut()
    {
        _session = null;
        _idToken = null;
        _secrets.Delete(SecretKey);
    }

    public async Task<string?> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (_session is null) return null;
        var seen = _idToken;
        if (!forceRefresh && seen is not null && _now() < _idTokenExpiry - ExpirySkew) return seen;

        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another caller refreshed while we waited: reuse its token (no parallel refreshes).
            if (_idToken is not null && !ReferenceEquals(_idToken, seen) && _now() < _idTokenExpiry - ExpirySkew)
                return _idToken;
            if (_session is null) return null;
            await RefreshAsync(_session, ct).ConfigureAwait(false);
            return _idToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshAsync(StoredSession session, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://securetoken.googleapis.com/v1/token?key={Uri.EscapeDataString(_config.FirebaseApiKey)}")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = session.RefreshToken,
            }),
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if ((int)resp.StatusCode is 400 or 401 or 403)
        {
            SignOut();
            SignedOut?.Invoke(this, EventArgs.Empty);
            throw new ReauthenticationRequiredException();
        }
        if (!resp.IsSuccessStatusCode) throw new ApiException((int)resp.StatusCode, body);

        var json = ApiClient.Decode(body);
        _idToken = Required(json, "id_token");
        _idTokenExpiry = _now().AddSeconds(ParseSeconds(json, "expires_in"));
        var newRefresh = JsonNormalizer.OptionalString(json, "refresh_token");
        if (newRefresh is not null && newRefresh != session.RefreshToken)
        {
            _session = session with { RefreshToken = newRefresh };
            _secrets.Write(SecretKey, _session.Serialize());
        }
    }

    private async Task<string> ExchangeCodeAsync(string code, string verifier, string redirectUri, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _config.GoogleDesktopClientId,
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        };
        if (!string.IsNullOrEmpty(_config.GoogleDesktopClientSecret))
            form["client_secret"] = _config.GoogleDesktopClientSecret;

        using var resp = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct)
            .ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new ApiException((int)resp.StatusCode, body);
        return Required(ApiClient.Decode(body), "id_token");
    }

    private async Task<(StoredSession session, string idToken, DateTimeOffset expiry)> SignInWithIdpAsync(
        string googleIdToken, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["requestUri"] = "http://localhost",
            ["postBody"] = $"id_token={googleIdToken}&providerId=google.com",
            ["returnSecureToken"] = true,
            ["returnIdpCredential"] = true,
        };
        using var resp = await _http.PostAsync(
            $"https://identitytoolkit.googleapis.com/v1/accounts:signInWithIdp?key={Uri.EscapeDataString(_config.FirebaseApiKey)}",
            new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw new ApiException((int)resp.StatusCode, body);

        var json = ApiClient.Decode(body);
        var session = new StoredSession(
            Required(json, "localId"),
            JsonNormalizer.OptionalString(json, "email") ?? "",
            JsonNormalizer.OptionalString(json, "displayName"),
            Required(json, "refreshToken"));
        return (session, Required(json, "idToken"), _now().AddSeconds(ParseSeconds(json, "expiresIn")));
    }

    private static string Required(JsonObject json, string key) =>
        JsonNormalizer.OptionalString(json, key) ?? throw new ApiSchemaException($"Missing '{key}'.");

    private static double ParseSeconds(JsonObject json, string key) =>
        JsonNormalizer.OptionalNumber(json, key) ?? 3600;

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal sealed record StoredSession(string Uid, string Email, string? DisplayName, string RefreshToken)
    {
        public UserProfile ToProfile() => new(Uid, Email, DisplayName);

        public string Serialize() => new JsonObject
        {
            ["uid"] = Uid, ["email"] = Email, ["displayName"] = DisplayName, ["refreshToken"] = RefreshToken,
        }.ToJsonString();

        public static StoredSession? TryParse(string raw)
        {
            try
            {
                if (JsonNode.Parse(raw) is not JsonObject o) return null;
                var uid = JsonNormalizer.OptionalString(o, "uid");
                var refresh = JsonNormalizer.OptionalString(o, "refreshToken");
                if (uid is null || refresh is null) return null;
                return new StoredSession(uid, JsonNormalizer.OptionalString(o, "email") ?? "",
                    JsonNormalizer.OptionalString(o, "displayName"), refresh);
            }
            catch (System.Text.Json.JsonException)
            {
                return null;
            }
        }
    }
}

/// <summary>Minimal one-shot HTTP responder for the RFC 8252 loopback redirect.</summary>
internal static class LoopbackRedirect
{
    private const string Page =
        "<!doctype html><meta charset=utf-8><title>Unicaption</title>" +
        "<body style=\"font-family:Segoe UI,sans-serif;text-align:center;padding-top:80px;color:#1C2430\">" +
        "<h2>You can return to Unicaption.</h2><p>This tab can be closed.</p></body>";

    public static async Task<Dictionary<string, string>> WaitAsync(TcpListener listener, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct).ConfigureAwait(false) ?? "";
            var parts = requestLine.Split(' ');
            var target = parts.Length >= 2 ? parts[1] : "/";
            var query = ParseQuery(target);

            var bytes = Encoding.UTF8.GetBytes(Page);
            var header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct).ConfigureAwait(false);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);

            // Ignore favicon or other stray requests; wait for the real redirect.
            if (query.ContainsKey("code") || query.ContainsKey("error")) return query;
        }
    }

    internal static Dictionary<string, string> ParseQuery(string target)
    {
        var result = new Dictionary<string, string>();
        var q = target.IndexOf('?');
        if (q < 0) return result;
        foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var k = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var v = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[k] = v;
        }
        return result;
    }
}
