using System.Windows;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Collects a Date + Name for ManageHolidaysDialog's Add button -- same shape
/// as AddUserDialog: this dialog touches no repository itself, it's handed the
/// existing holiday dates up front (existingDates) and validates against that
/// in-memory set, the same way AddUserDialog validates against a passed-in
/// existingUsernames set rather than querying the database itself.
/// ManageHolidaysDialog does the actual IHolidayRepository.AddAsync call after
/// this returns true -- including the unlikely check-then-act race
/// DuplicateHolidayDateException exists for, which this dialog's local pre-check
/// can't catch on its own.
///
/// Add-only -- editing an existing holiday happens inline in ManageHolidaysDialog's
/// own grid instead (see that class's own doc comment), so the "existing" parameter
/// this constructor used to accept for a doubled-up Edit form is gone; only a
/// brand-new holiday is ever collected here.
/// </summary>
public partial class HolidayDialog : Window
{
    private readonly IReadOnlyCollection<DateOnly> _existingDates;

    public DateOnly Date { get; private set; }

    /// <summary>Deliberately not named "Name" -- Window inherits a Name property
    /// from FrameworkElement (WPF's own x:Name/FindName plumbing), and a same-named
    /// property here would silently hide it instead of adding a new one (CS0108),
    /// which is exactly the kind of shadowing that trips someone up later expecting
    /// FindName-style behavior from this. HolidayName instead, matching Holiday.Name
    /// (the model property this ultimately becomes) closely enough to still read
    /// clearly at its call site in ManageHolidaysDialog.</summary>
    public string HolidayName { get; private set; } = string.Empty;

    public HolidayDialog(IReadOnlyCollection<DateOnly> existingDates)
    {
        InitializeComponent();
        _existingDates = existingDates;

        // No default SelectedDate -- a holiday being added is just as likely to be a
        // past or future date, so there's no better default than forcing an explicit
        // pick.
        Loaded += (_, _) => NameBox.Focus();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (DateBox.SelectedDate is not { } selectedDate)
        {
            ShowError("Pick a date.");
            return;
        }

        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Enter a name for the holiday.");
            return;
        }

        var date = DateOnly.FromDateTime(selectedDate);
        if (_existingDates.Contains(date))
        {
            ShowError($"{date:MMMM d, yyyy} is already listed as a holiday.");
            return;
        }

        Date = date;
        HolidayName = name;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
