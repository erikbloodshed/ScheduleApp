using System.Reactive.Linq;
using ScheduleApp.Core.Attendance;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>Which of a row's two column slots a cell sits in -- the editor's
/// column headers are literally "Time In" and "Time Out", and this maps
/// straight onto <see cref="PairingRole"/> when the layout is saved.</summary>
public enum ColumnSlot
{
    In,
    Out,
}

/// <summary>How the person left the Day Punch Pairing dialog -- see
/// DayPunchPairingEditorViewModel.Outcome.</summary>
public enum DayPunchPairingOutcome
{
    /// <summary>Persist the grid as it stands (see
    /// <see cref="DayPunchPairingEditorViewModel.BuildPairing"/>).</summary>
    Save,

    /// <summary>Drop this day's saved pairing entirely and go back to the default
    /// time-order pairing.</summary>
    ResetToAutomatic,
}

/// <summary>
/// One work segment in the Day Punch Pairing editor: the punch that opened it and
/// the punch that closed it, either of which may be missing (an empty slot -- a
/// valid drop target that shows a "missing punch" placeholder, and the visible
/// shape of a Partial day). Both slots are observable so a drag-drop move updates
/// the grid without rebuilding the row. A fully empty row is legal and persists as
/// working space (see <see cref="DayPunchPairingEditorViewModel.MoveCell"/>); only
/// "Remove Empty" or a save clears one out.
/// </summary>
public partial class DayPunchPairingRowViewModel : ReactiveObject
{
    public DayPunchPairingRowViewModel()
    {
        var slots = this.WhenAnyValue(x => x.InPunch, x => x.OutPunch, (inPunch, outPunch) => (In: inPunch, Out: outPunch));
        _isIncompleteHelper = slots.Select(s => (s.In is null) != (s.Out is null)).ToProperty(this, x => x.IsIncomplete);
        _isEmptyHelper = slots.Select(s => s.In is null && s.Out is null).ToProperty(this, x => x.IsEmpty);
    }

    [Reactive]
    public partial DayPunchPairingCellViewModel? InPunch { get; set; }

    [Reactive]
    public partial DayPunchPairingCellViewModel? OutPunch { get; set; }

    /// <summary>True when exactly one of the two slots is filled -- the row is a
    /// half-open segment, i.e. the orphan. Drives the row's warning styling.</summary>
    [ObservableAsProperty]
    public partial bool IsIncomplete { get; }

    [ObservableAsProperty(InitialValue = "true")]
    public partial bool IsEmpty { get; }

    public DayPunchPairingCellViewModel? this[ColumnSlot slot]
    {
        get => slot == ColumnSlot.In ? InPunch : OutPunch;
        set
        {
            if (slot == ColumnSlot.In) InPunch = value;
            else OutPunch = value;
        }
    }
}
