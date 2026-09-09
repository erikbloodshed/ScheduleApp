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
    private readonly Border _currentCellIndicator;
    private int _editingColumnIndex = -1;

    public int RowIndex { get; private set; } = -1;

    public bool IsEditing => _editingColumnIndex >= 0;

    public FlexGridRow(FlexDataGrid owner)
    {
        _owner = owner;
        Background = Brushes.Transparent;

        // A dedicated overlay child rather than styling whichever cell happens to be
        // current: it never has to be rebuilt when the cells themselves are (RebuildCells
        // re-adds it last, see below), and it doesn't interfere with an editor TextBox's
        // own border. Always the last child, so it paints on top of the actual cells;
        // MeasureOverride/ArrangeOverride only iterate the first Columns.Count children
        // (see their own loop bound), so it's positioned separately in ArrangeOverride
        // instead.
        _currentCellIndicator = new Border
        {
            BorderBrush = _owner.CurrentCellBrush,
            BorderThickness = new Thickness(2),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        Children.Add(_currentCellIndicator);
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

        EndEdit();
        return true;
    }

    public void CancelEdit()
    {
        if (_editingColumnIndex < 0) return;
        EndEdit();
    }

    /// <summary>Tears the editor down and rebuilds the row as display cells.
    ///
    /// If the editor still holds keyboard focus we're the ones ending the edit (Escape,
    /// Enter, Tab, a click on another cell) and destroying it would drop focus out of the
    /// control entirely -- leaving F2 and further Tab presses dead, since FlexDataGrid only
    /// sees key events that tunnel towards a focused descendant. So focus goes back to the
    /// grid. If it has already lost focus we got here from its own LostKeyboardFocus
    /// handler, i.e. focus legitimately moved somewhere else, and taking it back would be
    /// stealing it.</summary>
    private void EndEdit()
    {
        var editorHadFocus = _editingColumnIndex >= 0
                             && _editingColumnIndex < Children.Count
                             && Children[_editingColumnIndex] is TextBox { IsKeyboardFocusWithin: true };

        _editingColumnIndex = -1;
        RebuildCells();

        if (editorHadFocus) _owner.Focus();
    }

    private void RebuildCells()
    {
        Children.Clear();
        if (RowIndex < 0) return;

        for (var i = 0; i < _owner.Columns.Count; i++)
            Children.Add(CreateCell(_owner.Columns[i], i));

        // Re-added last on every rebuild so it stays on top of whatever cells just replaced
        // the old ones -- Children.Clear() above removed it along with everything else.
        Children.Add(_currentCellIndicator);
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

    private Border CreateDisplayCell(FlexGridColumn column, int columnIndex, object? value)
    {
        UIElement content = column.CellTemplate is not null
            ? new ContentPresenter { Content = value, ContentTemplate = column.CellTemplate, VerticalAlignment = VerticalAlignment.Center }
            : new TextBlock
            {
                Text = FlexGridReflection.FormatValue(value, column.StringFormat),
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(8, 0, 8, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

        // Background must be an actual brush (Transparent, not null/unset) for the whole
        // cell rectangle to be hit-testable -- a TextBlock with no Background of its own
        // only receives mouse events where a glyph is actually painted, so clicking
        // anywhere else in the cell (padding, a short value, an empty cell) would silently
        // miss it and neither select the cell nor register a double-click to edit it.
        var cell = new Border { Background = Brushes.Transparent, Child = content };
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

    private Border CreateCheckBoxCell(FlexGridColumn column, int columnIndex, bool? value)
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

        // Same full-rectangle hit-testing reasoning as CreateDisplayCell -- the checkbox
        // itself is small and centered, so wrapping it means clicking anywhere in the cell
        // (not just squarely on the box) still selects it; only the checkbox's own Click
        // toggles the value.
        var cell = new Border { Background = Brushes.Transparent, Child = checkBox };
        cell.PreviewMouseLeftButtonDown += (_, e) => OnCellMouseDown(columnIndex, e, selectOnly: true);
        return cell;
    }

    private void OnCellMouseDown(int columnIndex, MouseButtonEventArgs e, bool selectOnly = false)
    {
        if (RowIndex < 0) return;

        if (_editingColumnIndex >= 0 && _editingColumnIndex != columnIndex && !CommitEdit())
            return;

        // Nothing in the grid is focusable except an active edit TextBox, so without this
        // a plain cell click never moves keyboard focus into FlexDataGrid's tree at all --
        // and F2/Tab/arrow keys, wired as PreviewKeyDown on FlexDataGrid itself, only ever
        // tunnel through elements on the path to whatever currently holds keyboard focus.
        // No focus in the grid meant those keys silently did nothing.
        _owner.Focus();

        _owner.SelectRow(RowIndex, e);
        _owner.SetCurrentCell(RowIndex, columnIndex);

        if (selectOnly) return; // a checkbox cell: the CheckBox still needs this click to toggle

        if (e.ClickCount == 2)
            BeginEdit(columnIndex);

        // The click stops here. Left unhandled it keeps travelling up to the body
        // ScrollViewer, whose own OnMouseLeftButtonDown calls Focus() on itself -- which
        // pulls keyboard focus straight back out of the editor BeginEdit just opened,
        // firing its LostKeyboardFocus/CommitEdit and closing edit mode again in the same
        // click. That is what made double-click-to-edit look like it did nothing at all.
        e.Handled = true;
    }

    // Tab isn't handled here -- FlexDataGrid.OnPreviewKeyDown handles it for both an active
    // edit and a merely-selected cell in one place (committing first if needed), since
    // PreviewKeyDown tunnels from the grid down to this editor and a handler on the grid
    // itself runs first; a second Tab handler here would just be dead code.
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
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = _owner.RowHeight;
        for (var i = 0; i < Children.Count && i < _owner.Columns.Count; i++)
            Children[i].Measure(new Size(_owner.Columns[i].Width, height));

        _currentCellIndicator.Measure(new Size(double.PositiveInfinity, height));

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

        ArrangeCurrentCellIndicator(finalSize.Height);

        return finalSize;
    }

    /// <summary>Shows/hides and positions the current-cell border by re-reading
    /// FlexDataGrid.CurrentRowIndex/CurrentColumnIndex fresh on every arrange pass, rather
    /// than caching them -- FlexDataGrid.SetCurrentCell just calls InvalidateArrange on
    /// every realized row (see RefreshCurrentCellIndicator) and lets this recompute
    /// whether it's now the current row instead of pushing the new indices down itself.</summary>
    private void ArrangeCurrentCellIndicator(double height)
    {
        var columnIndex = _owner.CurrentColumnIndex;
        var isCurrent = RowIndex == _owner.CurrentRowIndex && columnIndex >= 0 && columnIndex < _owner.Columns.Count;

        _currentCellIndicator.Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed;

        if (!isCurrent)
        {
            _currentCellIndicator.Arrange(new Rect(0, 0, 0, 0));
            return;
        }

        var x = 0.0;
        for (var i = 0; i < columnIndex; i++)
            x += _owner.Columns[i].Width;

        _currentCellIndicator.Arrange(new Rect(x, 0, _owner.Columns[columnIndex].Width, height));
    }
}
