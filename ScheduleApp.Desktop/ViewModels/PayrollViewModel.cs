using ReactiveUI;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.ViewModels.Payroll;
using ScheduleApp.Payroll.Pdf;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// The Payroll tab: builds its four concerns over one shared payroll period and batch
/// (<see cref="Scope"/>), and PayrollPage binds to each directly --
/// <see cref="Summary"/> (the selected employee's itemized breakdown and attendance basis),
/// <see cref="Group"/> (the Payroll Group table and its membership), <see cref="Run"/> (starting
/// or reopening a run) and <see cref="PrintExport"/> (batch payslips and the Excel report). See
/// PayrollViewModel_Refactor_Plan.md for how they were split apart.
///
/// The selected employee is the app-wide one (<see cref="IEmployeeSelection"/>, shared with the
/// Schedule/Employees/Attendance tabs) rather than this tab's own. There's deliberately no
/// separate "Generate" action -- the breakdown is recomputed live whenever the employee, period
/// or an adjustment changes, the same way the Attendance tab's Summary recomputes.
///
/// Every child shares the one app-wide AttendanceBusyState: they, MainViewModel and the
/// Attendance tab all read and write the one app-lifetime ScheduleDbContext, and nothing may
/// touch it while anything else already is -- so the Payroll tab's busy state also reflects
/// work started elsewhere, by design.
/// </summary>
public sealed class PayrollViewModel : ReactiveObject
{
    public PayrollViewModel(
        IEmployeeSelection selection,
        IPayrollComputationService payrollComputationService,
        IPayrollAdjustmentRepository adjustmentRepository,
        IPayrollUndertimeWaiverRepository undertimeWaiverRepository,
        ActiveRosterProvider rosterProvider,
        IPayrollRunRepository payrollRunRepository,
        IStatusBarService statusBarService,
        AttendanceBusyState busy,
        AttendanceDataVersion dataVersion,
        PayrollSettings payrollSettings)
    {
        var companyName = string.IsNullOrWhiteSpace(payrollSettings.CompanyName)
            ? PayslipLineBuilder.DefaultCompanyName
            : payrollSettings.CompanyName;

        // Starts on the current half-month pay period: the 1st-15th, or the 16th-end.
        var today = DateTime.Today;
        var (startDay, endDay) = today.Day < 16 ? (1, 15) : (16, DateTime.DaysInMonth(today.Year, today.Month));
        Scope = new PayrollScopeState(
            new DateTime(today.Year, today.Month, startDay),
            new DateTime(today.Year, today.Month, endDay));

        Summary = new PayrollSummaryViewModel(
            selection, payrollComputationService, adjustmentRepository, undertimeWaiverRepository,
            statusBarService, busy, Scope, dataVersion, companyName);
        Group = new PayrollGroupViewModel(
            selection, rosterProvider, payrollComputationService, payrollRunRepository,
            statusBarService, busy, Scope, Summary, dataVersion);
        Run = new PayrollRunViewModel(
            selection, rosterProvider, payrollRunRepository, payrollComputationService,
            statusBarService, busy, Scope);
        PrintExport = new PayrollPrintExportViewModel(
            rosterProvider, payrollComputationService, statusBarService, busy, Scope, companyName);
    }

    /// <summary>The payroll period, the batch of employees in it, and the saved run it
    /// belongs to -- shared by all four children.</summary>
    public PayrollScopeState Scope { get; }

    public PayrollSummaryViewModel Summary { get; }

    public PayrollGroupViewModel Group { get; }

    public PayrollRunViewModel Run { get; }

    public PayrollPrintExportViewModel PrintExport { get; }

    /// <summary>Called on every visit to the Payroll page: picks up a schedule, holiday or
    /// attendance edit made on another page since the payslip and group were computed (see
    /// each child's own RecheckOnPageRevisitAsync).</summary>
    internal async Task RecheckOnPageRevisitAsync()
    {
        await Summary.RecheckOnPageRevisitAsync();
        await Group.RecheckOnPageRevisitAsync();
    }
}
