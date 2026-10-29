using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;

namespace Unicaption.App.Controls;

/// <summary>
/// WS 04 floating notepad inside the app (not an OS window): type, copy, clear, close,
/// drag by header, resize from the bottom-right corner (220–600 × 200–700). The panel
/// is clamped to its host canvas. Text lives for this app run only.
/// </summary>
public sealed partial class NotesPanel : UserControl
{
    public const double MinW = 220, MaxW = 600, MinH = 200, MaxH = 700;

    private Point? _dragStart;
    private Point _posStart;
    private Point? _resizeStart;
    private Size _sizeStart;

    public NotesPanel()
    {
        InitializeComponent();
        Shadow = new Microsoft.UI.Xaml.Media.ThemeShadow();
        Translation = new System.Numerics.Vector3(0, 0, 16);
    }

    public event EventHandler? CloseRequested;

    public Canvas? Host => Parent as Canvas;

    public void PlaceDefault()
    {
        if (Host is null) return;
        Canvas.SetLeft(this, Math.Max(0, Host.ActualWidth - Width - 24));
        Canvas.SetTop(this, Math.Max(0, Host.ActualHeight - Height - 96));
        Clamp();
    }

    /// <summary>Keeps the panel inside the window after drags or window resizes.</summary>
    public void Clamp()
    {
        if (Host is null) return;
        var maxX = Math.Max(0, Host.ActualWidth - Width);
        var maxY = Math.Max(0, Host.ActualHeight - Height);
        Canvas.SetLeft(this, Math.Clamp(Canvas.GetLeft(this), 0, maxX));
        Canvas.SetTop(this, Math.Clamp(Canvas.GetTop(this), 0, maxY));
    }

    public void FocusEditor() => Editor.Focus(FocusState.Programmatic);

    private void OnHeaderPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && IsInsideButton(fe)) return;
        _dragStart = e.GetCurrentPoint(Host).Position;
        _posStart = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
        Header.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnHeaderMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragStart is not { } start) return;
        var p = e.GetCurrentPoint(Host).Position;
        Canvas.SetLeft(this, _posStart.X + p.X - start.X);
        Canvas.SetTop(this, _posStart.Y + p.Y - start.Y);
        Clamp();
    }

    private void OnHeaderReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragStart = null;
        Header.ReleasePointerCaptures();
    }

    private void OnGripPressed(object sender, PointerRoutedEventArgs e)
    {
        _resizeStart = e.GetCurrentPoint(Host).Position;
        _sizeStart = new Size(Width, Height);
        ResizeGrip.CapturePointer(e.Pointer);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast);
        e.Handled = true;
    }

    private void OnGripMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeStart is not { } start) return;
        var p = e.GetCurrentPoint(Host).Position;
        var hostW = Host?.ActualWidth ?? MaxW;
        var hostH = Host?.ActualHeight ?? MaxH;
        Width = Math.Clamp(_sizeStart.Width + p.X - start.X, MinW, Math.Max(MinW, Math.Min(MaxW, hostW - Canvas.GetLeft(this))));
        Height = Math.Clamp(_sizeStart.Height + p.Y - start.Y, MinH, Math.Max(MinH, Math.Min(MaxH, hostH - Canvas.GetTop(this))));
    }

    private void OnGripReleased(object sender, PointerRoutedEventArgs e)
    {
        _resizeStart = null;
        ResizeGrip.ReleasePointerCaptures();
        ProtectedCursor = null;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var pkg = new DataPackage();
        pkg.SetText(Editor.Text);
        Clipboard.SetContent(pkg);
        _ = FlashCopiedAsync();
    }

    private async Task FlashCopiedAsync()
    {
        CopiedText.Visibility = Visibility.Visible;
        await Task.Delay(1500);
        CopiedText.Visibility = Visibility.Collapsed;
    }

    private void OnClear(object sender, RoutedEventArgs e) => Editor.Text = "";

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        var text = Editor.Text;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        CountText.Text = $"{text.Length} chars · {words} words";
    }

    private bool IsInsideButton(FrameworkElement fe)
    {
        for (DependencyObject? d = fe; d is not null && d != Header; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
            if (d is Button) return true;
        return false;
    }
}
