using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Unicaption.Core.Api;
using Unicaption.Core.Diagnostics;

namespace Unicaption.Core.Update;

public sealed record WindowsUpdate(string Version, Uri InstallerUrl, string Sha256, string? Notes);

public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Section 17: manifest check, bounded download with SHA-256 verification. The installer
/// is only ever returned when its checksum matches (AC 35). SHA-256 checks integrity, not
/// publisher identity; Authenticode signing is a separate release requirement.
/// </summary>
public sealed partial class UpdateService
{
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DownloadTotalTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(30);
    private const long MaxInstallerBytes = 1L << 30; // 1 GiB sanity cap

    private readonly HttpClient _http;
    private readonly Uri _manifestUrl;
    private readonly string _currentVersion;
    private readonly IDiagnostics _diag;

    public UpdateService(Uri manifestUrl, string currentVersion, IDiagnostics diag, HttpMessageHandler? handler = null)
    {
        _manifestUrl = manifestUrl;
        _currentVersion = currentVersion;
        _diag = diag;
        _http = new HttpClient(handler ?? ApiClient.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Returns a newer, valid update or null. Never throws for bad manifests (AC 34).</summary>
    public async Task<WindowsUpdate?> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ManifestTimeout);
            using var resp = await _http.GetAsync(_manifestUrl, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _diag.Warn("update.manifest_http", ("status", ((int)resp.StatusCode).ToString()));
                return null;
            }
            var text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var update = ParseManifest(text, _currentVersion);
            _diag.Info("update.checked", ("available", (update?.Version ?? "none")));
            return update;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            _diag.Warn("update.manifest_unreachable", ("type", e.GetType().Name));
            return null;
        }
    }

    internal static WindowsUpdate? ParseManifest(string text, string currentVersion)
    {
        JsonObject? json;
        try
        {
            json = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
        if (json is null) return null;

        var version = JsonNormalizer.OptionalString(json, "version") ?? "";
        var url = JsonNormalizer.OptionalString(json, "download_url", "installer_url");
        var sha = (JsonNormalizer.OptionalString(json, "sha256") ?? "").ToLowerInvariant();
        if (version.Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !Sha256Regex().IsMatch(sha) || CompareVersions(version, currentVersion) <= 0)
            return null;
        return new WindowsUpdate(version, uri, sha, JsonNormalizer.OptionalString(json, "notes"));
    }

    /// <summary>
    /// Downloads to a fresh temp folder and verifies SHA-256. Returns the verified installer path.
    /// Throws <see cref="UpdateException"/> on any failure; partial files are deleted.
    /// </summary>
    public async Task<string> DownloadAsync(WindowsUpdate update, IProgress<(long Received, long? Total)>? progress,
        CancellationToken ct)
    {
        var dir = Path.Combine(Path.GetTempPath(), "unicaption-update-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "UnicaptionSetup.exe");
        try
        {
            Directory.CreateDirectory(dir);
            using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
            total.CancelAfter(DownloadTotalTimeout);

            using var resp = await _http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, total.Token)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                throw new UpdateException($"Download failed (HTTP {(int)resp.StatusCode}).");
            var length = resp.Content.Headers.ContentLength;
            if (length > MaxInstallerBytes) throw new UpdateException("Installer is unexpectedly large.");

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await resp.Content.ReadAsStreamAsync(total.Token).ConfigureAwait(false))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long received = 0;
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                    idle.CancelAfter(DownloadIdleTimeout);
                    var read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    received += read;
                    if (received > MaxInstallerBytes) throw new UpdateException("Installer is unexpectedly large.");
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), total.Token).ConfigureAwait(false);
                    progress?.Report((received, length));
                }
                if (length is { } l && received != l) throw new UpdateException("Download was interrupted.");
            }

            var actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            if (actual != update.Sha256)
            {
                _diag.Error("update.checksum_mismatch", null, ("version", update.Version));
                throw new UpdateException("The downloaded installer failed verification and was discarded.");
            }
            _diag.Info("update.downloaded", ("version", update.Version));
            return path;
        }
        catch (Exception e)
        {
            TryDelete(dir);
            if (e is UpdateException) throw;
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            var message = e switch
            {
                IOException io when IsDiskFull(io) => "Not enough disk space to download the update.",
                OperationCanceledException => "The download timed out.",
                _ => "The update could not be downloaded.",
            };
            _diag.Error("update.download_failed", e);
            throw new UpdateException(message, e);
        }
    }

    public static int CompareVersions(string left, string right)
    {
        var l = VersionParts(left);
        var r = VersionParts(right);
        for (var i = 0; i < Math.Max(l.Count, r.Count); i++)
        {
            var c = (i < l.Count ? l[i] : 0).CompareTo(i < r.Count ? r[i] : 0);
            if (c != 0) return c;
        }
        return 0;
    }

    private static List<long> VersionParts(string v) =>
        DigitsRegex().Matches(v).Select(m => long.TryParse(m.Value, out var n) ? n : 0).ToList();

    private static bool IsDiskFull(IOException e) => (e.HResult & 0xFFFF) is 0x70 or 0x27; // ERROR_DISK_FULL / HANDLE_DISK_FULL

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [GeneratedRegex("^[a-f0-9]{64}$")]
    private static partial Regex Sha256Regex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitsRegex();
}
