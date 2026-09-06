using CommunityToolkit.Mvvm.ComponentModel;
using ScheduleApp.Core.Attendance;

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
/// shape of a Partial day). Both slots are observable so a drag-drop swap updates
/// the grid without rebuilding the row.
/// </summary>
public partial class DayPunchPairingRowViewModel : ObservableObject
{
    [ObservableProperty]
    private DayPunchPairingCellViewModel? inPunch;

    [ObservableProperty]
    private DayPunchPairingCellViewModel? outPunch;

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

    partial void OnInPunchChanged(DayPunchPairingCellViewModel? value) => NotifyFillStateChanged();

    partial void OnOutPunchChanged(DayPunchPairingCellViewModel? value) => NotifyFillStateChanged();

    private void NotifyFillStateChanged()
    {
        OnPropertyChanged(nameof(IsIncomplete));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
