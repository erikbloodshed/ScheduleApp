using ScheduleApp.Core.Attendance;
using ReactiveUI;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>Which of a row's two column slots a cell sits in -- the editor's
/// column headers are literally "Time In" and "Time Out", and this maps
/// straight onto <see cref="PairingRole"/> when the layout is saved.</summary>
public enum ColumnSlot
{
    In,
    Out,
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
public class DayPunchPairingRowViewModel : ReactiveObject
{
    public DayPunchPairingCellViewModel? InPunch
    {
        get => _inPunch;
        set
        {
            if (EqualityComparer<DayPunchPairingCellViewModel?>.Default.Equals(_inPunch, value)) return;
            this.RaisePropertyChanging();
            _inPunch = value;
            OnInPunchChanged(value);
            this.RaisePropertyChanged();
        }
    }

    private DayPunchPairingCellViewModel? _inPunch;

    public DayPunchPairingCellViewModel? OutPunch
    {
        get => _outPunch;
        set
        {
            if (EqualityComparer<DayPunchPairingCellViewModel?>.Default.Equals(_outPunch, value)) return;
            this.RaisePropertyChanging();
            _outPunch = value;
            OnOutPunchChanged(value);
            this.RaisePropertyChanged();
        }
    }

    private DayPunchPairingCellViewModel? _outPunch;

    /// <summary>True when exactly one of the two slots is filled -- the row is a
    /// half-open segment, i.e. the orphan. Drives the row's warning styling.</summary>
    public bool IsIncomplete => (InPunch is null) != (OutPunch is null);

    public bool IsEmpty => InPunch is null && OutPunch is null;

    public DayPunchPairingCellViewModel? this[ColumnSlot slot]
    {
        get => slot == ColumnSlot.In ? InPunch : OutPunch;
        set
        {
            if (slot == ColumnSlot.In) InPunch = value;
            else OutPunch = value;
        }
    }

    private void OnInPunchChanged(DayPunchPairingCellViewModel? value) => NotifyFillStateChanged();

    private void OnOutPunchChanged(DayPunchPairingCellViewModel? value) => NotifyFillStateChanged();

    private void NotifyFillStateChanged()
    {
        this.RaisePropertyChanged(nameof(IsIncomplete));
        this.RaisePropertyChanged(nameof(IsEmpty));
    }
}
