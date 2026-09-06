using System.Windows;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Desktop.Utilities;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Collects one punch time (plus the Reason/EnteredBy a
/// <see cref="ManualAttendanceLog"/> requires) for a single In/Out slot of the Day
/// Punch Pairing editor.
///
/// A deliberately narrower dialog than ManualLogEntryDialog: the employee, the
/// date, and which slot is being filled all come from the cell that was
/// right-clicked, so none of them are asked for again -- unlike that dialog, which
/// is reached with no such context and has to collect all four. The time input is
/// a Controls.TimeInput, same as ManualLogEntryDialog's own TimeBox, for the same
/// reason: a punch is a real clock event ("5:11 PM"), so the minute has to be
/// exact -- unlike Controls.TimePicker (now retired), which only offered :00/:30.
/// </summary>
public partial class PunchTimeEntryDialog : Wpf.Ui.Controls.FluentWindow
{
    /// <summary>Kept only for SaveButton_Click's blank-Reason default below -- see
    /// Reason's own doc comment. Everywhere else, the constructor already folds this
    /// straight into ContextSlotText, so there'd otherwise be nothing left holding
    /// onto it.</summary>
    private readonly string _slotLabel;

    public PunchTimeEntryDialog(
        string employeeName,
        DateOnly date,
        string slotLabel,
        TimeOnly? initialTime,
        ManualAttendanceLog? existingLog = null)
    {
        InitializeComponent();

        _slotLabel = slotLabel;
        ContextEmployeeText.Text = employeeName;
        ContextSlotText.Text = $"{slotLabel} · {date:dddd, MMMM d, yyyy}";

        TimeBox.SelectedTime = initialTime;

        // Shows _slotLabel ("Time In"/"Time Out", the same text just above in
        // ContextSlotText) as ReasonBox's starting, grayed-out default -- see
        // Reason's own doc comment. No dependency to track it against afterward
        // (unlike ManualLogEntryDialog's PunchTypeCombo-driven equivalent): the slot
        // a punch belongs to is fixed by which cell was right-clicked, not something
        // choosable inside this dialog.
        DefaultTextBox.Initialize(ReasonBox, () => _slotLabel);

        // Edit mode: the entry already exists, so this is a correction to a time
        // someone typed before, not a new punch. Only a manual entry ever reaches
        // this -- a device punch is a record of what the clock reported and is left
        // alone (see ManualAttendanceLog's own doc comment).
        if (existingLog is not null)
        {
            Title = "Edit Manual Punch";
            SaveButton.Content = "Save";

            // A real saved Reason overrides the default DefaultTextBox.Initialize
            // just put in ReasonBox above; a blank one (older data from before
            // Reason had a default at all) leaves that default showing instead of a
            // blank box, same as a brand-new entry.
            if (!string.IsNullOrWhiteSpace(existingLog.Reason))
            {
                ReasonBox.Text = existingLog.Reason;
                ReasonBox.ClearValue(System.Windows.Controls.TextBox.ForegroundProperty);
                ReasonBox.Tag = null;
            }

            EnteredByBox.Text = existingLog.EnteredBy;
        }
        else
        {
            EnteredByBox.Text = Environment.UserName;
        }

        Loaded += (_, _) => TimeBox.FocusHour();
    }

    public TimeOnly TimeOfDay { get; private set; }

    /// <summary>Optional -- ReasonBox shows the slot this dialog was opened for
    /// ("Time In"/"Time Out", the same text already shown above in ContextSlotText)
    /// as a live gray default for as long as it's left untouched (see
    /// DefaultTextBox, wired up in the constructor), and SaveButton_Click falls
    /// back to that same text if the box somehow still reads blank regardless. So
    /// this is never actually empty by the time the dialog closes, even though
    /// nothing forces the person to type anything more specific.</summary>
    public string Reason { get; private set; } = string.Empty;
    public string EnteredBy { get; private set; } = string.Empty;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (TimeBox.SelectedTime is not { } time)
        {
            ShowError("Select a time.");
            return;
        }

        TimeOfDay = time;
        Reason = string.IsNullOrWhiteSpace(ReasonBox.Text) ? _slotLabel : ReasonBox.Text.Trim();
        EnteredBy = string.IsNullOrWhiteSpace(EnteredByBox.Text)
            ? Environment.UserName
            : EnteredByBox.Text.Trim();

        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
