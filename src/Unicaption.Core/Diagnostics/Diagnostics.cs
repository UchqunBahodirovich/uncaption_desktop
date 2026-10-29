using System.Text;

namespace Unicaption.Core.Diagnostics;

/// <summary>
/// DIAG 01: structured lifecycle events only. Callers must never pass bearer tokens,
/// PCM, transcript text, card data or full URLs with query strings.
/// </summary>
public interface IDiagnostics
{
    void Info(string evt, params (string Key, string Value)[] fields);
    void Warn(string evt, params (string Key, string Value)[] fields);
    void Error(string evt, Exception? e, params (string Key, string Value)[] fields);
}

/// <summary>Bounded in-memory log; optional file sink appends the same sanitised lines.</summary>
public sealed class DiagnosticsLog : IDiagnostics
{
    private const int Capacity = 500;
    private readonly Queue<string> _lines = new();
    private readonly object _gate = new();
    private readonly string? _filePath;
    private readonly string _appVersion;

    public DiagnosticsLog(string appVersion, string? filePath = null)
    {
        _appVersion = appVersion;
        _filePath = filePath;
    }

    public void Info(string evt, params (string Key, string Value)[] fields) => Add("INFO", evt, null, fields);
    public void Warn(string evt, params (string Key, string Value)[] fields) => Add("WARN", evt, null, fields);
    public void Error(string evt, Exception? e, params (string Key, string Value)[] fields) => Add("ERROR", evt, e, fields);

    /// <summary>Copyable summary for support (no secrets by construction).</summary>
    public string Summary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Unicaption {_appVersion} · {Environment.OSVersion} · {(Environment.Is64BitProcess ? "x64" : "x86")}");
        lock (_gate)
            foreach (var l in _lines) sb.AppendLine(l);
        return sb.ToString();
    }

    private void Add(string level, string evt, Exception? e, (string Key, string Value)[] fields)
    {
        var sb = new StringBuilder();
        sb.Append(DateTimeOffset.UtcNow.ToString("O")).Append(' ').Append(level).Append(' ').Append(evt);
        foreach (var (k, v) in fields) sb.Append(' ').Append(k).Append('=').Append(Sanitize(v));
        // Exception type only: messages may embed URLs or server bodies.
        if (e is not null) sb.Append(" exception=").Append(e.GetType().Name);
        var line = sb.ToString();

        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > Capacity) _lines.Dequeue();
            if (_filePath is null) return;
            try
            {
                File.AppendAllText(_filePath, line + Environment.NewLine);
            }
            catch (IOException) { /* diagnostics must never break the app */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static string Sanitize(string v)
    {
        if (v.Length > 120) v = v[..120];
        return v.Replace('\n', ' ').Replace('\r', ' ');
    }
}

public sealed class NullDiagnostics : IDiagnostics
{
    public static readonly NullDiagnostics Instance = new();
    public void Info(string evt, params (string Key, string Value)[] fields) { }
    public void Warn(string evt, params (string Key, string Value)[] fields) { }
    public void Error(string evt, Exception? e, params (string Key, string Value)[] fields) { }
}
