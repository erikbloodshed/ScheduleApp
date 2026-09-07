using System.Collections.ObjectModel;
using System.Windows;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
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

    /// <summary>Grid-arrangement history for <see cref="UndoCommand"/> -- one entry
    /// per drop, Add Segment, or Remove Empty, captured just before the change. A
    /// drop cascades punches forward (see <see cref="MoveCell"/>) instead of swapping
    /// two, so "drag it back" is no longer a way to undo; this is. Backed by a list
    /// used as a stack (newest last) so the oldest entry can be dropped once it's
    /// full. Cleared whenever a manual punch is added or deleted -- those hit the
    /// database, which Undo can't reverse (see <see cref="AddManualPunchAsync"/>).</summary>
    private readonly List<GridSnapshot> _undoStack = [];

    private const int MaxUndoDepth = 50;

    /// <summary>One remembered grid arrangement: how many rows there were (so empty
    /// working segments are restored, not just punch positions -- an empty row names
    /// no punch, so the placement map alone can't represent it) and where each punch
    /// sat, in the exact shape <see cref="BuildOverrideMap"/> produces.</summary>
    private sealed record GridSnapshot(
        int RowCount,
        IReadOnlyDictionary<PunchKey, (int Segment, PairingRole Role)> Placement);

    public DayPunchPairingEditorViewModel(
        Employee employee,
        ScheduleEntry schedule,
        IReadOnlyList<AttendanceLog> dayPunches,
        AttendancePolicy policy,
        DayPunchPairing? existingPairing,
        IManualAttendanceLogRepository manualLogRepository,
        PunchStatus? attendanceStatus)
    {
        _schedule = schedule;
        _policy = policy;
        _manualLogRepository = manualLogRepository;
        _searchWindow = FlexiblePairingBuilder.SearchWindow(schedule);
        _dayPunches = dayPunches.OrderBy(p => p.Timestamp).ToList();

        EmployeeName = employee.DisplayName;
        EmployeePin = employee.Pin;
        Date = schedule.Date;
        DateText = schedule.Date.ToString("dddd, MMMM d, yyyy", CultureInfo.CurrentCulture);
        HasSavedOverride = existingPairing is not null;

        RequiredText = schedule.WorkTimeHours is { } required
            ? $"{required:0.##} h"
            : "—";

        // Two flags, and they don't always coincide:
        //
        //  * PairingAffectsResult -- Flexible only. Only a Flexible day's pairing is
        //    read back by the calculation (AttendanceCalculator.CalculateShift
        //    ignores an override for every other type, which matches punches against
        //    a fixed scheduled window instead). It drives whether Save persists
        //    anything and whether the footer shows a real verdict or just a punch
        //    count; DayPunchPairingEditorLauncher skips the write when it's false.
        //
        //  * IsReadOnly -- whether the grid can be touched at all. A Flexible day is
        //    always editable. A *non*-Flexible day is editable when there's something
        //    to work on: it opened Partial or Absent (a missing punch to add), or a
        //    hand-entered punch is in its pool (one that might still need correcting
        //    or removing, even once the day reads Complete because of it). Dragging,
        //    adding, or correcting a punch is then legitimate -- a manual punch feeds
        //    every calculation path; the re-pairing itself still isn't saved. A
        //    non-Flexible day that's Complete on device punches alone, or Leave/OB
        //    with nothing hand-entered, is a pure viewer.
        PairingAffectsResult = schedule.ScheduleType == ScheduleType.Flexible;
        var dayNeedsWork = attendanceStatus is PunchStatus.Partial or PunchStatus.Absent;
        var hasManualPunch = _dayPunches.Any(p => p.Source == AttendanceLogSource.Manual);
        IsReadOnly = !PairingAffectsResult && !dayNeedsWork && !hasManualPunch;

        ScheduleTypeText = schedule.ScheduleType.ToText();
        PairingNote = IsReadOnly
            ? $"A {ScheduleTypeText} day is matched against its scheduled window, not by " +
              "pairing. This is a read-only view -- use \"Add Manual Entry…\" on the day to " +
              "add or correct a punch."
            : $"A {ScheduleTypeText} day is matched against its scheduled window, so re-pairing " +
              "here isn't saved. Adding, correcting, or removing a punch does fix the day.";

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

    /// <summary>See the constructor -- true only for a Flexible day, the one type
    /// whose pairing the calculation reads back. Drives whether the dialog offers
    /// Save at all and whether the footer shows the live preview or just a punch
    /// count.</summary>
    public bool PairingAffectsResult { get; }

    /// <summary>True when the dialog is a pure viewer -- no drag, no Add/Edit/Delete
    /// of punches, no Add Segment/Remove Empty/Undo. Set only for a non-Flexible day
    /// whose attendance status is something other than Partial/Absent; a Flexible
    /// day, or a broken non-Flexible day, stays editable (see the constructor).
    /// Distinct from <see cref="PairingAffectsResult"/>: a non-Flexible Partial/
    /// Absent day is editable here yet still persists no pairing. Enforced in the
    /// view (hidden toolbar, suppressed gestures) and again in every mutator here as
    /// a backstop.</summary>
    public bool IsReadOnly { get; }

    /// <summary>The day's ScheduleType label (see ScheduleTypeLabel.ToText).</summary>
    public string ScheduleTypeText { get; }

    /// <summary>The footer line shown in place of the preview when
    /// <see cref="PairingAffectsResult"/> is false -- says why there's no verdict
    /// here and what still counts.</summary>
    public string PairingNote { get; }

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

    /// <summary>"3 punches recorded" -- what the footer shows in place of the
    /// Worked/Required/status preview on a day whose pairing isn't read back (see
    /// <see cref="PairingAffectsResult"/>), where those figures would be computed
    /// with rules that day isn't actually calculated by.</summary>
    [ObservableProperty]
    private string punchCountText = "No punches recorded";

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
        OnRowsChanged();
    }

    private void EnsureTrailingEmptyRow()
    {
        if (Rows.Count == 0 || !Rows[^1].IsEmpty)
            Rows.Add(new DayPunchPairingRowViewModel());
    }

    /// <summary>Run after any change to <see cref="Rows"/>' shape (a drop, Add
    /// Segment, Remove Empty, Undo, a manual punch add/delete): re-establish the
    /// trailing spare, then re-query the two commands whose CanExecute depends on
    /// how many rows -- and how many empty ones -- there are now. CommunityToolkit
    /// doesn't observe <see cref="Rows"/>.Count, so this is the only thing keeping
    /// "Remove Empty" and "Undo" enabled/disabled correctly after a drag.</summary>
    private void OnRowsChanged()
    {
        EnsureTrailingEmptyRow();
        RemoveEmptySegmentsCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
    }

    // ---- Drag & drop --------------------------------------------------------

    /// <summary>
    /// Drop <paramref name="dragged"/> into <paramref name="targetRow"/>'s
    /// <paramref name="targetSlot"/>. An empty target is a plain move -- the punch
    /// just parks there, nothing else shifts, which is how a gap gets used as
    /// working space. An occupied target is an *insert*: the punch already there is
    /// pushed to the next slot in reading order (In then Out within a row, then the
    /// next row's In), and the one after that, cascading until an empty slot absorbs
    /// it -- appending a fresh row if the cascade runs off the end.
    ///
    /// Not a swap: the dragged punch's old slot is simply vacated and left empty, so
    /// a run of drops keeps its intent instead of two punches ping-ponging. The
    /// row-append rule is what carries the old swap model's "no punch is ever lost"
    /// guarantee -- a cascade can always find somewhere to put what it's carrying.
    /// <see cref="UndoCommand"/> replaces "drag it back" as the way out.
    /// </summary>
    public void MoveCell(
        DayPunchPairingCellViewModel dragged,
        DayPunchPairingRowViewModel targetRow,
        ColumnSlot targetSlot)
    {
        if (IsReadOnly)
            return; // backstop -- the view suppresses the drag gesture too

        var source = Locate(dragged);
        if (source is null)
            return;

        var (sourceRow, sourceSlot) = source.Value;
        if (ReferenceEquals(sourceRow, targetRow) && sourceSlot == targetSlot)
            return;

        PushUndoSnapshot();

        // Vacate the source first -- wherever the cascade below sends things, the
        // dragged punch has left its old slot.
        sourceRow[sourceSlot] = null;

        var occupant = targetRow[targetSlot];
        targetRow[targetSlot] = dragged;

        if (occupant is not null)
            CascadeForward(occupant, Rows.IndexOf(targetRow), targetSlot);

        OnRowsChanged();
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

    /// <summary>The slot immediately after (<paramref name="rowIndex"/>,
    /// <paramref name="slot"/>) in reading order: In -&gt; Out within a row, then the
    /// next row's In. Null once past the last row's Out -- callers append a row there
    /// rather than let a cascaded punch fall out of the grid.</summary>
    private (int RowIndex, ColumnSlot Slot)? NextSlot(int rowIndex, ColumnSlot slot)
    {
        if (slot == ColumnSlot.In)
            return (rowIndex, ColumnSlot.Out);
        return rowIndex + 1 < Rows.Count ? (rowIndex + 1, ColumnSlot.In) : null;
    }

    /// <summary>Ripple <paramref name="carry"/> into the slots after
    /// (<paramref name="fromRow"/>, <paramref name="fromSlot"/>) in reading order:
    /// each occupied slot hands its own punch to <paramref name="carry"/> and takes
    /// the previous one, until an empty slot ends the chain. A carry that reaches
    /// past the last slot gets a new row -- an insert never pushes a punch out of
    /// the grid, which is also what guarantees this terminates.</summary>
    private void CascadeForward(DayPunchPairingCellViewModel carry, int fromRow, ColumnSlot fromSlot)
    {
        var cursor = NextSlot(fromRow, fromSlot);
        while (true)
        {
            int r;
            ColumnSlot s;
            if (cursor is null)
            {
                Rows.Add(new DayPunchPairingRowViewModel());
                (r, s) = (Rows.Count - 1, ColumnSlot.In);
            }
            else
            {
                (r, s) = cursor.Value;
            }

            var occupant = Rows[r][s];
            Rows[r][s] = carry;
            if (occupant is null)
                return;

            carry = occupant;
            cursor = NextSlot(r, s);
        }
    }

    private void PushUndoSnapshot()
    {
        _undoStack.Add(new GridSnapshot(Rows.Count, BuildOverrideMap()));
        if (_undoStack.Count > MaxUndoDepth)
            _undoStack.RemoveAt(0);
    }

    [RelayCommand]
    private void AddRow()
    {
        PushUndoSnapshot();
        Rows.Add(new DayPunchPairingRowViewModel());
        OnRowsChanged();
    }

    /// <summary>Drops every empty segment in one go, keeping a single trailing spare.
    /// This is the manual cleanup for the working rows a drag leaves behind now that
    /// a vacated row stays put (see <see cref="MoveCell"/>) instead of collapsing
    /// under the pointer. Bound to the "Remove Empty" button.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveEmptySegments))]
    private void RemoveEmptySegments()
    {
        PushUndoSnapshot();

        for (int i = Rows.Count - 1; i >= 0; i--)
        {
            if (Rows[i].IsEmpty)
                Rows.RemoveAt(i);
        }

        // No Recompute: an empty row names no punch, so removing one can't change
        // the pairing or the preview -- only the row indices, which BuildOverrideMap
        // renumbers on save anyway.
        OnRowsChanged();
    }

    /// <summary>Enabled only when removing would actually change the grid -- there's
    /// an empty row that isn't just the trailing spare (two or more empties, or a
    /// single one that isn't the last row).</summary>
    private bool CanRemoveEmptySegments()
    {
        int empties = Rows.Count(r => r.IsEmpty);
        if (empties == 0)
            return false;
        if (empties == 1)
            return !Rows[^1].IsEmpty;
        return true;
    }

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
        if (IsReadOnly)
            return; // backstop -- the view offers no way to reach this
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

        // A new punch in the pool: earlier snapshots don't mention it, so an undo
        // to one of them could only place it by the safety-net rule (see
        // RestoreGrid) rather than truly reverse anything. Adding or deleting a
        // punch starts a fresh history.
        _undoStack.Clear();
        OnRowsChanged();
        Recompute();
    }

    /// <summary>Corrects a time someone typed earlier. Offered only for a manual
    /// punch: a device punch is what the clock actually reported and stays
    /// untouched (the same rule the Punch Records grid enforces by having no Edit
    /// button for one) -- see ManualAttendanceLog's own doc comment.</summary>
    public async Task EditManualPunchAsync(DayPunchPairingCellViewModel cell)
    {
        if (IsReadOnly || !cell.IsManual)
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
        if (IsReadOnly || !cell.IsManual)
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

        // The row this punch was in stays put, empty -- "Remove Empty" clears it.
        // History is dropped for the same reason as the add path above: a snapshot
        // that still references this punch can no longer be honoured cleanly.
        _undoStack.Clear();
        OnRowsChanged();
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

        // Set before the no-punch short circuit below -- it's the whole of the
        // footer on a day where PairingAffectsResult is false, empty or not.
        PunchCountText = _dayPunches.Count switch
        {
            0 => "No punches recorded",
            1 => "1 punch recorded",
            var count => $"{count} punches recorded",
        };

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

        WorkedText = FormatDuration(preview.WorkedDuration);
        if (preview.OvertimeDuration > TimeSpan.Zero)
        {
            RemainderLabel = "Overtime";
            RemainderText = FormatDuration(preview.OvertimeDuration);
        }
        else
        {
            RemainderLabel = "Remaining";
            RemainderText = preview.RemainDuration > TimeSpan.Zero ? FormatDuration(preview.RemainDuration) : "—";
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
    /// In/Out). Row index is the literal <see cref="Rows"/> position -- gaps and
    /// all, since empty working rows count here; <see cref="BuildPairing"/>
    /// renumbers to a dense sequence only when persisting.</summary>
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

    // ---- Undo -------------------------------------------------------------------

    /// <summary>Steps the grid back one arrangement change -- a drop, Add Segment,
    /// or Remove Empty. No redo; the history is cleared whenever a manual punch is
    /// added or deleted (see <see cref="AddManualPunchAsync"/>), since those hit the
    /// database and Undo can't reverse them. Cancelling the dialog still throws the
    /// whole layout away regardless -- this is for stepping back mid-edit.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_undoStack.Count == 0)
            return;

        var snapshot = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);

        RestoreGrid(snapshot);

        OnRowsChanged();
        Recompute();
    }

    private bool CanUndo() => _undoStack.Count > 0;

    /// <summary>Rebuilds <see cref="Rows"/> from a snapshot: the recorded row count
    /// (so empty working segments come back, not just punch positions), each named
    /// punch back in its slot, and -- the safety net -- any punch the snapshot
    /// doesn't mention dropped into the first free slot so it can never vanish from
    /// the grid. Fresh cells throughout, since the old ones are discarded with the
    /// old rows; a punch is still identified by <see cref="PunchKey"/>, not by cell
    /// reference.</summary>
    private void RestoreGrid(GridSnapshot snapshot)
    {
        var cells = _dayPunches.ToDictionary(
            PunchKey.Of,
            p => new DayPunchPairingCellViewModel { Punch = p });

        Rows.Clear();
        for (int i = 0; i < snapshot.RowCount; i++)
            Rows.Add(new DayPunchPairingRowViewModel());

        var placed = new HashSet<PunchKey>();
        foreach (var entry in snapshot.Placement)
        {
            var key = entry.Key;
            var (segment, role) = entry.Value;

            if (!cells.TryGetValue(key, out var cell))
                continue;                                  // punch gone since the snapshot
            if (segment < 0 || segment >= Rows.Count)
                continue;                                  // row count shrank since the snapshot
            var slot = role == PairingRole.In ? ColumnSlot.In : ColumnSlot.Out;
            if (Rows[segment][slot] is not null)
                continue;                                  // slot already taken -- shouldn't happen

            Rows[segment][slot] = cell;
            placed.Add(key);
        }

        // Anything the snapshot didn't place (a punch added after it was taken, or
        // dropped by one of the guards above) still has to be somewhere visible and
        // draggable -- restore is not a way for a punch to leave the grid.
        foreach (var punch in _dayPunches)
        {
            var key = PunchKey.Of(punch);
            if (!placed.Contains(key))
                PlaceInFirstFreeSlot(cells[key]);
        }
    }

    /// <summary>Drop <paramref name="cell"/> into the first empty slot in reading
    /// order (In before Out, top row down), adding a row if every slot is
    /// full.</summary>
    private void PlaceInFirstFreeSlot(DayPunchPairingCellViewModel cell)
    {
        for (int r = 0; r < Rows.Count; r++)
        {
            if (Rows[r].InPunch is null) { Rows[r].InPunch = cell; return; }
            if (Rows[r].OutPunch is null) { Rows[r].OutPunch = cell; return; }
        }
        Rows.Add(new DayPunchPairingRowViewModel { InPunch = cell });
    }

    // ---- Saving -------------------------------------------------------------

    /// <summary>Turns the finished grid into the row the repository persists. One
    /// slot per filled cell; an empty cell simply isn't represented, which is what
    /// makes a half-open segment survive a round trip as a half-open segment.
    ///
    /// Segment indices are renumbered to a dense 0..N-1 here. Empty working rows
    /// carry no punch and so aren't stored -- without renumbering, a grid with an
    /// empty row in the middle would save with a hole in its index sequence (rows
    /// 0, 1, 3), and a reopen -- which rebuilds rows by grouping stored indices --
    /// would silently collapse and renumber it anyway. Renumbering on the way out
    /// makes what's stored match what a reopen shows, so save -&gt; reopen -&gt; save
    /// is a genuine no-op. Half-open segments are kept exactly as they sit; only
    /// fully empty rows drop out. Nothing is merged.</summary>
    public DayPunchPairing BuildPairing(string editedBy)
    {
        var map = BuildOverrideMap();

        var denseIndex = map.Values
            .Select(v => v.Segment)
            .Distinct()
            .OrderBy(segment => segment)
            .Select((segment, ordinal) => (segment, ordinal))
            .ToDictionary(x => x.segment, x => x.ordinal);

        return new DayPunchPairing
        {
            EmployeeId = EmployeePin,
            Date = Date,
            EditedBy = editedBy,
            Slots = map
                .Select(entry => new DayPunchPairingSlot
                {
                    PunchId = entry.Key.PunchId,
                    IsManualPunch = entry.Key.IsManual,
                    SegmentIndex = denseIndex[entry.Value.Segment],
                    Role = entry.Value.Role,
                })
                .OrderBy(s => s.SegmentIndex)
                .ThenBy(s => s.Role)
                .ToList(),
        };
    }

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
