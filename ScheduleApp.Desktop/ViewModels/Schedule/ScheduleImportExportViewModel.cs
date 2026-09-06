using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Excel;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;

namespace ScheduleApp.Desktop.ViewModels.Schedule;

/// <summary>
/// Backs the Schedule tab's "Export Schedule…"/"Import Schedule…"/"Import Employees…"
/// commands (see MainViewModel-Split-Plan.md's component list) -- the smallest of the four
/// child ViewModels, and the split plan's own "smallest piece, low risk" pick for last among
/// the pure extractions. Unlike ScheduleAssignmentViewModel (phase 4), none of these three
/// commands are gated by AttendanceBusyState at all -- they never were on MainViewModel
/// either (each just awaits its own repository/Excel calls directly on the UI thread), so
/// this class takes no _busy dependency and adds no _busy.PropertyChanged subscription of
/// its own.
///
/// Takes EmployeeTreeViewModel (phase 2) and ScheduleCalendarViewModel (phase 3) as
/// constructor dependencies -- same "one child depends on another child" shape ReportViewModel
/// already establishes for ReportScopeViewModel (see the split plan's "Two more precedents"
/// section), and the same shape ScheduleAssignmentViewModel (phase 4) already used this
/// pattern for. Reads Tree.SelectedEmployee (ExportScheduleAsync's preset-selection default)
/// and Calendar.DisplayedMonth (ExportScheduleAsync's default date range) purely for
/// defaults -- neither write ever touches Tree or Calendar directly, unlike
/// ScheduleAssignmentViewModel's writes back through Calendar.RefreshScheduleForSelectedEmployeeAsync.
/// Calls Tree.LoadAsync() after a successful Import Schedule/Import Employees, the same
/// full-tree reload MainViewModel.LoadAsync() already is today (now EmployeeTreeViewModel's
/// own LoadAsync per phase 2) -- this class doesn't subscribe to anything on Tree or Calendar
/// for itself, since nothing here needs to react to a Tree/Calendar property changing, only to
/// call into them once, synchronously, from inside its own commands.
///
/// Pure extraction as planned -- MainViewModel itself is untouched, still owns its own copy
/// of every member moved here, and no other class references this one yet.
///
/// Extracted per the split plan's phase 5 ("takes EmployeeTreeViewModel ... and
/// ScheduleCalendarViewModel") -- one gap found in this table's own "Depends on" column:
/// see _dataVersion's own doc comment below for why AttendanceDataVersion is a constructor
/// dependency here despite the table naming only IScheduleRepository and IStatusBarService,
/// same "phase N's author flags what the table missed" shape phase 2's own AttendanceDataVersion
/// gap (and phase 4's ScheduleCalendarViewModel.RefreshCalendarAttendanceStatusesAsync gap)
/// already established. Phase 6 rebuilds MainViewModel as the facade that constructs this
/// class (last of the four, after Tree, Calendar, and ScheduleAssignmentViewModel all exist)
/// and forwards its members flatly, the same way AttendanceViewModel forwards
/// AttendanceImportViewModel's.
///
/// Not yet compiled against the real project (same no-SDK caveat as phases 1-4) -- flagging
/// this again for phase 6's author, same as phases 1 through 4 each did for the phase right
/// after them.
/// </summary>
public partial class ScheduleImportExportViewModel : ObservableObject
{
    private readonly IScheduleRepository _repository;
    private readonly IStatusBarService _statusBarService;

    /// <summary>Shared with EmployeeTreeViewModel, ScheduleAssignmentViewModel,
    /// AttendanceViewModel, and PayrollViewModel (same instance -- see App.xaml.cs's
    /// registration and AttendanceDataVersion.ScheduleVersion's own doc comment). Bumped
    /// here after ImportScheduleAsync's own repository write, exactly as MainViewModel's own
    /// version does today, so the Attendance Summary tab's own auto-reload-on-tab-select
    /// picks the change up next time it's viewed.
    ///
    /// NOT listed in the split plan's component-list table for this class -- the table only
    /// names IScheduleRepository and IStatusBarService. That's a gap in the table, not a
    /// deliberate omission, same shape as phase 2's own AttendanceDataVersion gap for
    /// EmployeeTreeViewModel.DeleteEmployeeAsync: ImportScheduleAsync replaces potentially
    /// every employee's schedule for the imported range in one call, which is squarely a
    /// schedule-mutating write the Summary tab needs to know about, same as every other
    /// _dataVersion.BumpScheduleForEmployees(...) call site already documented on
    /// AttendanceDataVersion itself -- and, since that bump carries the workbook's own Pins,
    /// something the payroll side's own per-employee staleness check can see too (see
    /// ImportScheduleAsync's own comment at the call site).
    /// ImportEmployeesAsync does NOT bump this -- it only touches the employee
    /// roster, not any schedule entries, so there's nothing here for the Summary tab to care
    /// about. Dropping the bump from ImportScheduleAsync would silently break that
    /// auto-reload the moment this class actually starts being used, so this constructor
    /// takes the same shared instance MainViewModel/EmployeeTreeViewModel/
    /// ScheduleAssignmentViewModel already receive (see App.xaml.cs's existing
    /// `services.AddScoped&lt;AttendanceDataVersion&gt;()` registration -- already shared
    /// app-wide, so this needs no new registration of its own) rather than following the
    /// table literally.</summary>
    private readonly AttendanceDataVersion _dataVersion;

    /// <summary>For SelectedEmployee (ExportScheduleAsync's preset-selection default) and
    /// LoadAsync() (the post-import reload) -- see this class's own summary above for why
    /// this is a constructor dependency rather than this class reaching back out to a shared
    /// facade.</summary>
    private readonly EmployeeTreeViewModel _tree;

    /// <summary>For DisplayedMonth only -- ExportScheduleAsync's default date range (the
    /// currently-displayed month) before the person narrows or widens it in
    /// PayslipScopeDialog. See this class's own summary above for why this is a constructor
    /// dependency.</summary>
    private readonly ScheduleCalendarViewModel _calendar;

    /// <summary>Handed straight through to ExportScheduleAsync's own PayslipScopeDialog
    /// construction, replacing the raw _repository that dialog's constructor used to take --
    /// see ActiveRosterProvider's own doc comment for why every one of PayslipScopeDialog's
    /// callers (this one included) now goes through the shared cache instead of each paying
    /// for its own GetActiveDepartmentsWithEmployeesAsync/GetActiveUnassignedEmployeesAsync
    /// round trip. Nothing else in this class reads it -- ExportScheduleAsync's own
    /// GetDepartmentsForExportAsync/GetUnassignedForExportAsync calls (the actual export,
    /// once the dialog returns a scope) stay on _repository, since those aren't the
    /// Active-only pair this cache holds and export deliberately includes the specific
    /// employees the person picked regardless of Active status.</summary>
    private readonly ActiveRosterProvider _activeRosterProvider;

    public ScheduleImportExportViewModel(
        IScheduleRepository repository,
        IStatusBarService statusBarService,
        AttendanceDataVersion dataVersion,
        EmployeeTreeViewModel tree,
        ScheduleCalendarViewModel calendar,
        ActiveRosterProvider activeRosterProvider)
    {
        _repository = repository;
        _statusBarService = statusBarService;
        _dataVersion = dataVersion;
        _tree = tree;
        _calendar = calendar;
        _activeRosterProvider = activeRosterProvider;
    }

    /// <summary>
    /// Opens the same Department/Employee checkbox-tree + period-picker scope dialog
    /// PayrollViewModel's "Print Payslips…"/"Export Payroll Report…" use (see
    /// PayslipScopeDialog's own doc comment for the shared machinery), so the export can be
    /// narrowed to a single employee, a batch of employees/departments, or the whole company,
    /// over any date range -- rather than always dumping every employee's entire schedule
    /// history the way this command used to. requirePin: false because schedule export
    /// (unlike payroll) has never required an Employee ID -- see
    /// PayslipScopeViewModel.GetSelectedEmployees' own doc comment.
    /// </summary>
    [RelayCommand]
    private async Task ExportScheduleAsync()
    {
        var defaultStart = new DateTime(_calendar.DisplayedMonth.Year, _calendar.DisplayedMonth.Month, 1);
        var defaultEnd = defaultStart.AddMonths(1).AddDays(-1);

        var scopeDialog = new PayslipScopeDialog(
            _activeRosterProvider, defaultStart, defaultEnd,
            title: "Export Schedule",
            description: "Choose who to export the schedule for and which date range.",
            confirmButtonText: "Export…",
            confirmButtonTooltip: "Saves the chosen employees' schedule for the chosen date range to an Excel workbook.",
            // Presets to just the currently-selected employee (the common "export one
            // person's schedule" case) when there is one, same "start narrow, still fully
            // editable" reasoning as PayrollViewModel's HasBatchScope preset -- otherwise
            // falls back to the dialog's own "whole company" default.
            presetSelection: _tree.SelectedEmployee is not null ? [_tree.SelectedEmployee] : null,
            requirePin: false)
        {
            Owner = Application.Current.MainWindow,
        };
        if (scopeDialog.ShowDialog() != true) return;

        var rangeStart = DateOnly.FromDateTime(scopeDialog.PeriodStart);
        var rangeEnd = DateOnly.FromDateTime(scopeDialog.PeriodEnd);
        var employeeIds = scopeDialog.SelectedEmployees.Select(e => e.Id).ToList();

        var saveDialog = new SaveFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = $"Schedule_{rangeStart:yyyy-MM-dd}_to_{rangeEnd:yyyy-MM-dd}.xlsx",
        };
        if (saveDialog.ShowDialog() != true) return;

        try
        {
            var departments = await _repository.GetDepartmentsForExportAsync(employeeIds, rangeStart, rangeEnd);
            var unassigned = await _repository.GetUnassignedForExportAsync(employeeIds, rangeStart, rangeEnd);
            ExcelScheduleExporter.Export(departments, unassigned, saveDialog.FileName);
        }
        catch (Exception ex)
        {
            // Most likely cause: the target file is open in Excel (sharing violation),
            // or the destination path/folder is no longer valid.
            _statusBarService.ShowError($"Could not export the schedule. {ex.Message}", "Export failed");
            return;
        }

        _statusBarService.ShowSuccess("Schedule exported.", "Export complete");
    }

    [RelayCommand]
    private async Task ImportScheduleAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var departments = ExcelScheduleImporter.Import(dialog.FileName);
            await _repository.ImportAsync(departments);

            // Per-employee, not the plain BumpSchedule() this used to call. The workbook is
            // already fully parsed into Departments-of-Employees at this point (that's what
            // ImportAsync was just handed), so the affected Pins cost nothing extra to collect
            // -- and a bump with no per-pin entry is invisible to AnyScheduleChangeSince, so an
            // already-loaded payroll group covering an imported employee would have gone on
            // showing pre-import figures. Every Pin in the workbook, not just the rows
            // ImportAsync actually changed: over-reporting costs at most one recompute that
            // lands on the same numbers, while under-reporting is the stale-figures bug this
            // fixes -- see BumpScheduleForEmployees' own doc comment.
            _dataVersion.BumpScheduleForEmployees([.. departments.SelectMany(d => d.Employees).Select(e => e.Pin)]);
        }
        catch (Exception ex)
        {
            // Most likely cause: a cell that doesn't match the expected layout --
            // a non-numeric Id, a blank/invalid StartDate or EndDate, etc.
            _statusBarService.ShowError(
                $"Could not import this file. Check that it matches the expected column layout. {ex.Message}",
                "Import failed");
            return;
        }

        await _tree.LoadAsync();
        _statusBarService.ShowSuccess("Schedule import complete.", "Import");
    }

    [RelayCommand]
    private async Task ImportEmployeesAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Excel workbook (*.xlsx)|*.xlsx" };
        if (dialog.ShowDialog() != true) return;

        List<EmployeeImportRow> rows;
        try
        {
            rows = EmployeeRosterImporter.Import(dialog.FileName);
            await _repository.ImportEmployeeRosterAsync(rows);
            _dataVersion.BumpRoster(); // writes Employee/Department -- see this class's own _dataVersion doc comment
        }
        catch (EmployeeImportException ex)
        {
            // Unlike the generic catch below, this can carry many lines (one sheet can fail
            // several rows for several different reasons at once) -- the status bar's
            // transient, single-line-ish notification isn't a good fit for that, so this
            // uses the same MessageBox surface the rest of the app already reserves for
            // things the status bar can't handle (see StatusBarNotificationExtensions' own
            // doc comment).
            MessageBox.Show(ex.Message, "Import problems found", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        catch (Exception ex)
        {
            _statusBarService.ShowError(
                $"Could not import this file. Check that it matches the expected column layout. {ex.Message}",
                "Import failed");
            return;
        }

        await _tree.LoadAsync();
        _statusBarService.ShowSuccess($"Imported {rows.Count} employees.", "Import complete");
    }
}
