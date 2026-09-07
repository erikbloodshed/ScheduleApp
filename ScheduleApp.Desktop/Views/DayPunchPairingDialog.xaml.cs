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

        // Named for what it does here, matching the menu item that opened it (see
        // MonthCalendarControl.BuildDayContextMenu): "Edit Punch Pairing" for a
        // Flexible day, "Edit Punches" for an editable non-Flexible one (drag isn't
        // saved there, but the punch edits are), "Punches" for a read-only view.
        var titleVerb = editor.IsReadOnly ? "Punches"
            : editor.PairingAffectsResult ? "Edit Punch Pairing"
            : "Edit Punches";
        Title = $"{titleVerb} — {editor.EmployeeName}, {editor.Date:MMM d, yyyy}";

        // "Reset to Automatic" clears a *saved* pairing, so it's offered only where
        // one is actually saved -- a Flexible day. A non-Flexible day persists no
        // pairing (editable or not), so there's nothing for it to reset.
        ResetButton.Visibility = editor.HasSavedOverride && editor.PairingAffectsResult
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Save persists the pairing, which only a Flexible day does. Every other type
        // -- read-only viewer, or the editable Partial/Absent case where punches save
        // themselves the moment they're added -- has nothing for Save to do, so it
        // goes and the remaining button just closes the dialog.
        if (!editor.PairingAffectsResult)
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
