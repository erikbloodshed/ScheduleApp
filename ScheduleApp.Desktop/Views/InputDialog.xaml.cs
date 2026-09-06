using System.Windows;

namespace ScheduleApp.Desktop.Views;

public partial class InputDialog : Wpf.Ui.Controls.FluentWindow
{
    public string Value => ValueBox.Text;

    public InputDialog(string title, string prompt, string defaultValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = defaultValue;
        // Select-all rather than just placing the caret at the end -- a prefilled
        // default (see ScheduleAssignmentViewModel.ToggleHolidayForSelectionAsync)
        // is meant to be a one-keystroke accept-or-replace, not something to
        // backspace through. Harmless for the no-default callers: an empty box has
        // nothing to select.
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ValueBox.Text))
        {
            MessageBox.Show("Please enter a value.", "Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}
