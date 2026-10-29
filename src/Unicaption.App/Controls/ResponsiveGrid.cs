using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Unicaption.App.Controls;

/// <summary>
/// Grid that re-flows its children row-major into N equal columns based on its own width.
/// Breakpoints are a list of (minWidth, columns) checked from widest to narrowest.
/// </summary>
public sealed class ResponsiveGrid : Grid
{
    private int _columns = -1;

    public ResponsiveGrid()
    {
        ColumnSpacing = 16;
        RowSpacing = 16;
        SizeChanged += (_, e) => Reflow(e.NewSize.Width);
    }

    private (double MinWidth, int Columns)[] _breakpoints = [(760, 2), (0, 1)];

    public void SetBreakpoints(params (double MinWidth, int Columns)[] breakpoints)
    {
        _breakpoints = breakpoints;
        _columns = -1;
        Reflow();
    }

    public void Reflow() => Reflow(ActualWidth);

    private void Reflow(double width)
    {
        var cols = _breakpoints.OrderByDescending(b => b.MinWidth).First(b => width >= b.MinWidth).Columns;
        var visible = Children.OfType<FrameworkElement>().Where(c => c.Visibility == Visibility.Visible).ToList();
        var rows = (visible.Count + cols - 1) / cols;
        if (cols != _columns || ColumnDefinitions.Count != cols)
        {
            _columns = cols;
            ColumnDefinitions.Clear();
            for (var i = 0; i < cols; i++) ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        if (RowDefinitions.Count != rows)
        {
            RowDefinitions.Clear();
            for (var i = 0; i < rows; i++) RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (var i = 0; i < visible.Count; i++)
        {
            SetColumn(visible[i], i % cols);
            SetRow(visible[i], i / cols);
        }
    }
}
