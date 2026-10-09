using System.Reactive.Linq;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Desktop.Utilities;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>
/// One draggable punch card in the Day Punch Pairing editor. Wraps a single
/// <see cref="AttendanceLog"/> -- a real device punch, or a
/// <see cref="ManualAttendanceLog"/> surfaced through
/// <see cref="ManualAttendanceLog.ToAttendanceLog"/>.
///
/// A cell isn't tied to a row or column: it just holds its punch, and the slot it
/// currently occupies is whichever <see cref="DayPunchPairingRowViewModel"/> holds
/// a reference to it (see
/// <see cref="DayPunchPairingEditorViewModel.MoveCell"/>). That's what lets the
/// same instance be dragged from one In/Out slot to another without rebuilding it.
/// </summary>
public partial class DayPunchPairingCellViewModel : ReactiveObject
{
    public DayPunchPairingCellViewModel() =>
        // ReasonText folds the out-of-window note in.
        this.WhenAnyValue(x => x.IsOutsideScheduleWindow)
            .Skip(1)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ReasonText)));

    public required AttendanceLog Punch { get; init; }

    /// <summary>How this punch is referred to in a saved
    /// <see cref="DayPunchPairing"/> -- see <see cref="DayPunchPairingSlot"/>.</summary>
    public PunchKey Key => PunchKey.Of(Punch);

    public bool IsManual => Punch.Source == AttendanceLogSource.Manual;

    public string TimeText => TimeDisplayFormat.Format(TimeOnly.FromDateTime(Punch.Timestamp));

    /// <summary>Badge text -- a manual entry is always called out, so a
    /// hand-typed time is never mistaken for one the clock actually recorded
    /// (same signal as the Summary grid's trailing " *").</summary>
    public string SourceText => IsManual ? "Manual" : "Device";

    /// <summary>
    /// True when this punch falls outside every window the day's schedule
    /// actually searches -- see <see cref="ScheduleApp.Attendance.PunchCandidateWindows"/>
    /// for what those are per ScheduleType. The calculation will not consider it:
    /// for Normal or a windowed RestDay it's outside the buffered clock-in/
    /// clock-out windows, for SplitShift outside every segment's pair of windows,
    /// for Flexible outside the RestrictedTimeIn/RestrictedTimeOut boundary. On a
    /// Leave, Official Business, or unscheduled RestDay this is true for *every*
    /// punch, because those types never look at punches at all.
    ///
    /// Drives nothing but presentation: the card greys out and explains itself,
    /// and stays fully draggable. Dragging one into a slot is allowed and simply
    /// doesn't move the preview -- the punch is not in the pool the preview is
    /// computed from, exactly as it isn't in the one the calculation uses (see
    /// DayPunchPairingEditorViewModel.IsWithinScheduleWindow).
    ///
    /// Settable rather than init-only because correcting a manual punch's time can
    /// carry it across a window boundary in either direction -- see
    /// DayPunchPairingEditorViewModel.EditManualPunchAsync, which re-evaluates this
    /// the same way it refreshes TimeText.
    /// </summary>
    [Reactive]
    public partial bool IsOutsideScheduleWindow { get; set; }

    /// <summary>Why a manual entry was needed, shown as the card's tooltip. Null
    /// for a real device punch with nothing else to say about it -- the tooltip is
    /// suppressed entirely then. An out-of-window punch always has something to
    /// say, so it explains itself here even when it's a device punch with no
    /// reason of its own.</summary>
    public string? ReasonText => IsOutsideScheduleWindow
        ? string.IsNullOrWhiteSpace(Punch.Reason)
            ? OutsideWindowNote
            : $"{Punch.Reason}\n\n{OutsideWindowNote}"
        : Punch.Reason;

    private const string OutsideWindowNote =
        "Outside this day's schedule window, so the calculation ignores it. " +
        "Widen the clock-in/clock-out buffer on the day, or the restricted " +
        "punching window, to bring it in.";

    /// <summary>Set by the editor control while this cell is the one being
    /// dragged, so its card can dim. Reset in the drag source's finally block
    /// whether the drop landed or was cancelled.</summary>
    [Reactive]
    public partial bool IsBeingDragged { get; set; }

    /// <summary>True when this punch's segment has no partner for it -- an in
    /// with no out, or an out with no in. Recomputed on every move (see
    /// <see cref="DayPunchPairingEditorViewModel"/>); this is the orphan the
    /// whole editor exists to let someone resolve.</summary>
    [Reactive]
    public partial bool IsUnpaired { get; set; }

    /// <summary>Re-reads every property projected off <see cref="Punch"/>. Needed
    /// because editing a manual punch's time corrects the underlying
    /// <see cref="AttendanceLog"/> in place -- the cell and the saved pairing's slot
    /// both refer to that exact instance/id, so replacing it would mean re-threading
    /// both (see
    /// <see cref="DayPunchPairingEditorViewModel.EditManualPunchAsync"/>). Only a
    /// manual punch ever reaches this; a device punch is never edited.</summary>
    public void NotifyPunchChanged()
    {
        this.RaisePropertyChanged(nameof(TimeText));
        this.RaisePropertyChanged(nameof(ReasonText));
    }
}
