using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NSubstitute;
using ScheduleApp.Desktop.Controls;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.Views;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

/// <summary>The Schedule and Employees pages, shown, over a whole Schedule tab.</summary>
public sealed class SchedulePagesTests : IDisposable
{
    private readonly TestSchedule _schedule = new();
    private MainViewModel Vm => _schedule.ViewModel;

    public void Dispose() => _schedule.Dispose();

    private static Task WithWindowAsync<TPage>(Func<TPage> newPage, Func<TPage, Task> test) where TPage : FrameworkElement =>
        UiThread.RunAsync(async () =>
        {
            var page = newPage();
            var window = await UiThread.ShowAsync(new Window { Content = page, Width = 1300, Height = 800 });
            try
            {
                await test(page);
            }
            finally
            {
                window.Close();
            }
        });

    private async Task SettledAsync()
    {
        await _schedule.SettledAsync();
        await UiThread.IdleAsync();
    }

    private static Border DayBorder(MonthCalendarControl calendar, CalendarDayViewModel day)
    {
        var presenter = (ContentPresenter)calendar.DaysList.ItemContainerGenerator.ContainerFromItem(day);
        return (Border)presenter.ContentTemplate.FindName("DayBorder", presenter);
    }

    private static void Press(UIElement element, MouseButton button) =>
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button)
        {
            RoutedEvent = button == MouseButton.Left ? UIElement.MouseLeftButtonDownEvent : UIElement.MouseRightButtonDownEvent,
        });

    [Fact]
    public Task The_schedule_page_opens_on_the_restored_employee() => WithWindowAsync(
        () => new SchedulePage(Vm),
        async page =>
        {
            _schedule.ViewState.Schedule.SelectedEmployeeId = 30;

            await page.OnNavigatedToAsync();
            await Until.TrueAsync(() => _schedule.Day(TestSchedule.Sep1).Entry is not null, "Cruz's schedule");
            await SettledAsync();

            Assert.Same(Vm.Tree.VisibleDepartments, page.EmployeeTree.ItemsSource);
            Assert.Same(TestRoster.Node(Vm.Tree.VisibleDepartments, 3), page.EmployeeTree.SelectedItem);
            Assert.Equal("Cruz, Juan", page.CalendarHeaderText.Text);
            Assert.Equal(Vm.Calendar.DisplayedMonthText, page.DisplayedMonthText.Text);
            Assert.Equal("Assign Schedule to Multiple Employees", page.MultiSelectButton.Label);
            Assert.Equal(Visibility.Collapsed, page.MultiSelectPanel.Visibility);
            Assert.Equal("Set Schedule for Selected Days", page.SetScheduleButton.Label);
            Assert.Equal("", page.RefreshOrCancelButton.Tag);
            Assert.Equal(42, page.MonthCalendar.DaysList.Items.Count);
            Assert.True(page.RefreshOrCancelButton.Command.CanExecute(null));

            page.NextMonthButton.Command.Execute(null);
            await UiThread.IdleAsync();
            Assert.Equal(new DateTime(2026, 10, 1).ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture),
                page.DisplayedMonthText.Text);
            page.PreviousMonthButton.Command.Execute(null);
            await SettledAsync();

            page.TreeSearchBox.Text = "Dizon";
            await UiThread.IdleAsync();
            Assert.Equal("Dizon", Vm.Tree.SearchText);
            Assert.Equal(["Kitchen"], Vm.Tree.VisibleDepartments.Select(d => d.Name));
        });

    [Fact]
    public Task Multi_select_mode_shows_the_checkboxes_panel() => WithWindowAsync(
        () => new SchedulePage(Vm),
        async page =>
        {
            await page.OnNavigatedToAsync();
            await SettledAsync();

            page.MultiSelectButton.Command.Execute(null);
            await SettledAsync();

            Assert.Equal("Cancel Multi-Select", page.MultiSelectButton.Label);
            Assert.Equal(Visibility.Visible, page.MultiSelectPanel.Visibility);
            Assert.Equal("Multiple employees -- select days, then assign", page.CalendarHeaderText.Text);
            Assert.Equal("0 employee(s) checked", page.CheckedCountText.Text);
            Assert.False(page.ClearCheckedButton.Command.CanExecute(null));

            TestRoster.Node(Vm.Tree.Departments, 2).IsSelected = true;
            await UiThread.IdleAsync();
            Assert.Equal("1 employee(s) checked", page.CheckedCountText.Text);
            Assert.True(page.ClearCheckedButton.Command.CanExecute(null));
        });

    [Fact]
    public Task The_calendar_selects_by_click_and_offers_the_days_actions() => WithWindowAsync(
        () => new SchedulePage(Vm),
        async page =>
        {
            _schedule.ViewState.Schedule.SelectedEmployeeId = 30;
            await page.OnNavigatedToAsync();
            await Until.TrueAsync(() => _schedule.Day(TestSchedule.Sep1).AttendanceStatus is not null, "Cruz's markers");
            await SettledAsync();

            var sep1 = _schedule.Day(TestSchedule.Sep1);
            var sep2 = _schedule.Day(TestSchedule.Sep1.AddDays(1));
            sep2.IsSelected = true;

            Press(DayBorder(page.MonthCalendar, sep1), MouseButton.Left);
            page.MonthCalendar.DaysList.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent,
            });
            await UiThread.IdleAsync();
            Assert.Equal([TestSchedule.Sep1], Vm.Calendar.GetSelectedDates());
            Assert.Equal("Edit Schedule for Selected Days", page.SetScheduleButton.Label);

            var border = DayBorder(page.MonthCalendar, sep1);
            Press(border, MouseButton.Right);
            await UiThread.IdleAsync();
            var menu = border.ContextMenu!;
            try
            {
                var headers = menu.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToList();
                Assert.Equal(
                    ["Set Schedule As", "Edit Schedule for Selected Days", "Remove Schedule for Selected Days",
                     "Mark as Holiday…", "Add Manual Entry…", "Edit Punches…"],
                    headers);

                var setAs = (MenuItem)menu.Items[0]!;
                Assert.Equal(Enum.GetValues<Core.Enums.ScheduleType>().Length, setAs.Items.Count);
                var addManualEntry = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "Add Manual Entry…");
                Assert.Same(sep1, addManualEntry.CommandParameter);
                Assert.Same(Vm.Assignment.AddManualEntryForDayCommand, addManualEntry.Command);
            }
            finally
            {
                menu.IsOpen = false;
            }

            // A holiday offers its removal instead of marking.
            var holiday = _schedule.Day(new DateOnly(2026, 9, 21));
            var holidayBorder = DayBorder(page.MonthCalendar, holiday);
            Press(holidayBorder, MouseButton.Right);
            await UiThread.IdleAsync();
            try
            {
                Assert.Contains(holidayBorder.ContextMenu!.Items.OfType<MenuItem>(), i => (string)i.Header == "Remove Holiday");
                Assert.True(holiday.IsSelected);
                Assert.False(sep1.IsSelected);
            }
            finally
            {
                holidayBorder.ContextMenu!.IsOpen = false;
            }
        });

    [Fact]
    public Task The_employees_page_follows_the_shared_selection_and_finds_people() => WithWindowAsync(
        () => new EmployeesPage(Vm, _schedule.StatusBar),
        async page =>
        {
            await Vm.OpenCommand.Execute();
            Vm.Tree.SelectedEmployee = _schedule.Pin(4);
            Vm.Tree.SelectedDepartment = _schedule.Roster.Kitchen;
            await SettledAsync();

            await page.OnNavigatedToAsync();
            await SettledAsync();
            await UiThread.IdleAsync();

            var kitchen = Vm.Tree.Departments[1];
            Assert.Same(kitchen, page.DepartmentTree.SelectedItem);
            Assert.Same(TestRoster.Node(Vm.Tree.Departments, 4), page.EmployeesGrid.SelectedItem);
            Assert.True(page.EditEmployeeButton.Command.CanExecute(null));

            // Search jumps to the match, wherever it is.
            page.EmployeeSearchBox.Text = "Flores";
            page.SearchButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await SettledAsync();
            await UiThread.IdleAsync();
            Assert.Same(Vm.Tree.Departments[2], page.DepartmentTree.SelectedItem);
            Assert.Same(_schedule.Pin(6), Vm.Tree.SelectedEmployee);
            Assert.Null(Vm.Tree.SelectedDepartment);

            page.EmployeeSearchBox.Text = "bake";
            page.SearchButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            await SettledAsync();
            Assert.Same(Vm.Tree.Departments[0], page.DepartmentTree.SelectedItem);
            Assert.Null(Vm.Tree.SelectedEmployee);
            Assert.Same(_schedule.Roster.Bakery, Vm.Tree.SelectedDepartment);
            Assert.False(page.EditEmployeeButton.Command.CanExecute(null));

            page.EmployeeSearchBox.Text = "nobody";
            page.SearchButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            _schedule.StatusBar.Received(1).Show(Arg.Any<string>(), "No employee or department found matching \"nobody\".",
                StatusKind.Caution, Arg.Any<TimeSpan>());

            // A reload (after an edit here) keeps the department showing.
            await Vm.Tree.LoadCommand.Execute();
            await SettledAsync();
            await UiThread.IdleAsync();
            Assert.Same(Vm.Tree.Departments[0], page.DepartmentTree.SelectedItem);
        });
}
