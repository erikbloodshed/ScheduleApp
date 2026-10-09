using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Excel;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ReactiveUI.SourceGenerators;

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
/// A ReactiveUI ViewModel (ViewModelBase): a failure no catch below expected is shown and
/// logged rather than escaping a command, and an import's list of problems is shown through
/// Notify, which the page answers with a MessageBox.
/// </summary>
public partial class ScheduleImportExportViewModel : ViewModelBase
{
    private readonly IScheduleRepository _repository;

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
    /// the scope picker. See this class's own summary above for why this is a constructor
    /// dependency.</summary>
    private readonly ScheduleCalendarViewModel _calendar;

    /// <summary>Handed straight through to ExportScheduleAsync's own scope picker
    /// (PayslipScopeViewModel), replacing the raw _repository it used to take --
    /// see ActiveRosterProvider's own doc comment for why every one of that picker's
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
        : base(statusBarService)
    {
        _repository = repository;
        _dataVersion = dataVersion;
        _tree = tree;
        _calendar = calendar;
        _activeRosterProvider = activeRosterProvider;

        ReportFailuresOf(ExportScheduleCommand, ImportScheduleCommand, ImportEmployeesCommand, ExportEmployeesCommand);
    }

    private const string ExcelFilter = "Excel workbook (*.xlsx)|*.xlsx";

    /// <summary>
    /// Opens the same Department/Employee checkbox-tree + period-picker scope dialog
    /// PayrollViewModel's "Print Payslips…"/"Export Payroll Report…" use (see
    /// PayslipScopeViewModel's own doc comment for the shared machinery), so the export can be
    /// narrowed to a single employee, a batch of employees/departments, or the whole company,
    /// over any date range -- rather than always dumping every employee's entire schedule
    /// history the way this command used to. requirePin: false because schedule export
    /// (unlike payroll) has never required an Employee ID -- see
    /// PayslipScopeViewModel.GetSelectedEmployees' own doc comment.
    /// </summary>
    [ReactiveCommand]
    private async Task ExportScheduleAsync()
    {
        var defaultStart = new DateTime(_calendar.DisplayedMonth.Year, _calendar.DisplayedMonth.Month, 1);
        var defaultEnd = defaultStart.AddMonths(1).AddDays(-1);

        var scopePicker = new PayslipScopeViewModel(_activeRosterProvider, requirePin: false)
        {
            Title = "Export Schedule",
            Description = "Choose who to export the schedule for and which date range.",
            ConfirmText = "Export…",
            ConfirmToolTip = "Saves the chosen employees' schedule for the chosen date range to an Excel workbook.",
            PeriodStart = defaultStart,
            PeriodEnd = defaultEnd,
            // Presets to just the currently-selected employee (the common "export one
            // person's schedule" case) when there is one, same "start narrow, still fully
            // editable" reasoning as the Payroll tab's active-group preset -- otherwise
            // falls back to the picker's own "whole company" default.
            PresetSelection = _tree.SelectedEmployee is { } selected ? [selected] : null,
        };
        if (!await ShowDialogAsync(scopePicker) || scopePicker.AcceptedScope is not { } scope) return;

        var rangeStart = DateOnly.FromDateTime(scope.PeriodStart);
        var rangeEnd = DateOnly.FromDateTime(scope.PeriodEnd);
        var employeeIds = scope.Employees.Select(e => e.Id).ToList();

        if (await PickFileToSaveAsync(ExcelFilter, $"Schedule_{rangeStart:yyyy-MM-dd}_to_{rangeEnd:yyyy-MM-dd}.xlsx") is not { } path)
            return;

        try
        {
            var departments = await _repository.GetDepartmentsForExportAsync(employeeIds, rangeStart, rangeEnd);
            var unassigned = await _repository.GetUnassignedForExportAsync(employeeIds, rangeStart, rangeEnd);
            ExcelScheduleExporter.Export(departments, unassigned, path);
        }
        catch (Exception ex)
        {
            // Most likely cause: the target file is open in Excel (sharing violation),
            // or the destination path/folder is no longer valid -- Failure says which.
            ShowFailure(ex, "Could not export the schedule");
            return;
        }

        StatusBar.ShowSuccess("Schedule exported.", "Export complete");
    }

    [ReactiveCommand]
    private async Task ImportScheduleAsync()
    {
        if (await PickFileToOpenAsync(ExcelFilter) is not { } path) return;

        try
        {
            var departments = ExcelScheduleImporter.Import(path);
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
        catch (DuplicateEmployeeIdException ex)
        {
            // A sheet named someone whose Pin already belongs to an employee outside
            // that sheet's own department (a different department, or none at all) --
            // see IScheduleRepository.ImportAsync's own doc comment. Not a column-
            // layout problem, so it gets its own message rather than the generic
            // catch's "Check that it matches the expected column layout" below, which
            // would be actively misleading here.
            ShowFailure(ex, "Import failed");
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Most likely cause: a cell that doesn't match the expected layout --
            // a non-numeric Id, a blank/invalid StartDate or EndDate, etc.
            Logger.Warning(ex, "Schedule import failed");
            StatusBar.ShowError(
                $"Could not import this file. Check that it matches the expected column layout. {ex.Message}",
                "Import failed");
            return;
        }

        await _tree.LoadAsync();
        StatusBar.ShowSuccess("Schedule import complete.", "Import");
    }

    [ReactiveCommand]
    private async Task ImportEmployeesAsync()
    {
        if (await PickFileToOpenAsync(ExcelFilter) is not { } path) return;

        List<EmployeeImportRow> rows;
        try
        {
            rows = EmployeeRosterImporter.Import(path);
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
            // doc comment), through Notify, which the page answers.
            await NotifyAsync(ex.Message, "Import problems found", NoticeKind.Warning);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Employee import failed");
            StatusBar.ShowError(
                $"Could not import this file. Check that it matches the expected column layout. {ex.Message}",
                "Import failed");
            return;
        }

        await _tree.LoadAsync();
        StatusBar.ShowSuccess($"Imported {rows.Count} employees.", "Import complete");
    }

    /// <summary>
    /// Saves the whole roster -- every department's employees plus the unassigned ones,
    /// blacklisted included -- in the same column layout ImportEmployeesAsync reads (see
    /// EmployeeRosterExporter's own doc comment), so the file can be edited in Excel and
    /// imported straight back. Reads the full GetDepartmentsWithEmployeesAsync/
    /// GetUnassignedEmployeesAsync pair rather than ActiveRosterProvider's Active-only cache,
    /// since a roster export that silently dropped blacklisted employees wouldn't be the
    /// complete roster.
    /// </summary>
    [ReactiveCommand]
    private async Task ExportEmployeesAsync()
    {
        if (await PickFileToSaveAsync(ExcelFilter, $"Employees_{DateTime.Today:yyyy-MM-dd}.xlsx") is not { } path)
            return;

        try
        {
            var departments = await _repository.GetDepartmentsWithEmployeesAsync();
            var unassigned = await _repository.GetUnassignedEmployeesAsync();
            EmployeeRosterExporter.Export(departments, unassigned, path);
        }
        catch (Exception ex)
        {
            // Most likely cause: the target file is open in Excel (sharing violation),
            // or the destination path/folder is no longer valid -- Failure says which.
            ShowFailure(ex, "Could not export employees");
            return;
        }

        StatusBar.ShowSuccess("Employees exported.", "Export complete");
    }
}
