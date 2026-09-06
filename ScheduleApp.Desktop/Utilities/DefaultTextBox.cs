using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScheduleApp.Desktop.Utilities;

/// <summary>
/// Turns a plain TextBox into one that shows a computed default value, in gray
/// placeholder-style text, until the person actually types something over it -- the
/// same "grayed-out until edited" convention ApplyScheduleDialog's own
/// InitializeBufferBox/ShowBufferDefault pair uses for its numeric buffer-override
/// boxes, generalized here to any text via a delegate instead of a formatted number
/// (that pair stays as its own, numeric-specific copy rather than being rebuilt on top
/// of this -- it also tracks isExistingSegment's gray-vs-normal distinction, which
/// nothing using this needs). Tag is used as the "still showing the default" flag
/// exactly as that pair uses it: true while the box holds the default, cleared to null
/// the moment a real edit lands.
///
/// First tenant: ManualLogEntryDialog/PunchTimeEntryDialog's own Reason box, defaulted
/// to the punch type/slot being logged so a person who has nothing more specific to say
/// isn't blocked on typing something.
/// </summary>
public static class DefaultTextBox
{
    /// <summary>Shows <paramref name="computeDefault"/>'s current value and wires up
    /// the GotFocus/PreviewMouseLeftButtonDown/TextChanged/LostFocus quartet that keeps
    /// the box tracking it until the first real edit. Call once per box, after whatever
    /// <paramref name="computeDefault"/> itself reads (e.g. a combo box's starting
    /// selection) is already in place -- this calls it immediately for the box's
    /// starting text.</summary>
    public static void Initialize(TextBox box, Func<string> computeDefault)
    {
        ShowDefault(box, computeDefault);

        // Selects rather than leaves the caret in place: the box already shows the
        // default (or, after an edit, a real value), and highlighting it lets typing
        // immediately replace it wholesale -- the usual "click a prefilled field and
        // just start typing" convention. SelectAll alone only reliably works for
        // keyboard (Tab) focus; a mouse click re-places the caret afterward and
        // cancels the selection unless the click that caused the focus is intercepted
        // too, hence PreviewMouseLeftButtonDown below (a well-known WPF TextBox quirk,
        // not redundant with GotFocus).
        box.GotFocus += (_, _) => box.SelectAll();
        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (box.IsFocused) return;

            e.Handled = true;
            box.Focus();
        };

        box.TextChanged += (_, _) =>
        {
            // Tag is true only while the box is still showing the default untouched
            // (see ShowDefault) -- the first real edit (typing over the selection
            // above, pasting, deleting a character, anything) means this is now a
            // real value the person entered, not the default anymore, so it stops
            // being styled as one and stops tracking computeDefault via Refresh below.
            if (box.Tag is true)
            {
                box.Tag = null;
                box.ClearValue(TextBox.ForegroundProperty);
            }
        };

        box.LostFocus += (_, _) =>
        {
            // Left empty -- either never typed anything, or typed then deleted it all
            // again -- means "no override": restore the default display rather than
            // leaving a blank box with no indication of what value is actually in
            // effect (e.g. what a Save button reading Text back will end up using).
            if (string.IsNullOrWhiteSpace(box.Text))
                ShowDefault(box, computeDefault);
        };
    }

    /// <summary>Re-displays <paramref name="computeDefault"/>'s current value -- call
    /// this from a dependency's own change handler (e.g. a combo box the default is
    /// derived from) so the box stays in sync while it's still showing a default.
    /// No-ops once the box holds a real typed value (Tag no longer true), so calling
    /// this after every dependency change is always safe -- it never overwrites
    /// something the person actually typed.</summary>
    public static void Refresh(TextBox box, Func<string> computeDefault)
    {
        if (box.Tag is true)
            ShowDefault(box, computeDefault);
    }

    private static void ShowDefault(TextBox box, Func<string> computeDefault)
    {
        box.Text = computeDefault();
        box.Foreground = Brushes.Gray;
        box.Tag = true;
    }
}
