using System.Windows;
using ScheduleApp.Desktop.ViewModels.Attendance;

namespace ScheduleApp.Desktop.Views;

/// <summary>What the person chose on the way out of
/// <see cref="DayPunchPairingDialog"/>. Only meaningful when ShowDialog()
/// returned true.</summary>
public enum DayPunchPairingDialogOutcome
{
    /// <summary>Persist the grid as it stands (see
    /// <see cref="DayPunchPairingEditorViewModel.BuildPairing"/>).</summary>
    Save,

    /// <summary>Drop this day's saved pairing entirely and go back to the default
    /// time-order pairing.</summary>
    ResetToAutomatic,
}

/// <summary>
/// Hosts the Day Punch Pairing editor for one employee/day. Like
/// ManualLogEntryDialog, this dialog touches no repository itself -- it collects
/// an intent and hands the finished view model back; the launcher
/// (DayPunchPairingEditorLauncher) does the actual save/delete and the
/// AttendanceDataVersion bump.
/// </summary>
public partial class DayPunchPairingDialog : Wpf.Ui.Controls.FluentWindow
{
    public DayPunchPairingDialog(DayPunchPairingEditorViewModel editor)
    {
        InitializeComponent();
        Editor = editor;
        DataContext = editor;

        // Named for what it can actually do on this day -- only a Flexible day's
        // pairing is read back by the calculation (see
        // DayPunchPairingEditorViewModel.PairingAffectsResult); on every other type
        // this is a read-only view of the day's punches (IsReadOnly).
        Title = editor.PairingAffectsResult
            ? $"Edit Punch Pairing — {editor.EmployeeName}, {editor.Date:MMM d, yyyy}"
            : $"Punches — {editor.EmployeeName}, {editor.Date:MMM d, yyyy}";

        // Nothing to reset back to on a day that never had an override -- and
        // nothing at all is changeable from a read-only "View Punches…" open, a
        // saved override included.
        ResetButton.Visibility = editor.HasSavedOverride && !editor.IsReadOnly
            ? Visibility.Visible
            : Visibility.Collapsed;

        // A read-only day has nothing to save and nothing to discard: the grid can't
        // be changed and the launcher wouldn't persist a non-Flexible pairing
        // anyway. So Save goes away and the remaining button just closes the view.
        if (editor.IsReadOnly)
        {
            SaveButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "Close";
            CancelButton.IsDefault = true;
        }
    }

    public DayPunchPairingEditorViewModel Editor { get; }

    public DayPunchPairingDialogOutcome Outcome { get; private set; } = DayPunchPairingDialogOutcome.Save;

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // Saving a still-Partial layout is allowed on purpose: a day can be
        // genuinely incomplete (someone really did forget to badge out, and the
        // manual entry hasn't been added yet), and forcing it to be whole before
        // it can be recorded would just mean losing the re-pairing work already
        // done. The footer already says Partial in that case, so it isn't silent.
        Outcome = DayPunchPairingDialogOutcome.Save;
        DialogResult = true;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            $"Discard the saved punch pairing for {Editor.EmployeeName} on {Editor.Date:MMM d, yyyy}?\n\n" +
            "The day goes back to being paired automatically, in punch-time order.",
            "Reset to automatic", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        Outcome = DayPunchPairingDialogOutcome.ResetToAutomatic;
        DialogResult = true;
    }
}
