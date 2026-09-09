using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ScheduleApp.Desktop.Controls.FlexGrid;

/// <summary>
/// Renders the column header row: one cell per <see cref="FlexGridColumn"/>, laid out with
/// the same left-to-right, width-driven arrangement as <see cref="FlexGridRow"/> so the
/// header always lines up with the body without any explicit synchronization -- both read
/// FlexDataGrid.Columns and both live inside the same outer, horizontally-scrolled Grid
/// (see FlexDataGrid.xaml). Not virtualized: a grid has at most a few dozen columns, nowhere
/// near enough to need it.
/// </summary>
internal sealed class FlexGridColumnHeadersPresenter : Panel
{
    private readonly FlexDataGrid _owner;

    public FlexGridColumnHeadersPresenter(FlexDataGrid owner)
    {
        _owner = owner;
        Rebuild();
    }

    public void Rebuild()
    {
        Children.Clear();
        for (var i = 0; i < _owner.Columns.Count; i++)
            Children.Add(CreateHeaderCell(_owner.Columns[i], i));
    }

    private Grid CreateHeaderCell(FlexGridColumn column, int columnIndex)
    {
        var text = new TextBlock
        {
            Text = column.Header,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 4, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var glyph = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 9,
            Text = SortGlyph(column.SortDirection),
        };

        var content = new DockPanel { LastChildFill = true, Background = Brushes.Transparent };
        DockPanel.SetDock(glyph, Dock.Right);
        content.Children.Add(glyph);
        content.Children.Add(text);

        var cell = new Border
        {
            BorderBrush = (Brush)(_owner.TryFindResource("CardStrokeColorDefaultBrush") ?? Brushes.LightGray),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = content,
        };

        if (column.CanSort)
        {
            cell.Cursor = Cursors.Hand;
            cell.MouseLeftButtonUp += (_, _) => _owner.SortByColumn(columnIndex);
        }

        var host = new Grid();
        host.Children.Add(cell);

        if (column.CanResize)
        {
            var thumb = new Thumb
            {
                Width = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
                Cursor = Cursors.SizeWE,
                Background = Brushes.Transparent,
            };
            thumb.DragDelta += (_, e) => column.Width += e.HorizontalChange;
            host.Children.Add(thumb);
        }

        return host;
    }

    private static string SortGlyph(ListSortDirection? direction) => direction switch
    {
        ListSortDirection.Ascending => "▲",
        ListSortDirection.Descending => "▼",
        _ => string.Empty,
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = double.IsInfinity(availableSize.Height) ? _owner.HeaderHeight : availableSize.Height;
        for (var i = 0; i < Children.Count && i < _owner.Columns.Count; i++)
            Children[i].Measure(new Size(_owner.Columns[i].Width, height));

        return new Size(FlexGridLayout.TotalWidth(_owner.Columns), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        for (var i = 0; i < Children.Count && i < _owner.Columns.Count; i++)
        {
            var width = _owner.Columns[i].Width;
            Children[i].Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }
}
