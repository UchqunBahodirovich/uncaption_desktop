using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Unicaption.Core.Auth;

namespace Unicaption.App.Platform;

/// <summary>
/// SEC 01: secrets encrypted with Windows DPAPI (CurrentUser scope) in
/// %LOCALAPPDATA%\Unicaption\session.dat. Only this Windows user can decrypt it.
/// </summary>
public sealed class DpapiSecretStore(string path) : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Unicaption.SecretStore.v1");
    private readonly object _gate = new();

    public string? Read(string key)
    {
        lock (_gate) return Load().GetValueOrDefault(key);
    }

    public void Write(string key, string value)
    {
        lock (_gate)
        {
            var all = Load();
            all[key] = value;
            Save(all);
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            var all = Load();
            if (all.Remove(key)) Save(all);
        }
    }

    private Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(path)) return [];
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException)
        {
            return [];
        }
    }

    private void Save(Dictionary<string, string> all)
    {
        if (all.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(all), Entropy, DataProtectionScope.CurrentUser);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, cipher);
        File.Move(tmp, path, overwrite: true);
    }
}
