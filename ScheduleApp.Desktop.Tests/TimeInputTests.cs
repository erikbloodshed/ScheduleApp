using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ScheduleApp.Desktop.Controls;
using Syncfusion.Windows.Shared;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

/// <summary>A time field that starts blank, as one bound to a time nobody has entered yet
/// does: it stays blank until it's edited, and then starts at 5:00 AM rather than at whatever
/// time it is.</summary>
public class TimeInputTests
{
    private sealed class Entry
    {
        public TimeOnly? Time { get; set; }
    }

    private static Task WithFieldAsync(TimeOnly? time, Func<TimeInput, Entry, Task> test) => UiThread.RunAsync(async () =>
    {
        var entry = new Entry { Time = time };
        var field = new TimeInput { DataContext = entry };
        field.SetBinding(TimeInput.SelectedTimeProperty, new Binding(nameof(Entry.Time)));
        var window = await UiThread.ShowAsync(new Window { Content = field, Width = 300, Height = 400 });
        try
        {
            await test(field, entry);
        }
        finally
        {
            field.Editor.IsDropDownOpen = false;
            window.Close();
        }
    });

    /// <summary>Presses <paramref name="key"/> in the field; true if the field kept the key to
    /// itself rather than passing it on to the editor.</summary>
    private static bool Press(TimeInput field, Key key)
    {
        var press = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(field.Editor)!, 0, key)
        {
            RoutedEvent = UIElement.PreviewKeyDownEvent,
        };
        field.Editor.RaiseEvent(press);
        return press.Handled;
    }

    [Fact]
    public Task A_blank_field_shows_nothing() => WithFieldAsync(null, (field, entry) =>
    {
        Assert.Null(field.SelectedTime);
        Assert.Null(entry.Time);
        Assert.Equal(string.Empty, field.Editor.Text);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(Key.D7)]
    [InlineData(Key.NumPad7)]
    public Task The_first_digit_starts_a_blank_field_at_five_with_its_hour_ready_to_replace(Key digit) =>
        WithFieldAsync(null, async (field, entry) =>
        {
            var keptToItself = Press(field, digit);
            await UiThread.IdleAsync();

            // The digit itself goes on to the editor, which types it over the selected hour.
            Assert.False(keptToItself);
            Assert.Equal(TimeInput.BlankStartsAt, field.SelectedTime);
            Assert.Equal(TimeInput.BlankStartsAt, entry.Time);
            Assert.Equal("5:00 AM", field.Editor.Text);
            Assert.Equal("5", field.Editor.SelectedText);
        });

    [Theory]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    public Task An_arrow_shows_five_itself_on_a_blank_field(Key arrow) => WithFieldAsync(null, async (field, entry) =>
    {
        var keptToItself = Press(field, arrow);
        await UiThread.IdleAsync();

        // Kept, or the editor would step straight off 5:00 to 6:00 or 4:00.
        Assert.True(keptToItself);
        Assert.Equal(TimeInput.BlankStartsAt, entry.Time);
    });

    [Theory]
    [InlineData(Key.Tab)]
    [InlineData(Key.Left)]
    [InlineData(Key.Enter)]
    [InlineData(Key.A)]
    public Task Any_other_key_leaves_a_blank_field_blank(Key key) => WithFieldAsync(null, async (field, entry) =>
    {
        Press(field, key);
        await UiThread.IdleAsync();

        Assert.Null(entry.Time);
        Assert.Equal(string.Empty, field.Editor.Text);
    });

    [Fact]
    public Task A_field_with_a_time_keeps_it() => WithFieldAsync(new TimeOnly(14, 30), async (field, entry) =>
    {
        Assert.False(Press(field, Key.D7));
        await UiThread.IdleAsync();

        Assert.Equal(new TimeOnly(14, 30), entry.Time);
        Assert.Equal("2:30 PM", field.Editor.Text);
    });

    [Fact]
    public Task The_clock_opens_at_five_on_a_blank_field_without_filling_it_in() => WithFieldAsync(null, async (field, entry) =>
    {
        field.Editor.IsDropDownOpen = true;
        await UiThread.IdleAsync();

        var clock = ClockOf(field);
        Assert.Equal(TimeInput.BlankStartsAt, TimeOnly.FromDateTime(clock.DateTime));
        Assert.Null(entry.Time);

        // Closed without OK, the field is as blank as it was.
        field.Editor.IsDropDownOpen = false;
        await UiThread.IdleAsync();
        Assert.Null(entry.Time);
        Assert.Equal(string.Empty, field.Editor.Text);
    });

    [Fact]
    public Task The_clock_opens_at_the_fields_own_time_when_it_has_one() => WithFieldAsync(new TimeOnly(14, 30), async (field, _) =>
    {
        field.Editor.IsDropDownOpen = true;
        await UiThread.IdleAsync();

        Assert.Equal(new TimeOnly(14, 30), TimeOnly.FromDateTime(ClockOf(field).DateTime));
    });

    private static Clock ClockOf(TimeInput field)
    {
        var popup = Assert.IsType<Popup>(field.Editor.Template.FindName("PART_Popup", field.Editor));
        return Descendants<Clock>(popup.Child).Single();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T self)
            yield return self;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            foreach (var descendant in Descendants<T>(VisualTreeHelper.GetChild(root, i)))
                yield return descendant;
        }
    }
}
