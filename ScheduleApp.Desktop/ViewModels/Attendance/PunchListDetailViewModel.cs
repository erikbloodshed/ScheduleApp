using System.Reactive.Linq;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Excel;
using ReactiveUI.SourceGenerators;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>
/// The read-only punch list behind the Summary tab's Orphaned/Unscheduled tiles (see
/// ReportViewModel) -- raw punches in the same StoredPunchLogRow shape, and column set, as the
/// Punch Records and Manual Entries grids, since neither has a matched shift to summarize.
///
/// Export… writes the punches out with the same AttendanceExcelExporter.ExportLogsToExcel the
/// Punch Records export uses, which needs the raw AttendanceLog/Employee objects rather than
/// the flattened rows -- hence both. The sheet is named after the list ("Orphaned"/
/// "Unscheduled") rather than the default "Punch Logs".
/// </summary>
public partial class PunchListDetailViewModel : ReactiveViewModel
{
    private readonly string _name;
    private readonly IReadOnlyList<AttendanceLog> _punches;
    private readonly IReadOnlyList<Employee> _employees;

    /// <summary>Nothing to write for an empty list -- disabled rather than producing an empty
    /// workbook.</summary>
    private readonly IObservable<bool> _hasPunches;

    public PunchListDetailViewModel(string name, IReadOnlyList<AttendanceLog> punches, IReadOnlyList<Employee> employees)
    {
        _name = name;
        _punches = punches;
        _employees = employees;

        var employeeInfo = StoredPunchLogRowFactory.BuildEmployeeInfoByPin(employees);
        Rows = [.. punches.OrderBy(p => p.Timestamp).Select(p => StoredPunchLogRowFactory.BuildRow(p, employeeInfo))];
        Title = $"{name} -- {Rows.Count} record{(Rows.Count == 1 ? "" : "s")}";
        _hasPunches = Observable.Return(punches.Count > 0);
    }

    public string Title { get; }

    public IReadOnlyList<StoredPunchLogRow> Rows { get; }

    [ReactiveCommand(CanExecute = nameof(_hasPunches))]
    private async Task ExportAsync()
    {
        if (await PickFileToSaveAsync("Excel Workbook (*.xlsx)|*.xlsx", $"Attendance_{_name}_{DateTime.Now:MMddyy}.xlsx",
                $"Save {_name} Punches") is not { } path)
        {
            return;
        }

        try
        {
            AttendanceExcelExporter.ExportLogsToExcel(path, _punches, _employees, _name);
            await NotifyAsync(
                $"Saved {_punches.Count} {_name} record{(_punches.Count == 1 ? "" : "s")} to:\n\n{path}",
                "Export complete");
        }
        catch (Exception ex)
        {
            await NotifyAsync("Export failed:\n\n" + ex.Message, "Export failed", NoticeKind.Error);
        }
    }
}
