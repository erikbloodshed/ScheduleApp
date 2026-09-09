using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ScheduleApp.Desktop.Controls.FlexGrid;

/// <summary>
/// Row-virtualizing panel: only ever realizes <see cref="FlexGridRow"/> instances for the
/// rows actually visible (plus a one-row buffer), regardless of FlexDataGrid.RowCount.
/// This is the piece that makes the control behave like FlexGrid on a
/// large dataset rather than an ItemsControl that happens to look like a grid -- a plain
/// ItemsControl/ListView would materialize one visual per row up front.
///
/// Implements IScrollInfo directly (rather than relying on VirtualizingPanel/
/// VirtualizingStackPanel) so the realize/recycle logic below has full control over which
/// rows exist as visuals at all, which a VirtualizingStackPanel's UI-virtualization mode
/// already does for plain items but doesn't expose for this panel's own row-height math.
/// Must be hosted in a ScrollViewer with CanContentScroll="True" for any of this to engage
/// -- see FlexDataGrid.xaml's BodyScrollViewer.
/// </summary>
internal sealed class FlexGridRowsPanel : Panel, IScrollInfo
{
    private readonly FlexDataGrid _owner;
    private readonly Dictionary<int, FlexGridRow> _realizedRows = [];
    private readonly Stack<FlexGridRow> _recyclePool = [];

    private double _verticalOffset;
    private double _extentHeight;
    private double _viewportHeight;
    private double _extentWidth;
    private double _viewportWidth;

    public FlexGridRowsPanel(FlexDataGrid owner)
    {
        _owner = owner;
        ClipToBounds = true;
    }

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extentWidth;
    public double ExtentHeight => _extentHeight;
    public double ViewportWidth => _viewportWidth;
    public double ViewportHeight => _viewportHeight;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _verticalOffset;
    public ScrollViewer? ScrollOwner { get; set; }

    public IEnumerable<FlexGridRow> RealizedRows => _realizedRows.Values;

    // Horizontal scrolling of the whole grid (header + rows together) is handled by the
    // outer, non-virtualizing ScrollViewer in FlexDataGrid.xaml, so this axis is a no-op.
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void LineUp() => SetVerticalOffset(_verticalOffset - _owner.RowHeight);
    public void LineDown() => SetVerticalOffset(_verticalOffset + _owner.RowHeight);
    public void PageUp() => SetVerticalOffset(_verticalOffset - _viewportHeight);
    public void PageDown() => SetVerticalOffset(_verticalOffset + _viewportHeight);
    public void MouseWheelUp() => SetVerticalOffset(_verticalOffset - _owner.RowHeight * 3);
    public void MouseWheelDown() => SetVerticalOffset(_verticalOffset + _owner.RowHeight * 3);

    public void SetVerticalOffset(double offset)
    {
        var max = Math.Max(0, _extentHeight - _viewportHeight);
        offset = Math.Clamp(offset, 0, max);
        if (offset.Equals(_verticalOffset)) return;

        _verticalOffset = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public void MakeRowVisible(int rowIndex)
    {
        var top = rowIndex * _owner.RowHeight;
        var bottom = top + _owner.RowHeight;

        if (top < _verticalOffset) SetVerticalOffset(top);
        else if (bottom > _verticalOffset + _viewportHeight) SetVerticalOffset(bottom - _viewportHeight);
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        foreach (var (index, row) in _realizedRows)
        {
            if (row != visual && !row.IsAncestorOf(visual)) continue;
            MakeRowVisible(index);
            break;
        }
        return rectangle;
    }

    /// <summary>Rebuilds one already-realized row's cells in place, e.g. after a direct
    /// FlexDataGrid indexer write to an unbound cell that bypassed the row's own edit
    /// flow. A no-op if that row isn't currently on screen.</summary>
    public void RefreshRow(int rowIndex)
    {
        if (_realizedRows.TryGetValue(rowIndex, out var row))
            row.Bind(rowIndex);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var rowCount = _owner.RowCount;
        var rowHeight = Math.Max(1.0, _owner.RowHeight);

        _extentHeight = rowCount * rowHeight;
        _extentWidth = FlexGridLayout.TotalWidth(_owner.Columns);
        _viewportHeight = double.IsInfinity(availableSize.Height) ? _extentHeight : availableSize.Height;
        _viewportWidth = double.IsInfinity(availableSize.Width) ? _extentWidth : availableSize.Width;

        var maxOffset = Math.Max(0, _extentHeight - _viewportHeight);
        if (_verticalOffset > maxOffset) _verticalOffset = maxOffset;

        var firstVisible = Math.Max(0, (int)(_verticalOffset / rowHeight));
        var visibleCount = (int)Math.Ceiling(_viewportHeight / rowHeight) + 1;
        var lastVisible = Math.Min(rowCount - 1, firstVisible + visibleCount);

        RealizeRange(firstVisible, lastVisible, rowCount, rowHeight);

        ScrollOwner?.InvalidateScrollInfo();

        return new Size(_extentWidth, _extentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var rowHeight = Math.Max(1.0, _owner.RowHeight);
        foreach (var (index, row) in _realizedRows)
        {
            var y = index * rowHeight - _verticalOffset;
            row.Arrange(new Rect(0, y, _extentWidth, rowHeight));
        }
        return finalSize;
    }

    private void RealizeRange(int first, int last, int rowCount, double rowHeight)
    {
        List<int>? outOfRange = null;
        foreach (var index in _realizedRows.Keys)
        {
            if (index >= first && index <= last && index < rowCount) continue;
            (outOfRange ??= []).Add(index);
        }

        if (outOfRange is not null)
        {
            foreach (var index in outOfRange)
            {
                var row = _realizedRows[index];
                _realizedRows.Remove(index);
                row.Visibility = Visibility.Hidden;
                _recyclePool.Push(row);
            }
        }

        for (var i = first; i <= last && i < rowCount; i++)
        {
            if (_realizedRows.ContainsKey(i)) continue;

            var row = _recyclePool.Count > 0 ? _recyclePool.Pop() : CreateRow();
            row.Visibility = Visibility.Visible;
            row.Bind(i);
            row.Measure(new Size(_extentWidth, rowHeight));
            _realizedRows[i] = row;
        }
    }

    private FlexGridRow CreateRow()
    {
        var row = new FlexGridRow(_owner);
        Children.Add(row);
        return row;
    }
}
