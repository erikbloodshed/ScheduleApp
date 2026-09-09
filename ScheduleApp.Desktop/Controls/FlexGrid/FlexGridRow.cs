using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScheduleApp.Desktop.Controls.FlexGrid;

/// <summary>
/// One realized row of cells. Owned and recycled by <see cref="FlexGridRowsPanel"/> --
/// <see cref="Bind"/> re-points an existing instance at a different row index instead of
/// a new row being constructed per scroll tick. Lays its own cells out left-to-right by
/// column width rather than using a Grid, so resizing a column never needs to rebuild
/// ColumnDefinitions on every visible row -- MeasureOverride/ArrangeOverride just read
/// FlexDataGrid.Columns directly.
///
/// Doesn't hold the row's underlying item (or even know whether one exists) -- bound vs.
/// unbound mode is entirely FlexDataGrid's own concern; this class only ever asks it for
/// "the value at (RowIndex, columnIndex)" via GetCellValue/TryCommitCell/IsBooleanColumn,
/// which dispatch to reflection or FlexGridUnboundStore underneath.
/// </summary>
internal sealed class FlexGridRow : Panel
{
    private readonly FlexDataGrid _owner;
    private int _editingColumnIndex = -1;

    public int RowIndex { get; private set; } = -1;

    public FlexGridRow(FlexDataGrid owner)
    {
        _owner = owner;
        Background = Brushes.Transparent;
    }

    public void Bind(int rowIndex)
    {
        RowIndex = rowIndex;
        _editingColumnIndex = -1;
        RebuildCells();
        UpdateAppearance();
    }

    public void UpdateAppearance()
    {
        if (_owner.IsRowSelected(RowIndex))
        {
            Background = _owner.SelectionBrush;
            return;
        }

        Background = RowIndex % 2 == 1 && _owner.AlternatingRowsBackground is { } alt
            ? alt
            : Brushes.Transparent;
    }

    public void BeginEdit(int columnIndex)
    {
        if (RowIndex < 0 || columnIndex < 0 || columnIndex >= _owner.Columns.Count) return;
        if (_editingColumnIndex == columnIndex) return;

        var column = _owner.Columns[columnIndex];
        if (column.IsReadOnly || _owner.IsReadOnly) return;
        if (_owner.IsBooleanColumn(RowIndex, columnIndex)) return;

        CommitEdit();
        _editingColumnIndex = columnIndex;
        RebuildCells();

        if (Children[columnIndex] is TextBox editor)
        {
            editor.Focus();
            editor.SelectAll();
        }
    }

    /// <summary>Commits whatever cell is currently being edited, if any. Returns false only
    /// when a commit was attempted and the typed text failed to parse -- the caller (a key
    /// press, a click elsewhere) should then leave editing in place rather than moving on.</summary>
    public bool CommitEdit()
    {
        if (_editingColumnIndex < 0 || RowIndex < 0) return true;

        var columnIndex = _editingColumnIndex;
        if (Children[columnIndex] is not TextBox editor) return true;

        if (!_owner.TryCommitCell(RowIndex, columnIndex, editor.Text))
            return false;

        _editingColumnIndex = -1;
        RebuildCells();
        return true;
    }

    public void CancelEdit()
    {
        if (_editingColumnIndex < 0) return;
        _editingColumnIndex = -1;
        RebuildCells();
    }

    private void RebuildCells()
    {
        Children.Clear();
        if (RowIndex < 0) return;

        for (var i = 0; i < _owner.Columns.Count; i++)
            Children.Add(CreateCell(_owner.Columns[i], i));
    }

    private FrameworkElement CreateCell(FlexGridColumn column, int columnIndex)
    {
        var value = _owner.GetCellValue(RowIndex, columnIndex);

        if (_owner.IsBooleanColumn(RowIndex, columnIndex))
            return CreateCheckBoxCell(column, columnIndex, value as bool?);

        if (columnIndex == _editingColumnIndex)
            return CreateEditorCell(column, columnIndex, value);

        return CreateDisplayCell(column, columnIndex, value);
    }

    private FrameworkElement CreateDisplayCell(FlexGridColumn column, int columnIndex, object? value)
    {
        FrameworkElement cell = column.CellTemplate is not null
            ? new ContentPresenter { Content = value, ContentTemplate = column.CellTemplate, VerticalAlignment = VerticalAlignment.Center }
            : new TextBlock
            {
                Text = FlexGridReflection.FormatValue(value, column.StringFormat),
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

        cell.MouseLeftButtonDown += (_, e) => OnCellMouseDown(columnIndex, e);
        return cell;
    }

    private TextBox CreateEditorCell(FlexGridColumn column, int columnIndex, object? value)
    {
        var editor = new TextBox
        {
            Text = FlexGridReflection.FormatValue(value, column.StringFormat),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(1),
            Padding = new Thickness(6, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        editor.PreviewKeyDown += (_, e) => OnEditorPreviewKeyDown(e, columnIndex);
        editor.LostKeyboardFocus += (_, _) => CommitEdit();

        return editor;
    }

    private CheckBox CreateCheckBoxCell(FlexGridColumn column, int columnIndex, bool? value)
    {
        var checkBox = new CheckBox
        {
            IsChecked = value,
            IsThreeState = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = !column.IsReadOnly && !_owner.IsReadOnly,
        };

        checkBox.Click += (_, _) => _owner.SetBooleanCell(RowIndex, columnIndex, checkBox.IsChecked == true);
        checkBox.PreviewMouseLeftButtonDown += (_, e) => OnCellMouseDown(columnIndex, e, selectOnly: true);

        return checkBox;
    }

    private void OnCellMouseDown(int columnIndex, MouseButtonEventArgs e, bool selectOnly = false)
    {
        if (RowIndex < 0) return;

        if (_editingColumnIndex >= 0 && _editingColumnIndex != columnIndex && !CommitEdit())
            return;

        _owner.SelectRow(RowIndex, e);
        _owner.SetCurrentCell(RowIndex, columnIndex);

        if (!selectOnly && e.ClickCount == 2)
            BeginEdit(columnIndex);
    }

    private void OnEditorPreviewKeyDown(KeyEventArgs e, int columnIndex)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (CommitEdit())
                    _owner.MoveCurrentCell(RowIndex + 1, columnIndex);
                e.Handled = true;
                break;

            case Key.Escape:
                CancelEdit();
                e.Handled = true;
                break;

            case Key.Tab:
                var forward = Keyboard.Modifiers != ModifierKeys.Shift;
                if (CommitEdit())
                    _owner.MoveCurrentCell(RowIndex, columnIndex + (forward ? 1 : -1), wrap: true);
                e.Handled = true;
                break;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = _owner.RowHeight;
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
