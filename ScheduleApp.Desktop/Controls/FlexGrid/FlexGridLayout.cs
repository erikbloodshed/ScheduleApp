namespace ScheduleApp.Desktop.Controls.FlexGrid;

/// <summary>Shared column-width math used by both the header presenter and the row
/// panel, so the two are always laid out identically without duplicating the loop.</summary>
internal static class FlexGridLayout
{
    public static double TotalWidth(IReadOnlyList<FlexGridColumn> columns)
    {
        var total = 0.0;
        foreach (var column in columns)
            total += column.Width;
        return total;
    }
}
