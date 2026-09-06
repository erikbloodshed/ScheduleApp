using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.Utilities;
using ScheduleApp.Desktop.Views;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>
/// Backs the Day Punch Pairing editor: one employee, one Flexible-schedule day,
/// that day's punches laid out as In/Out slots across an arbitrary number of
/// segment rows. Dragging a punch to another slot re-decides which punches form
/// which work interval -- the fix for a day that computed Partial because a
/// missed or duplicated tap left an orphan that time-order pairing couldn't
/// resolve.
///
/// Purely in-memory: the launcher (see IDayPunchPairingEditorLauncher) does the
/// loading and the repository write, the same division
/// ManualLogEntryDialog/ManualEntryEditorViewModel already use. This class owns
/// the grid state, the live Worked/Remainder/status preview, and turning the
/// finished grid into a <see cref="DayPunchPairing"/>.
///
/// The preview deliberately goes through the very same
/// <see cref="FlexiblePairingBuilder.BuildFromOverride"/> and
/// <see cref="FlexibleWorkedHours.Populate"/> the real calculation will use on
/// the next report run (see OverriddenFlexibleShiftCalculationStrategy), rather
/// than a lookalike sum of its own -- so what the footer says while dragging is
/// what the Summary grid will say after saving, including the Complete/Partial
/// verdict.
/// </summary>
public partial class DayPunchPairingEditorViewModel : ObservableObject
{
    private readonly ScheduleEntry _schedule;
    private readonly AttendancePolicy _policy;

    /// <summary>Every punch for the day, in timestamp order -- the full pool the
    /// grid is a layout of. A punch is always in exactly one cell, so the grid and
    /// this list stay in agreement, which is what lets
    /// <see cref="BuildOverrideMap"/> be total (and therefore the preview exact).
    /// Only the manual-punch commands below ever add to or remove from it, and each
    /// keeps a cell in step as it does.</summary>
    private readonly List<AttendanceLog> _dayPunches;

    /// <summary>Writes the Add/Edit/Delete Manual Punch commands make. Manual
    /// entries are their own table (see ManualAttendanceLog), separate from the
    /// pairing being edited here -- which is why those writes land immediately
    /// rather than waiting for Save: the punch is a record in its own right, and
    /// only *where it sits* is part of the pairing.</summary>
    private readonly IManualAttendanceLogRepository _manualLogRepository;

    /// <summary>The day's punch-search boundary, used to date a newly typed punch
    /// (see <see cref="ResolveTimestamp"/>) -- a Flexible day whose
    /// RestrictedTimeOut crosses midnight legitimately reaches into the next
    /// calendar day.</summary>
    private readonly (DateTime Start, DateTime End) _searchWindow;

    public DayPunchPairingEditorViewModel(
        Employee employee,
        ScheduleEntry schedule,
        IReadOnlyList<AttendanceLog> dayPunches,
        AttendancePolicy policy,
        DayPunchPairing? existingPairing,
        IManualAttendanceLogRepository manualLogRepository)
    {
        _schedule = schedule;
        _policy = policy;
        _manualLogRepository = manualLogRepository;
        _searchWindow = FlexiblePairingBuilder.SearchWindow(schedule);
        _dayPunches = dayPunches.OrderBy(p => p.Timestamp).ToList();

        EmployeeName = employee.DisplayName;
        EmployeePin = employee.Pin;
        Date = schedule.Date;
        DateText = schedule.Date.ToString("dddd, MMMM d, yyyy");
        HasSavedOverride = existingPairing is not null;

        RequiredText = schedule.WorkTimeHours is { } required
            ? $"{required:0.##} h"
            : "—";

        SeedRows(existingPairing);
        Recompute();
    }

    public string EmployeeName { get; }
    public int EmployeePin { get; }
    public DateOnly Date { get; }
    public string DateText { get; }
    public string RequiredText { get; }

    /// <summary>True when this day already had a saved pairing when the editor
    /// opened -- drives whether "Reset to Automatic" is offered at all.</summary>
    public bool HasSavedOverride { get; }

    public ObservableCollection<DayPunchPairingRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private string workedText = "—";

    [ObservableProperty]
    private string remainderText = "—";

    /// <summary>"Remaining" when short of the day's required hours, "Overtime"
    /// when past it -- the footer label next to <see cref="RemainderText"/>, so
    /// the same figure doesn't need two separate rows.</summary>
    [ObservableProperty]
    private string remainderLabel = "Remaining";

    [ObservableProperty]
    private PunchStatus previewStatus = PunchStatus.Absent;

    [ObservableProperty]
    private string previewStatusText = PunchStatus.Absent.ToText();

    /// <summary>How many punches are currently sitting in a half-open segment.
    /// Zero is what makes the day Complete.</summary>
    [ObservableProperty]
    private int unpairedCount;

    [ObservableProperty]
    private bool hasUnpairedPunches;

    // ---- Layout -------------------------------------------------------------

    /// <summary>
    /// Lay the day's punches out into rows. With a saved pairing, each punch goes
    /// to the segment/role it was saved with, and any punch that arrived since is
    /// auto-appended by timestamp -- both handled by
    /// <see cref="FlexiblePairingBuilder.PlaceFromOverride"/>, so the editor shows
    /// exactly what the calculation currently makes of this day. With no saved
    /// pairing, the grid opens on the default time-order pairing
    /// (<see cref="FlexiblePairingBuilder.BuildDefault"/>), which is what the day
    /// computed as -- so the person starts from the status quo and only has to
    /// change what's wrong.
    /// </summary>
    private void SeedRows(DayPunchPairing? existingPairing)
    {
        var cells = _dayPunches.ToDictionary(
            PunchKey.Of,
            p => new DayPunchPairingCellViewModel { Punch = p });

        if (existingPairing is not null)
        {
            var savedMap = existingPairing.Slots
                .GroupBy(s => new PunchKey(s.PunchId, s.IsManualPunch))
                .ToDictionary(g => g.Key, g => (g.Last().SegmentIndex, g.Last().Role));

            foreach (var segment in FlexiblePairingBuilder
                         .PlaceFromOverride(_dayPunches, savedMap)
                         .GroupBy(p => p.SegmentIndex)
                         .OrderBy(g => g.Key))
            {
                var ins = segment.Where(p => p.Role == PairingRole.In)
                    .OrderBy(p => p.Punch.Timestamp).ToList();
                var outs = segment.Where(p => p.Role == PairingRole.Out)
                    .OrderBy(p => p.Punch.Timestamp).ToList();

                // Normally one in and one out, i.e. one row. A segment holding
                // more than one of either can only come from auto-appended
                // punches piling into a row that was already full -- spill those
                // into extra rows rather than dropping them, so every punch stays
                // visible and draggable.
                int rowCount = Math.Max(Math.Max(ins.Count, outs.Count), 1);
                for (int i = 0; i < rowCount; i++)
                {
                    Rows.Add(new DayPunchPairingRowViewModel
                    {
                        InPunch = i < ins.Count ? cells[PunchKey.Of(ins[i].Punch)] : null,
                        OutPunch = i < outs.Count ? cells[PunchKey.Of(outs[i].Punch)] : null,
                    });
                }
            }
        }
        else
        {
            var defaultPairing = FlexiblePairingBuilder.BuildDefault(
                _dayPunches, _policy.FlexibleMinimumBreakGap);

            foreach (var pair in defaultPairing.Pairs)
            {
                Rows.Add(new DayPunchPairingRowViewModel
                {
                    InPunch = cells[PunchKey.Of(pair.In)],
                    OutPunch = cells[PunchKey.Of(pair.Out)],
                });
            }

            // The odd trailing punch the default rule couldn't pair -- the orphan.
            // It opens as an In with an empty Out beside it, which reads as
            // "this shift was never closed" and is exactly the slot someone
            // either drags a punch into or fills with a manual entry.
            foreach (var orphan in defaultPairing.Unpaired)
                Rows.Add(new DayPunchPairingRowViewModel { InPunch = cells[PunchKey.Of(orphan)] });
        }

        // Always leave one empty row at the bottom to drag into, so splitting a
        // segment never needs an Add Row click first.
        EnsureTrailingEmptyRow();
    }

    private void EnsureTrailingEmptyRow()
    {
        if (Rows.Count == 0 || !Rows[^1].IsEmpty)
            Rows.Add(new DayPunchPairingRowViewModel());
    }

    // ---- Drag & drop --------------------------------------------------------

    /// <summary>
    /// Move <paramref name="dragged"/> into <paramref name="targetRow"/>'s
    /// <paramref name="targetSlot"/>, swapping whatever was already there back
    /// into the slot the dragged cell came from. Modelled as a swap rather than
    /// an insert-and-shift so no punch is ever lost and every drop is reversible
    /// by dragging back -- and so dropping onto an empty slot simply empties the
    /// source one.
    /// </summary>
    public void MoveCell(
        DayPunchPairingCellViewModel dragged,
        DayPunchPairingRowViewModel targetRow,
        ColumnSlot targetSlot)
    {
        var source = Locate(dragged);
        if (source is null)
            return;

        var (sourceRow, sourceSlot) = source.Value;
        if (ReferenceEquals(sourceRow, targetRow) && sourceSlot == targetSlot)
            return;

        var displaced = targetRow[targetSlot];
        targetRow[targetSlot] = dragged;
        sourceRow[sourceSlot] = displaced;

        TrimEmptyRows();
        EnsureTrailingEmptyRow();
        Recompute();
    }

    private (DayPunchPairingRowViewModel Row, ColumnSlot Slot)? Locate(DayPunchPairingCellViewModel cell)
    {
        foreach (var row in Rows)
        {
            if (ReferenceEquals(row.InPunch, cell)) return (row, ColumnSlot.In);
            if (ReferenceEquals(row.OutPunch, cell)) return (row, ColumnSlot.Out);
        }
        return null;
    }

    /// <summary>Drop every empty row -- a swap that vacates a row shouldn't leave
    /// a gap in the middle of the grid. The trailing spare is re-added by
    /// <see cref="EnsureTrailingEmptyRow"/> right after.</summary>
    private void TrimEmptyRows()
    {
        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (Rows[i].IsEmpty)
                Rows.RemoveAt(i);
        }
    }

    [RelayCommand]
    private void AddRow()
    {
        Rows.Add(new DayPunchPairingRowViewModel());
        RemoveRowCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Removes the last row, but only while it's empty -- a row holding a
    /// punch can't be deleted, since that would drop the punch out of the grid
    /// entirely and there'd be nowhere to drag it back from.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveRow))]
    private void RemoveRow()
    {
        if (Rows.Count > 0 && Rows[^1].IsEmpty)
            Rows.RemoveAt(Rows.Count - 1);
        EnsureTrailingEmptyRow();
        RemoveRowCommand.NotifyCanExecuteChanged();
    }

    private bool CanRemoveRow() => Rows.Count > 1 && Rows[^1].IsEmpty;

    // ---- Manual punches -----------------------------------------------------

    /// <summary>True once any Add/Edit/Delete Manual Punch command has actually
    /// written something, so the launcher knows to bump
    /// <see cref="AttendanceDataVersion.ManualLogsVersion"/> as well as the pairing
    /// counter -- those writes hit ManualAttendanceLogs, which the Manual Entries
    /// grid and every report also read. Stays true even if the editor is then
    /// cancelled: the punch rows are already on file either way (see
    /// <see cref="_manualLogRepository"/>).</summary>
    public bool ManualPunchesChanged { get; private set; }

    /// <summary>
    /// Types a missing punch straight into an empty slot -- the other half of
    /// resolving a Partial day, alongside re-pairing the punches already there. An
    /// odd number of punches can never pair up completely no matter how they're
    /// arranged, so without this the editor could only ever redistribute the
    /// orphan, not close the gap that caused it.
    ///
    /// Writes the ManualAttendanceLog immediately (see
    /// <see cref="_manualLogRepository"/>) rather than staging it until Save: a
    /// slot in the saved pairing refers to a punch by id, and there's no id to
    /// refer to until the row exists. That the punch outlives a cancelled edit is
    /// correct, not a leak -- it's an independent record of "this person was here
    /// at this time," and the same thing happens today when Add Manual Entry… is
    /// used from anywhere else.
    /// </summary>
    public async Task AddManualPunchAsync(DayPunchPairingRowViewModel row, ColumnSlot slot)
    {
        if (row[slot] is not null)
            return; // occupied -- Add is only offered on an empty slot

        var dialog = new PunchTimeEntryDialog(
            EmployeeName, Date, SlotLabel(slot), SuggestTimeFor(row, slot))
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true)
            return;

        var saved = await _manualLogRepository.AddAsync(new ManualAttendanceLog
        {
            EmployeeId = EmployeePin,
            Timestamp = ResolveTimestamp(dialog.TimeOfDay),
            // 0 = Clock In, 1 = Clock Out -- taken from the slot being filled rather
            // than asked for, since the column already says which it is. Matching
            // punch types isn't what pairing keys off (see ManualAttendanceLog.PunchType),
            // but a person reading the raw entry later should still see the right one.
            PunchType = slot == ColumnSlot.In ? 0 : 1,
            Reason = dialog.Reason,
            EnteredBy = dialog.EnteredBy,
        });

        var punch = saved.ToAttendanceLog();
        _dayPunches.Add(punch);
        _dayPunches.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        row[slot] = new DayPunchPairingCellViewModel { Punch = punch };
        ManualPunchesChanged = true;

        EnsureTrailingEmptyRow();
        Recompute();
    }

    /// <summary>Corrects a time someone typed earlier. Offered only for a manual
    /// punch: a device punch is what the clock actually reported and stays
    /// untouched (the same rule the Punch Records grid enforces by having no Edit
    /// button for one) -- see ManualAttendanceLog's own doc comment.</summary>
    public async Task EditManualPunchAsync(DayPunchPairingCellViewModel cell)
    {
        if (!cell.IsManual)
            return;

        var located = Locate(cell);
        var slotLabel = located is { } spot ? SlotLabel(spot.Slot) : "Punch";

        var existing = new ManualAttendanceLog
        {
            Id = cell.Punch.Id,
            EmployeeId = cell.Punch.EmployeeId,
            Timestamp = cell.Punch.Timestamp,
            PunchType = cell.Punch.PunchType,
            Reason = cell.Punch.Reason ?? string.Empty,
            EnteredBy = cell.Punch.EnteredBy ?? string.Empty,
        };

        var dialog = new PunchTimeEntryDialog(
            EmployeeName, Date, slotLabel, TimeOnly.FromDateTime(cell.Punch.Timestamp), existing)
        {
            Owner = Application.Current.MainWindow,
        };
        if (dialog.ShowDialog() != true)
            return;

        existing.Timestamp = ResolveTimestamp(dialog.TimeOfDay);
        existing.Reason = dialog.Reason;
        existing.EnteredBy = dialog.EnteredBy;
        await _manualLogRepository.UpdateAsync(existing);

        // Mutated in place rather than rebuilt: the cell (and the saved pairing's
        // slot) refer to this exact punch instance/id, so replacing it would mean
        // re-threading both. Only the fields the dialog can change are touched.
        cell.Punch.Timestamp = existing.Timestamp;
        cell.Punch.Reason = existing.Reason;
        cell.Punch.EnteredBy = existing.EnteredBy;
        cell.NotifyPunchChanged();

        _dayPunches.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        ManualPunchesChanged = true;
        Recompute();
    }

    /// <summary>Removes a manual punch entirely -- for one typed by mistake. Same
    /// manual-only restriction as <see cref="EditManualPunchAsync"/>, and the same
    /// reason: AttendanceLogs is meant to stay an untouched record of what the
    /// clock reported, while a manual entry is this app's own typed data.</summary>
    public async Task DeleteManualPunchAsync(DayPunchPairingCellViewModel cell)
    {
        if (!cell.IsManual)
            return;

        var confirm = MessageBox.Show(
            $"Delete the manual {cell.TimeText} punch for {EmployeeName} on {Date:MMM d, yyyy}?",
            "Delete manual punch", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        await _manualLogRepository.DeleteAsync(cell.Punch.Id);

        if (Locate(cell) is { } spot)
            spot.Row[spot.Slot] = null;
        _dayPunches.RemoveAll(p => ReferenceEquals(p, cell.Punch));
        ManualPunchesChanged = true;

        TrimEmptyRows();
        EnsureTrailingEmptyRow();
        Recompute();
    }

    private static string SlotLabel(ColumnSlot slot) => slot == ColumnSlot.In ? "Time In" : "Time Out";

    /// <summary>A starting time for a punch being typed into an empty slot: just
    /// after the row's own counterpart when it has one (filling an Out beside a
    /// 1:04 PM In suggests 1:04 PM to adjust from, which beats midnight), otherwise
    /// the day's last punch, otherwise nothing.</summary>
    private TimeOnly? SuggestTimeFor(DayPunchPairingRowViewModel row, ColumnSlot slot)
    {
        var counterpart = slot == ColumnSlot.In ? row.OutPunch : row.InPunch;
        if (counterpart is { } other)
            return TimeOnly.FromDateTime(other.Punch.Timestamp);

        return _dayPunches.Count > 0
            ? TimeOnly.FromDateTime(_dayPunches[^1].Timestamp)
            : null;
    }

    /// <summary>Which calendar day a typed time belongs to. Normally the schedule's
    /// own date, but a Flexible day whose RestrictedTimeOut crosses midnight has a
    /// search window reaching into the next day -- so a 2:00 AM badge-out for a
    /// shift that started the previous evening lands there instead. Decided by
    /// asking which of the two candidates the window actually admits, rather than
    /// by a separate rule that could drift from
    /// <see cref="FlexiblePairingBuilder.SearchWindow"/>.</summary>
    private DateTime ResolveTimestamp(TimeOnly time)
    {
        var sameDay = Date.ToDateTime(time);
        if (sameDay >= _searchWindow.Start && sameDay <= _searchWindow.End)
            return sameDay;

        var nextDay = Date.AddDays(1).ToDateTime(time);
        return nextDay >= _searchWindow.Start && nextDay <= _searchWindow.End ? nextDay : sameDay;
    }

    // ---- Live preview -------------------------------------------------------

    /// <summary>
    /// Re-derives the footer figures and every cell's unpaired marker from the
    /// grid as it stands, through the real calculation path. Called after every
    /// move; cheap enough to do eagerly (a day has a handful of punches).
    /// </summary>
    private void Recompute()
    {
        var pairing = FlexiblePairingBuilder.BuildFromOverride(_dayPunches, BuildOverrideMap());

        var unpairedKeys = pairing.Unpaired.Select(PunchKey.Of).ToHashSet();
        foreach (var row in Rows)
        {
            if (row.InPunch is { } inCell) inCell.IsUnpaired = unpairedKeys.Contains(inCell.Key);
            if (row.OutPunch is { } outCell) outCell.IsUnpaired = unpairedKeys.Contains(outCell.Key);
        }

        UnpairedCount = pairing.Unpaired.Count;
        HasUnpairedPunches = UnpairedCount > 0;

        if (_dayPunches.Count == 0)
        {
            // Matches OverriddenFlexibleShiftCalculationStrategy's own no-punch
            // short circuit -- a day with nothing on it is Absent, not an empty
            // Partial.
            WorkedText = "—";
            RemainderText = "—";
            RemainderLabel = "Remaining";
            SetPreviewStatus(PunchStatus.Absent);
            return;
        }

        var preview = new AttendanceSummary { Span = _schedule.WorkTimeHours };
        FlexibleWorkedHours.Populate(preview, pairing, _schedule, _policy, _dayPunches);

        WorkedText = FormatDuration(preview.Worked_T);
        if (preview.Overtime_T > TimeSpan.Zero)
        {
            RemainderLabel = "Overtime";
            RemainderText = FormatDuration(preview.Overtime_T);
        }
        else
        {
            RemainderLabel = "Remaining";
            RemainderText = preview.Remain_T > TimeSpan.Zero ? FormatDuration(preview.Remain_T) : "—";
        }

        SetPreviewStatus(preview.Status);
    }

    private void SetPreviewStatus(PunchStatus status)
    {
        PreviewStatus = status;
        PreviewStatusText = status.ToText();
    }

    /// <summary>The grid as the map
    /// <see cref="FlexiblePairingBuilder.BuildFromOverride"/> and a saved
    /// <see cref="DayPunchPairing"/> both speak: punch -&gt; (row index,
    /// In/Out).</summary>
    private Dictionary<PunchKey, (int Segment, PairingRole Role)> BuildOverrideMap()
    {
        var map = new Dictionary<PunchKey, (int Segment, PairingRole Role)>();
        for (int i = 0; i < Rows.Count; i++)
        {
            if (Rows[i].InPunch is { } inCell) map[inCell.Key] = (i, PairingRole.In);
            if (Rows[i].OutPunch is { } outCell) map[outCell.Key] = (i, PairingRole.Out);
        }
        return map;
    }

    // ---- Saving -------------------------------------------------------------

    /// <summary>Turns the finished grid into the row the repository persists. One
    /// slot per filled cell; an empty cell simply isn't represented, which is what
    /// makes a half-open segment survive a round trip as a half-open
    /// segment.</summary>
    public DayPunchPairing BuildPairing(string editedBy) => new()
    {
        EmployeeId = EmployeePin,
        Date = Date,
        EditedBy = editedBy,
        Slots = BuildOverrideMap()
            .Select(entry => new DayPunchPairingSlot
            {
                PunchId = entry.Key.PunchId,
                IsManualPunch = entry.Key.IsManual,
                SegmentIndex = entry.Value.Segment,
                Role = entry.Value.Role,
            })
            .OrderBy(s => s.SegmentIndex)
            .ThenBy(s => s.Role)
            .ToList(),
    };

    // ---- Formatting ---------------------------------------------------------

    // Same "[h]:mm" elapsed-hours shape as AttendanceSummaryRow's own duration
    // columns, so a figure here reads identically to the same figure on the
    // Summary grid.
    private static string FormatDuration(TimeSpan span)
    {
        var abs = span.Duration();
        return $"{(span < TimeSpan.Zero ? "-" : "")}{(int)abs.TotalHours}:{abs.Minutes:00}";
    }
}
