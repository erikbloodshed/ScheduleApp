using System;
using System.Collections.Generic;
using System.Windows;
using Microsoft.Win32;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Excel;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Read-only detail dialog opened from a clickable status tile in the Summary
/// tab's counts strip (see ReportViewModel.ShowStatusDetail) -- shows every
/// row of one PunchStatus at a time, using the same AttendanceSummaryRow shape
/// and column set as the Summary grid itself, so a row looks identical
/// whichever place it's viewed from.
///
/// Export… writes just this status's rows out to their own workbook via the
/// same AttendanceExcelExporter.ExportSummaryToExcel the Summary tab's own
/// "Export Summary…" button uses -- that needs the raw AttendanceSummary
/// objects (not the flattened, display-only AttendanceSummaryRow the grid
/// binds to), so ReportViewModel.ShowStatusDetail passes both: rows for the
/// grid, summaries for this. AttendanceExcelExporter re-groups/re-sorts by
/// department and employee internally regardless of input order (see
/// WriteRows), so summaries doesn't need to already be in the same order as
/// rows for the exported workbook to come out right.
/// </summary>
public partial class AttendanceStatusDetailDialog : Window
{
    private readonly PunchStatus _status;
    private readonly IReadOnlyList<AttendanceSummary> _summaries;
    private readonly AttendancePolicy _policy;

    public AttendanceStatusDetailDialog(
        PunchStatus status,
        IReadOnlyList<AttendanceSummaryRow> rows,
        IReadOnlyList<AttendanceSummary> summaries,
        AttendancePolicy policy)
    {
        InitializeComponent();
        _status = status;
        _summaries = summaries;
        _policy = policy;

        // ToText() (not raw ToString()) for anything shown to a person, e.g.
        // "Official Business" instead of "OfficialBusiness" -- see
        // PunchStatusLabel. ExportButton_Click's file name below deliberately
        // keeps the raw ToString() instead, since a space isn't a great fit
        // for a file name.
        Title = $"{status.ToText()} -- {rows.Count} record{(rows.Count == 1 ? "" : "s")}";
        RowsGrid.ItemsSource = rows;

        // Nothing to write if the tile's own count was zero -- same
        // "disable rather than let it produce an empty workbook" treatment
        // CanExportSummary gives the Summary tab's own Export button.
        ExportButton.IsEnabled = _summaries.Count > 0;
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = $"Save {_status.ToText()} Attendance",
            Filter = "Excel Workbook (*.xlsx)|*.xlsx",
            // Raw ToString() here (not ToText()) -- "OfficialBusiness" is a
            // cleaner file name than "Official Business".
            FileName = $"Attendance_{_status}_{DateTime.Now:MMddyy}.xlsx",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            AttendanceExcelExporter.ExportSummaryToExcel(dialog.FileName, _summaries, _policy);
            MessageBox.Show(
                $"Saved {_summaries.Count} {_status.ToText()} record{(_summaries.Count == 1 ? "" : "s")} to:\n\n{dialog.FileName}",
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
