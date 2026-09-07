using System.Windows;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;

namespace ScheduleApp.Desktop.Services;

/// <summary>
/// Opens the Day Punch Pairing editor for one employee/day and persists whatever
/// comes back. Exists so the two entry points -- the Attendance Summary grid's
/// row context menu and the Schedule page's calendar tile menu -- share one
/// gather/show/save path instead of each growing four repository dependencies and
/// its own copy of the loading rules.
///
/// Same division of labour as ManualEntryEditorViewModel/ManualLogEntryDialog: the
/// dialog collects an intent, this does the database work and the
/// <see cref="AttendanceDataVersion"/> bump that makes the Summary grid (and the
/// Schedule calendar's status markers) pick the change up.
/// </summary>
public interface IDayPunchPairingEditorLauncher
{
    /// <summary>Returns true when something was actually written (a pairing saved
    /// or reset, or a manual punch added/corrected on an editable non-Flexible day),
    /// so a caller that needs to refresh its own view -- the Schedule calendar,
    /// which isn't driven by AttendanceDataVersion the way the Summary grid is --
    /// knows whether it has to.
    ///
    /// <paramref name="attendanceStatus"/> is the day's computed status as the
    /// caller already has it (CalendarDayViewModel.AttendanceStatus /
    /// AttendanceSummaryRow.Status). It decides whether a non-Flexible day opens
    /// editable (Partial/Absent -- worth fixing) or read-only; pass null when it
    /// isn't known, which is treated as read-only for a non-Flexible day.</summary>
    Task<bool> OpenAsync(Employee employee, DateOnly date, PunchStatus? attendanceStatus, CancellationToken cancellationToken = default);
}

public sealed class DayPunchPairingEditorLauncher(
    IScheduleRepository scheduleRepository,
    IAttendanceLogRepository attendanceLogRepository,
    IManualAttendanceLogRepository manualAttendanceLogRepository,
    IDayPunchPairingRepository dayPunchPairingRepository,
    // AttendanceSettings, not AttendancePolicy -- only the settings object is
    // registered in App.xaml.cs; every other consumer reaches the policy through
    // settings.Policy the same way (see AttendanceViewModel).
    AttendanceSettings attendanceSettings,
    AttendanceDataVersion dataVersion,
    IStatusBarService statusBarService) : IDayPunchPairingEditorLauncher
{
    private readonly AttendancePolicy _policy = attendanceSettings.Policy;


    public async Task<bool> OpenAsync(
        Employee employee, DateOnly date, PunchStatus? attendanceStatus,
        CancellationToken cancellationToken = default)
    {
        var entries = await scheduleRepository.GetScheduleEntriesForPeriodAsync(
            date, date, new HashSet<int> { employee.Pin }, cancellationToken);
        var schedule = entries.FirstOrDefault(s => s.EmployeeId == employee.Pin && s.Date == date);

        if (schedule is null)
        {
            statusBarService.ShowCaution(
                $"{employee.DisplayName} has no schedule on {date:MMM d, yyyy}, so there's no pairing to edit.",
                "No schedule");
            return false;
        }

        // A hand-edited pairing only changes the computed result for a Flexible day
        // -- every other type matches punches against a fixed scheduled window
        // rather than by time order, so AttendanceCalculator.CalculateShift ignores
        // an override for them, and this launcher never persists one (see the Save
        // branch below). The dialog still opens on any scheduled day: for a
        // non-Flexible day that came out Partial/Absent it's a working editor
        // (adding or correcting a punch there does fix the day -- a manual punch
        // feeds every calculation path), and for any other non-Flexible day it's a
        // read-only "View Punches…" viewer. The view model works all this out from
        // the schedule type and attendanceStatus -- see its constructor,
        // IsReadOnly, and PairingNote.
        var pairingAffectsResult = schedule.ScheduleType == ScheduleType.Flexible;

        // The day's punch pool, merged exactly the way AttendanceWorkflowService
        // merges it (device rows plus manual entries projected through
        // ToAttendanceLog) so the editor is looking at the same punches the
        // calculation will. Fetched over the schedule's own search window rather
        // than a bare calendar day, so a RestrictedTimeOut reaching past midnight
        // brings its punches along.
        var (searchStart, searchEnd) = ScheduleApp.Attendance.FlexiblePairingBuilder.SearchWindow(schedule);
        int[] pins = [employee.Pin];
        var deviceLogs = await attendanceLogRepository.GetLogsAsync(searchStart, searchEnd, pins, cancellationToken);
        var manualLogs = await manualAttendanceLogRepository.GetLogsAsync(searchStart, searchEnd, pins, cancellationToken);
        var dayPunches = deviceLogs
            .Concat(manualLogs.Select(m => m.ToAttendanceLog()))
            .OrderBy(p => p.Timestamp)
            .ToList();

        var existing = await dayPunchPairingRepository.GetAsync(employee.Pin, date, cancellationToken);

        var editor = new DayPunchPairingEditorViewModel(
            employee, schedule, dayPunches, _policy, existing, manualAttendanceLogRepository,
            attendanceStatus);
        var dialog = new DayPunchPairingDialog(editor) { Owner = Application.Current.MainWindow };

        var dialogResult = dialog.ShowDialog();

        // Adding, correcting, or deleting a manual punch inside the editor -- which
        // every mode except the read-only viewer allows -- writes to
        // ManualAttendanceLogs immediately (see
        // DayPunchPairingEditorViewModel.AddManualPunchAsync for why it can't wait for
        // Save), so those rows are on file whether or not the pairing itself was saved
        // -- and the Summary grid, the Manual Entries grid and every report read that
        // table. Bumped before the Cancel check below for exactly that reason.
        if (editor.ManualPunchesChanged)
            dataVersion.BumpManualLogs();

        if (dialogResult != true)
            return editor.ManualPunchesChanged;

        if (dialog.Outcome == DayPunchPairingDialogOutcome.ResetToAutomatic)
        {
            // Only reachable on a Flexible day -- the dialog shows "Reset to
            // Automatic" only when a pairing is actually read back (see
            // DayPunchPairingDialog) -- so this always clears a live override
            // rather than a stale one.
            await dayPunchPairingRepository.DeleteAsync(employee.Pin, date, cancellationToken);
            dataVersion.BumpPairings();
            statusBarService.ShowSuccess(
                $"Punch pairing for {employee.DisplayName} on {date:MMM d, yyyy} reset to automatic.");
            return true;
        }

        if (!pairingAffectsResult)
        {
            // Non-Flexible: no pairing to persist. The calculator ignores a pairing
            // row for these types, and a stored one could silently reactivate if the
            // day ever became Flexible. The editable (Partial/Absent) variant may
            // still have had a manual punch added or corrected -- that's already
            // written, and ManualPunchesChanged tells the caller to refresh.
            return editor.ManualPunchesChanged;
        }

        // EnteredBy has no user/auth system behind it -- the Windows username is
        // the same "just a typed name" default ManualLogEntryDialog uses.
        await dayPunchPairingRepository.SaveAsync(
            editor.BuildPairing(Environment.UserName), cancellationToken);
        dataVersion.BumpPairings();

        var statusNote = editor.PreviewStatus == PunchStatus.Complete
            ? "now Complete"
            : $"still {editor.PreviewStatus.ToText()}";
        statusBarService.ShowSuccess(
            $"Saved punch pairing for {employee.DisplayName} on {date:MMM d, yyyy} — {statusNote}.");
        return true;
    }
}
