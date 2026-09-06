using System.Globalization;
using System.Windows.Controls;

namespace ScheduleApp.Desktop.Controls;

/// <summary>
/// Replaces the old free-text/editable-ComboBox time entry (see TimeDisplayFormat)
/// with three plain dropdowns -- hour (1-12), minute (00/30 only), AM/PM -- so a
/// time can only ever be picked, never mistyped. Used for the "Time in" field and
/// each Flexible punching window's start/end time in ApplyScheduleDialog.
///
/// Minute is restricted to :00/:30 by design (the request that prompted this
/// control). That means SelectedTime's setter snaps to the nearest of those two
/// -- relevant when loading a prefilled value that happens to fall elsewhere (e.g.
/// something saved before this control existed, or edited directly in the
/// database) -- see SetSnapped.
/// </summary>
public partial class TimePicker : UserControl
{
    /// <summary>Raised whenever the composed time changes, including when it moves
    /// between "fully selected" and "not yet fully selected" (see SelectedTime).
    /// Mirrors the old ComboBox's SelectionChanged for callers that want live
    /// validation, though nothing currently wires it up -- OkButton_Click reads
    /// SelectedTime directly instead.</summary>
    public event EventHandler? SelectedTimeChanged;

    /// <summary>Guards the three SelectionChanged handlers below while SetSnapped is
    /// assigning all three parts, so setting SelectedTime programmatically raises
    /// SelectedTimeChanged once (or not at all, if nothing's wired up) instead of
    /// up to three times with a briefly-inconsistent in-between state.</summary>
    private bool _suppressEvents;

    public TimePicker()
    {
        InitializeComponent();

        for (var hour = 1; hour <= 12; hour++)
            HourCombo.Items.Add(hour);

        MinuteCombo.Items.Add("00");
        MinuteCombo.Items.Add("30");

        AmPmCombo.Items.Add("AM");
        AmPmCombo.Items.Add("PM");
    }

    /// <summary>Null whenever any of the three parts is unselected -- matches the old
    /// ComboBox's blank-text starting state, which callers rely on to mean "not yet
    /// set" (e.g. ApplyScheduleDialog's MixedScheduleNote case, or a freshly-added
    /// Flexible segment row that the person still has to fill in). Setting null
    /// clears all three parts back to that same blank state.</summary>
    public TimeOnly? SelectedTime
    {
        get
        {
            if (HourCombo.SelectedItem is not int hour12 ||
                MinuteCombo.SelectedItem is not string minuteText ||
                AmPmCombo.SelectedItem is not string amPm)
                return null;

            var minute = int.Parse(minuteText, CultureInfo.InvariantCulture);
            var hour24 = amPm == "PM"
                ? (hour12 == 12 ? 12 : hour12 + 12)
                : (hour12 == 12 ? 0 : hour12);

            return new TimeOnly(hour24, minute);
        }
        set => SetSnapped(value);
    }

    /// <summary>Nearest-:00/:30 rounding for a value that doesn't land exactly on
    /// one of the two minute options this control offers -- standard round-half-up
    /// at the 15/45-minute midpoints, with 45+ rolling into the next hour (and, at
    /// 11 PM, wrapping the day over to 12 AM).</summary>
    private void SetSnapped(TimeOnly? value)
    {
        _suppressEvents = true;
        try
        {
            if (value is not { } time)
            {
                HourCombo.SelectedItem = null;
                MinuteCombo.SelectedItem = null;
                AmPmCombo.SelectedItem = null;
                return;
            }

            var snappedMinute = time.Minute is >= 15 and < 45 ? 30 : 0;
            var hour24 = time.Minute >= 45 ? (time.Hour + 1) % 24 : time.Hour;
            var hour12 = hour24 % 12 == 0 ? 12 : hour24 % 12;

            HourCombo.SelectedItem = hour12;
            MinuteCombo.SelectedItem = snappedMinute.ToString("00", CultureInfo.InvariantCulture);
            AmPmCombo.SelectedItem = hour24 < 12 ? "AM" : "PM";
        }
        finally
        {
            _suppressEvents = false;
        }

        SelectedTimeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Part_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressEvents)
            SelectedTimeChanged?.Invoke(this, EventArgs.Empty);
    }
}
