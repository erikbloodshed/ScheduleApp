using System;
using System.Collections.Generic;
using System.Windows;
using Microsoft.Win32;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Excel;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Read-only detail dialog opened from the Orphaned/Unscheduled tiles in the
/// Summary tab's counts strip (see ReportViewModel.ShowOrphanedDetail /
/// ShowUnscheduledDetail) -- shows a plain list of raw punches, using the same
/// StoredPunchLogRow shape (and the same column set) as the Punch Records and
/// Manual Entries tabs, since neither Orphaned nor Unscheduled has a matched
/// shift to summarize the way AttendanceStatusDetailDialog's rows do.
///
/// Export… writes the punches behind title's rows out to their own workbook
/// via the same AttendanceExcelExporter.ExportLogsToExcel the Punch Records
/// tab's own export uses -- that needs the raw AttendanceLog/Employee objects
/// (not the flattened StoredPunchLogRow the grid binds to), so
/// ReportViewModel.ShowPunchListDetail passes both: rows for the grid,
/// punches/employees for this. The exported sheet is named after title
/// ("Orphaned"/"Unscheduled") rather than the default "Punch Logs", so the
/// workbook tab matches what was actually exported.
/// </summary>
public partial class PunchListDetailDialog : Window
{
    private readonly string _title;
    private readonly IReadOnlyList<AttendanceLog> _punches;
    private readonly IReadOnlyList<Employee> _employees;

    public PunchListDetailDialog(
        string title,
        IReadOnlyList<StoredPunchLogRow> rows,
        IReadOnlyList<AttendanceLog> punches,
        IReadOnlyList<Employee> employees)
    {
        InitializeComponent();
        _title = title;
        _punches = punches;
        _employees = employees;

        Title = $"{title} -- {rows.Count} record{(rows.Count == 1 ? "" : "s")}";
        RowsGrid.ItemsSource = rows;

        // Nothing to write if the tile's own count was zero -- same
        // "disable rather than let it produce an empty workbook" treatment
        // AttendanceStatusDetailDialog's Export button uses.
        ExportButton.IsEnabled = _punches.Count > 0;
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = $"Save {_title} Punches",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            FileName = $"Attendance_{_title}_{DateTime.Now:MMddyy}.xlsx",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            AttendanceExcelExporter.ExportLogsToExcel(dialog.FileName, _punches, _employees, _title);
            MessageBox.Show(
                $"Saved {_punches.Count} {_title} record{(_punches.Count == 1 ? "" : "s")} to:\n\n{dialog.FileName}",
                "Export complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Export failed:\n\n" + ex.Message,
                "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
