namespace ScheduleApp.Desktop.Controls.FlexGrid;

public sealed class FlexGridCellEventArgs(object? item, int rowIndex, int columnIndex) : EventArgs
{
    /// <summary>The bound row item, or null in unbound mode -- there is no item, only a
    /// (row, column) address (see <see cref="FlexDataGrid"/>'s indexer).</summary>
    public object? Item { get; } = item;
    public int RowIndex { get; } = rowIndex;
    public int ColumnIndex { get; } = columnIndex;
}
