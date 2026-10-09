using ScheduleApp.Core.Attendance;
using ReactiveUI.SourceGenerators;

namespace ScheduleApp.Desktop.ViewModels.Attendance;

/// <summary>
/// Collects one punch time (plus the Reason/EnteredBy a <see cref="ManualAttendanceLog"/>
/// requires) for a single In/Out slot of the Day Punch Pairing editor.
///
/// Deliberately narrower than ManualLogEntryViewModel: the employee, the date, and which slot is
/// being filled all come from the cell that was right-clicked, so none of them are asked for
/// again. The time is exact to the minute -- a punch is a real clock event ("5:11 PM").
/// </summary>
public partial class PunchTimeEntryViewModel : ReactiveViewModel
{
    /// <param name="slotLabel">"Time In"/"Time Out" -- also the Reason's default, since the slot
    /// is fixed by the cell, not chosen here.</param>
    /// <param name="existingLog">The manual entry being corrected, or null for a new punch. Only
    /// a manual entry ever gets here -- a device punch is a record of what the clock reported and
    /// is left alone.</param>
    public PunchTimeEntryViewModel(
        string employeeName, DateOnly date, string slotLabel, TimeOnly? initialTime, ManualAttendanceLog? existingLog = null)
    {
        EmployeeName = employeeName;
        SlotText = $"{slotLabel} · {date:dddd, MMMM d, yyyy}";
        Title = existingLog is null ? "Add Manual Punch" : "Edit Manual Punch";
        SaveText = existingLog is null ? "Add" : "Save";
        Time = initialTime;
        Reason = new DefaultedText(() => slotLabel, existingLog?.Reason);
        EnteredBy = existingLog?.EnteredBy ?? Environment.UserName;
    }

    public string EmployeeName { get; }

    public string SlotText { get; }

    public string Title { get; }

    public string SaveText { get; }

    [Reactive]
    public partial TimeOnly? Time { get; set; }

    public DefaultedText Reason { get; }

    [Reactive]
    public partial string EnteredBy { get; set; } = string.Empty;

    /// <summary>Why Save was turned away, until it isn't.</summary>
    [Reactive]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>The time Save settled on; meaningful once Save has returned true.</summary>
    public TimeOnly AcceptedTime { get; private set; }

    public string AcceptedReason { get; private set; } = string.Empty;

    public string AcceptedEnteredBy { get; private set; } = string.Empty;

    /// <summary>Save: needs a time; settles the Accepted values.</summary>
    [ReactiveCommand]
    private bool Accept()
    {
        if (Time is not { } time)
        {
            ErrorMessage = "Select a time.";
            return false;
        }

        ErrorMessage = null;
        AcceptedTime = time;
        AcceptedReason = Reason.Value;
        AcceptedEnteredBy = string.IsNullOrWhiteSpace(EnteredBy) ? Environment.UserName : EnteredBy.Trim();
        return true;
    }
}
