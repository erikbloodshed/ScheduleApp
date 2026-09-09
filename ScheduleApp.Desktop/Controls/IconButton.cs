using System.Windows;
using System.Windows.Controls;

namespace ScheduleApp.Desktop.Controls;

/// <summary>
/// A Button with a Segoe Fluent Icons / Segoe MDL2 Assets glyph placed before its
/// ordinary Content -- replaces the hand-rolled
/// "&lt;Button&gt;&lt;StackPanel&gt;&lt;TextBlock FontFamily=\"Segoe Fluent Icons...\"&gt;
/// (the glyph) &lt;/TextBlock&gt;&lt;TextBlock&gt;(the label)&lt;/TextBlock&gt;&lt;/StackPanel&gt;
/// &lt;/Button&gt;" markup ManualEntriesView's own "Export" button was the first to need
/// (see that file's git history for the exact shape this replaces) -- worth a shared
/// control now that more buttons are getting icons the same way, rather than copying that
/// same StackPanel-of-two-TextBlocks into every page that wants one.
///
/// Deliberately a Button subclass, not a UserControl wrapping one -- see NumericTextBox's
/// own doc comment for the same reasoning applied to TextBox: a UserControl's Command/
/// CommandParameter/Click/IsDefault and keyboard/focus behavior would all need to be
/// re-exposed and forwarded to an inner Button by hand, where subclassing Button keeps
/// every one of those working exactly as they already do on every other Button in this
/// app, with zero extra plumbing -- an existing "Command={Binding ExportCommand}" binding
/// just keeps working unchanged when a plain Button is swapped for this.
///
/// Not a replacement for the separate IconHeaderActionButton style each page already
/// carries its own copy of (AttendanceSummaryView/PayrollSummaryView/SchedulePage/
/// ManualEntriesView's own Refresh/Cancel button) -- that style is a small, fixed-size,
/// round, icon-*only* hover target with its own template; this control instead keeps
/// whatever chrome the ambient Button style already gives a page's ordinary buttons (see
/// this class's own style in Themes/AppStyles.xaml, based on that implicit style) and
/// just prepends an icon to Content, so it fits in next to plain-text siblings like "Add"/
/// "Import" rather than standing out as a separate round button.
///
/// Default style lives in Themes/AppStyles.xaml, keyed by TargetType (implicit), so a
/// plain &lt;controls:IconButton Icon="&amp;#xEDE1;" Content="Export" .../&gt; picks it up
/// automatically -- no Style="{StaticResource ...}" needed on every usage the way
/// NumericTextBox's own "Styling note" says every one of those needs (that style lives per
/// -page instead; this one is genuinely shared, the same way CardBorderStyle/
/// CaptionTextStyle/etc. already are in that same file).
/// </summary>
public class IconButton : Button
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(string),
        typeof(IconButton),
        new PropertyMetadata(null));

    /// <summary>A single Segoe Fluent Icons / Segoe MDL2 Assets glyph (e.g. "&#xEDE1;" for
    /// "Export") -- see
    /// https://learn.microsoft.com/en-us/windows/apps/design/iconography/segoe-fluent-icons-font
    /// for the full list and each glyph's own codepoint. Left null/empty renders no icon at
    /// all, i.e. an ordinary Button showing just its Content -- there's no separate
    /// "icon-only" or "no icon" switch beyond setting (or not setting) this.</summary>
    public string? Icon
    {
        get => (string?)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }
}
