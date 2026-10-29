namespace Unicaption.Core.Config;

/// <summary>
/// Endpoints and public client identifiers. Nothing in here is a secret:
/// the Firebase web API key and an OAuth "Desktop app" client id are public
/// by design (RFC 8252). Server or payment secrets must never be added here.
/// </summary>
public sealed record AppConfig
{
    public string ApiBaseUrl { get; init; } = "https://soniox-backend-216350809512.us-central1.run.app";
    public string WsUrl { get; init; } = "wss://soniox-backend-216350809512.us-central1.run.app/ws/transcribe/";
    public string UpdateManifestUrl { get; init; } = "https://unicaption.com/instals/latest.json";

    /// <summary>
    /// Firebase project public API key (identitytoolkit / securetoken). This is the key the
    /// existing desktop client already uses for signInWithIdp; the owner may issue a
    /// Windows-specific key and override it in unicaption.config.json.
    /// </summary>
    public string FirebaseApiKey { get; init; } = "AIzaSyCpL5NcfBV8qh5701sb3ICNQUmEfKTMiv0";

    /// <summary>
    /// Google OAuth client of type "Desktop app" registered for Windows.
    /// QAROR: must be provided by the project owner (see docs/SETUP.md).
    /// </summary>
    public string GoogleDesktopClientId { get; init; } = "";

    /// <summary>
    /// Google issues a "client secret" even for Desktop clients; Google documents
    /// it as non-confidential for installed apps. Left empty unless the owner's
    /// registration requires it for the token exchange.
    /// </summary>
    public string GoogleDesktopClientSecret { get; init; } = "";

    /// <summary>Optional external links. Empty = the UI hides the action (UI 06).</summary>
    public string TermsUrl { get; init; } = "";
    public string PrivacyUrl { get; init; } = "";
    public string BookCallUrl { get; init; } = "";
    public string WhatsAppUrl { get; init; } = "";
    public string SupportEmail { get; init; } = "support@unicaption.com";

    /// <summary>Realtime model id. Apex names are UI labels, not server ids (RT 01).</summary>
    public string RealtimeModel { get; init; } = "stt-rt-v3";

    public string AppVersion { get; init; } = "0.2.0";
}
