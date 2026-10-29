using System.Text.Json;
using Microsoft.UI.Dispatching;
using Unicaption.App.Platform;
using Unicaption.Core.Api;
using Unicaption.Core.Auth;
using Unicaption.Core.Config;
using Unicaption.Core.Diagnostics;
using Unicaption.Core.Realtime;
using Unicaption.Core.Session;
using Unicaption.Core.Settings;
using Unicaption.Core.Time;
using Unicaption.Core.Transcript;
using Unicaption.Core.Update;

namespace Unicaption.App;

/// <summary>Composition root. One instance per process; created on the UI thread.</summary>
public sealed class AppServices
{
    public static AppServices Current { get; private set; } = null!;

    private AppServices(DispatcherQueue dispatcher)
    {
        Dispatcher = dispatcher;
        var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unicaption");
        Directory.CreateDirectory(dataDir);

        Config = LoadConfig();
        Settings = WorkspaceSettings.Load(Path.Combine(dataDir, "settings.json"));
        Diagnostics = new DiagnosticsLog(Config.AppVersion, Path.Combine(dataDir, "diagnostics.log"));
        Browser = new SystemBrowser();
        Auth = new AuthService(Config, new DpapiSecretStore(Path.Combine(dataDir, "session.dat")), Browser);
        Api = new ApiClient(Config.ApiBaseUrl, Auth);
        Account = new AccountService(Api, Diagnostics);
        Transcript = new TranscriptStore(SystemMonotonicClock.Instance);
        Audio = new WasapiAudioEngine(Diagnostics);
        Session = new SessionController(
            Audio,
            () => new ClientWsConnection(),
            Auth,
            new ApiUsageReporter(Api),
            new Uri(Config.WsUrl),
            Transcript,
            SystemMonotonicClock.Instance,
            Diagnostics,
            action => Dispatcher.TryEnqueue(() => action()));
        Session.EntitlementCheck = CheckEntitlement;
        Session.SessionEnded += (_, _) => _ = Account.RefreshAsync();
        Updates = new UpdateService(new Uri(Config.UpdateManifestUrl), Config.AppVersion, Diagnostics);
    }

    public static void Initialize(DispatcherQueue dispatcher) => Current = new AppServices(dispatcher);

    public DispatcherQueue Dispatcher { get; }
    public AppConfig Config { get; }
    public WorkspaceSettings Settings { get; }
    public DiagnosticsLog Diagnostics { get; }
    public IBrowserLauncher Browser { get; }
    public AuthService Auth { get; }
    public ApiClient Api { get; }
    public AccountService Account { get; }
    public TranscriptStore Transcript { get; }
    public WasapiAudioEngine Audio { get; }
    public SessionController Session { get; }
    public UpdateService Updates { get; }

    public bool UpdatePromptShown { get; set; }

    private SessionError? CheckEntitlement() =>
        Account.IsOutOfMinutes
            ? new SessionError(SessionErrorKind.LimitReached,
                "You have used all minutes in your plan. Top up or upgrade in Billing to continue.")
            : null;

    /// <summary>Optional unicaption.config.json next to the exe overrides public client settings.</summary>
    private static AppConfig LoadConfig()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "unicaption.config.json");
        var version = typeof(AppServices).Assembly.GetName().Version;
        var appVersion = version is null ? "0.2.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        try
        {
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip });
                if (cfg is not null) return cfg with { AppVersion = appVersion };
            }
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            // Fall back to built-in defaults; diagnostics are not yet available here.
        }
        return new AppConfig { AppVersion = appVersion };
    }

    private sealed class ApiUsageReporter(ApiClient api) : IUsageReporter
    {
        public Task ReportAsync(UsageReport report, CancellationToken ct) => api.UpdateUsageAsync(report, ct);
    }
}
