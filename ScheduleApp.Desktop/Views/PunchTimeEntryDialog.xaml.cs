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
/// the same editable ComboBox + <see cref="TimeDisplayFormat.TryParse"/> pairing it
/// uses, for the same reason: a punch is a real clock event ("5:11 PM"), so the
/// minute has to be typeable, which rules out the Controls.TimePicker used for
/// scheduling (that one snaps to :00/:30 by design).
/// </summary>
public partial class PunchTimeEntryDialog : Wpf.Ui.Controls.FluentWindow
{
    public PunchTimeEntryDialog(
        string employeeName,
        DateOnly date,
        string slotLabel,
        TimeOnly? initialTime,
        ManualAttendanceLog? existingLog = null)
    {
        InitializeComponent();

        for (var hours = 0; hours < 24; hours++)
            TimeCombo.Items.Add(TimeDisplayFormat.Format(TimeOnly.FromTimeSpan(TimeSpan.FromHours(hours))));

        ContextEmployeeText.Text = employeeName;
        ContextSlotText.Text = $"{slotLabel} · {date:dddd, MMMM d, yyyy}";

        if (initialTime is { } time)
            TimeCombo.Text = TimeDisplayFormat.Format(time);

        // Edit mode: the entry already exists, so this is a correction to a time
        // someone typed before, not a new punch. Only a manual entry ever reaches
        // this -- a device punch is a record of what the clock reported and is left
        // alone (see ManualAttendanceLog's own doc comment).
        if (existingLog is not null)
        {
            Title = "Edit Manual Punch";
            SaveButton.Content = "Save";
            ReasonBox.Text = existingLog.Reason;
            EnteredByBox.Text = existingLog.EnteredBy;
        }
        else
        {
            EnteredByBox.Text = Environment.UserName;
        }

        Loaded += (_, _) => TimeCombo.Focus();
    }

    public TimeOnly TimeOfDay { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string EnteredBy { get; private set; } = string.Empty;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TimeDisplayFormat.TryParse(TimeCombo.Text, out var time))
        {
            ShowError("Enter a valid time, e.g. 5:11 PM.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ReasonBox.Text))
        {
            ShowError("Enter a reason — e.g. \"Forgot to badge out\".");
            return;
        }

        TimeOfDay = time;
        Reason = ReasonBox.Text.Trim();
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
