using System.Net;
using System.Text.Json.Nodes;
using Unicaption.Core.Auth;
using Unicaption.Core.Config;
using Xunit;

namespace Unicaption.Core.Tests;

public class EmailAuthTests
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

    private const string Session =
        """{"idToken":"id-1","refreshToken":"r-1","expiresIn":"3600","localId":"u1","email":"a@b.co"}""";

    private static string Error(string message) =>
        new JsonObject { ["error"] = new JsonObject { ["code"] = 400, ["message"] = message } }.ToJsonString();

    private static (AuthService Auth, MemorySecrets Secrets) Create(StubHandler handler) =>
        CreateWith(new AppConfig(), handler);

    private static (AuthService Auth, MemorySecrets Secrets) CreateWith(AppConfig config, StubHandler handler)
    {
        var secrets = new MemorySecrets();
        return (new AuthService(config, secrets, new NoBrowser(), handler), secrets);
    }

    private static JsonObject Body(HttpRequestMessage r) =>
        JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!.AsObject();

    [Fact]
    public async Task SignIn_StoresSessionAndIssuesToken()
    {
        var handler = new StubHandler(_ => StubHandler.Json(Session));
        var (auth, secrets) = Create(handler);

        var user = await auth.SignInWithPasswordAsync("  a@b.co ", "secret1", CancellationToken.None);

        Assert.Equal("u1", user.Uid);
        Assert.True(auth.IsSignedIn);
        Assert.Contains("r-1", secrets.Data["unicaption.session"]);
        Assert.DoesNotContain("secret1", secrets.Data["unicaption.session"]); // password is never stored
        Assert.Equal("id-1", await auth.GetTokenAsync(false, CancellationToken.None));
        var req = Assert.Single(handler.Requests);
        Assert.Contains("accounts:signInWithPassword", req.RequestUri!.AbsoluteUri);
        Assert.Equal("a@b.co", (string?)Body(req)["email"]);
    }

    [Theory]
    [InlineData("INVALID_LOGIN_CREDENTIALS", "Email or password is incorrect.")]
    [InlineData("EMAIL_EXISTS", "An account with this email already exists. Sign in instead.")]
    [InlineData("WEAK_PASSWORD : Password should be at least 6 characters", "Password must be at least 6 characters.")]
    [InlineData("TOO_MANY_ATTEMPTS_TRY_LATER : Access temporarily disabled", "Too many attempts. Wait a few minutes and try again.")]
    [InlineData("SOMETHING_NEW", "Sign in failed. Please try again.")]
    public async Task FirebaseErrors_AreMappedToFriendlyText(string firebase, string expected)
    {
        var (auth, _) = Create(new StubHandler(_ => StubHandler.Json(Error(firebase), HttpStatusCode.BadRequest)));
        var e = await Assert.ThrowsAsync<AuthFailedException>(
            () => auth.SignInWithPasswordAsync("a@b.co", "secret1", CancellationToken.None));
        Assert.Equal(expected, e.Message);
        Assert.False(auth.IsSignedIn);
    }

    [Fact]
    public async Task SignUp_SetsDisplayName()
    {
        var handler = new StubHandler(r => r.RequestUri!.AbsoluteUri.Contains("accounts:update")
            ? StubHandler.Json("""{"localId":"u1","displayName":"Ali","idToken":"id-2","refreshToken":"r-2","expiresIn":"3600"}""")
            : StubHandler.Json(Session));
        var (auth, secrets) = Create(handler);

        var user = await auth.SignUpWithPasswordAsync("a@b.co", "secret1", " Ali ", CancellationToken.None);

        Assert.Equal("Ali", user.DisplayName);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Ali", (string?)Body(handler.Requests[1])["displayName"]);
        Assert.Equal("id-2", await auth.GetTokenAsync(false, CancellationToken.None));
        Assert.Contains("r-2", secrets.Data["unicaption.session"]);
    }

    [Fact]
    public async Task SignUp_ShortPassword_RejectedLocally()
    {
        var handler = new StubHandler(_ => StubHandler.Json(Session));
        var (auth, _) = Create(handler);
        await Assert.ThrowsAsync<AuthFailedException>(
            () => auth.SignUpWithPasswordAsync("a@b.co", "123", null, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PasswordReset_UnknownEmail_LooksLikeSuccess()
    {
        var handler = new StubHandler(_ => StubHandler.Json(Error("EMAIL_NOT_FOUND"), HttpStatusCode.BadRequest));
        var (auth, _) = Create(handler);
        await auth.SendPasswordResetAsync("nobody@b.co", CancellationToken.None);
        Assert.Equal("PASSWORD_RESET", (string?)Body(Assert.Single(handler.Requests))["requestType"]);
    }

    [Fact]
    public void GoogleConfigured_FollowsClientId()
    {
        var handler = new StubHandler(_ => StubHandler.Json("{}"));
        Assert.False(Create(handler).Auth.IsGoogleConfigured);
        Assert.True(CreateWith(new AppConfig { GoogleDesktopClientId = "x.apps.googleusercontent.com" }, handler).Auth.IsGoogleConfigured);
    }
}
