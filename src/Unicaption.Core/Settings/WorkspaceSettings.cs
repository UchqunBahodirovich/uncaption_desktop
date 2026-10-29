using System.Text.Json;
using System.Text.Json.Serialization;

namespace Unicaption.Core.Settings;

/// <summary>
/// Non-secret local preferences (DATA 03). Tokens never go here. Stored as JSON in
/// %LOCALAPPDATA%\Unicaption\settings.json; unknown/corrupt files fall back to defaults.
/// </summary>
public sealed class WorkspaceSettings
{
    public bool DarkMode { get; set; }
    public string SourceLanguage { get; set; } = "en";

    /// <summary>Extra languages that may also be spoken (after the primary <see cref="SourceLanguage"/>).</summary>
    public List<string> SourceLanguageHints { get; set; } = [];
    public string TargetLanguage { get; set; } = "ru";
    public double TextScale { get; set; } = 1.0;
    public bool SidebarExpanded { get; set; } = true;
    public int AudioSource { get; set; }

    /// <summary>Set once the first-run tour was finished or skipped; Support → Start tour replays it.</summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>Custom Context (≤300 chars). Kept locally until a backend transport is agreed (WS 03).</summary>
    public string CustomContext { get; set; } = "";
    public string TranslationPrompt { get; set; } = "";

    public static readonly double[] AllowedScales = [0.85, 1.0, 1.15, 1.30];
    public const int MaxContextLength = 300;
    public const int MaxLanguageHints = 4;

    [JsonIgnore] public string? FilePath { get; private set; }

    public static WorkspaceSettings Load(string path)
    {
        WorkspaceSettings s;
        try
        {
            s = File.Exists(path)
                ? JsonSerializer.Deserialize<WorkspaceSettings>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            s = new();
        }
        s.FilePath = path;
        s.Sanitize();
        return s;
    }

    public void Save()
    {
        if (FilePath is null) return;
        Sanitize();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Sanitize()
    {
        if (!AllowedScales.Any(a => Math.Abs(a - TextScale) < 0.001)) TextScale = 1.0;
        if (AudioSource is < 0 or > 2) AudioSource = 0;
        if (string.IsNullOrWhiteSpace(SourceLanguage)) SourceLanguage = "en";
        if (string.IsNullOrWhiteSpace(TargetLanguage)) TargetLanguage = "ru";
        SourceLanguageHints = (SourceLanguageHints ?? [])
            .Where(h => !string.IsNullOrWhiteSpace(h) && h != SourceLanguage)
            .Distinct()
            .Take(MaxLanguageHints)
            .ToList();
        CustomContext ??= "";
        TranslationPrompt ??= "";
        if (CustomContext.Length > MaxContextLength) CustomContext = CustomContext[..MaxContextLength];
    }
}
