using CommunityToolkit.Mvvm.ComponentModel;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Desktop.Utilities;

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
public partial class DayPunchPairingCellViewModel : ObservableObject
{
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

    /// <summary>Why a manual entry was needed, shown as the card's tooltip. Null
    /// for a real device punch (the tooltip is suppressed there).</summary>
    public string? ReasonText => Punch.Reason;

    /// <summary>Set by the editor control while this cell is the one being
    /// dragged, so its card can dim. Reset in the drag source's finally block
    /// whether the drop landed or was cancelled.</summary>
    [ObservableProperty]
    private bool isBeingDragged;

    /// <summary>True when this punch's segment has no partner for it -- an in
    /// with no out, or an out with no in. Recomputed on every move (see
    /// <see cref="DayPunchPairingEditorViewModel"/>); this is the orphan the
    /// whole editor exists to let someone resolve.</summary>
    [ObservableProperty]
    private bool isUnpaired;

    /// <summary>Re-reads every property projected off <see cref="Punch"/>. Needed
    /// because editing a manual punch's time corrects the underlying
    /// <see cref="AttendanceLog"/> in place -- the cell and the saved pairing's slot
    /// both refer to that exact instance/id, so replacing it would mean re-threading
    /// both (see
    /// <see cref="DayPunchPairingEditorViewModel.EditManualPunchAsync"/>). Only a
    /// manual punch ever reaches this; a device punch is never edited.</summary>
    public void NotifyPunchChanged()
    {
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(ReasonText));
    }
}
