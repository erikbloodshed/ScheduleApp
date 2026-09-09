namespace ScheduleApp.Desktop.Controls.FlexGrid;

/// <summary>
/// Backing store for <see cref="FlexDataGrid"/>'s unbound mode -- FlexGrid's own
/// "no DataSource, just Rows/Cols and a value per cell" way of working, for data that
/// isn't naturally a list of objects (a manually-populated summary grid, a small
/// spreadsheet-like editor, etc.). Each row is a plain <c>object?[]</c> sized to the
/// grid's current column count; there is no item, no BindingPath -- a cell is addressed
/// purely by (row, column). Active only while FlexDataGrid.ItemsSource is null.
/// </summary>
internal sealed class FlexGridUnboundStore
{
    private readonly List<object?[]> _rows = [];

    public int RowCount => _rows.Count;

    public object? GetValue(int rowIndex, int columnIndex)
    {
        if (rowIndex < 0 || rowIndex >= _rows.Count) return null;
        var row = _rows[rowIndex];
        return columnIndex >= 0 && columnIndex < row.Length ? row[columnIndex] : null;
    }

    public void SetValue(int rowIndex, int columnIndex, object? value)
    {
        var row = _rows[rowIndex];
        if (columnIndex < 0 || columnIndex >= row.Length) return;
        row[columnIndex] = value;
    }

    public void SetRowCount(int count, int columnCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        while (_rows.Count > count)
            _rows.RemoveAt(_rows.Count - 1);
        while (_rows.Count < count)
            _rows.Add(new object?[columnCount]);
    }

    public void InsertRow(int index, int columnCount) => _rows.Insert(Math.Clamp(index, 0, _rows.Count), new object?[columnCount]);

    public void RemoveRow(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        _rows.RemoveAt(index);
    }

    public void Clear() => _rows.Clear();

    /// <summary>Resizes every row to match a new column count -- called whenever
    /// FlexDataGrid.Columns changes -- preserving values in columns that still exist and
    /// leaving new columns null.</summary>
    public void EnsureColumnCount(int columnCount)
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            if (row.Length == columnCount) continue;

            var resized = new object?[columnCount];
            Array.Copy(row, resized, Math.Min(row.Length, columnCount));
            _rows[i] = resized;
        }
    }

    public void Sort(int columnIndex, System.ComponentModel.ListSortDirection direction)
    {
        var sign = direction == System.ComponentModel.ListSortDirection.Ascending ? 1 : -1;
        _rows.Sort((a, b) => sign * Comparer<object?>.Default.Compare(a[columnIndex], b[columnIndex]));
    }
}
