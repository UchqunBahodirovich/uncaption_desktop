using System.Runtime.InteropServices;

namespace Unicaption.App.Platform;

/// <summary>
/// Win32 window subclass for what WinUI does not expose directly:
/// minimum window size (UI 03) and OS suspend notification (TIME 02).
/// </summary>
internal sealed class WindowHooks
{
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMSUSPEND = 0x0004;

    private readonly IntPtr _hwnd;
    private readonly int _minWidthDip;
    private readonly int _minHeightDip;
    private readonly SubclassProc _proc; // kept alive for the lifetime of the window

    public WindowHooks(IntPtr hwnd, int minWidthDip, int minHeightDip)
    {
        _hwnd = hwnd;
        _minWidthDip = minWidthDip;
        _minHeightDip = minHeightDip;
        _proc = WndProc;
        SetWindowSubclass(hwnd, _proc, 1, 0);
    }

    /// <summary>Raised on the UI thread when Windows is about to suspend.</summary>
    public event Action? Suspending;

    public double Scale => GetDpiForWindow(_hwnd) / 96.0;

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, nuint id, nuint refData)
    {
        switch (msg)
        {
            case WM_GETMINMAXINFO:
                var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                info.ptMinTrackSize.x = (int)(_minWidthDip * Scale);
                info.ptMinTrackSize.y = (int)(_minHeightDip * Scale);
                Marshal.StructureToPtr(info, lParam, false);
                break;
            case WM_POWERBROADCAST when wParam.ToInt32() == PBT_APMSUSPEND:
                Suspending?.Invoke();
                break;
        }
        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}
