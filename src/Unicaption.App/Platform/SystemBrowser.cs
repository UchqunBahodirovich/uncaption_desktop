using System.Diagnostics;
using Unicaption.Core.Auth;

namespace Unicaption.App.Platform;

/// <summary>Opens the default browser / handler. Only http(s), mailto and ms-settings are allowed.</summary>
public sealed class SystemBrowser : IBrowserLauncher
{
    private static readonly HashSet<string> AllowedSchemes = ["https", "http", "mailto", "ms-settings"];

    public void Open(Uri uri)
    {
        if (!AllowedSchemes.Contains(uri.Scheme)) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public static void OpenMicrophonePrivacySettings() =>
        Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
}
