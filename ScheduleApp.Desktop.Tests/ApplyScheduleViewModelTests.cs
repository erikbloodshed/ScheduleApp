using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Desktop.Controls;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Schedule;
using ScheduleApp.Desktop.Views;
using Xunit;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Tests;

public class ApplyScheduleViewModelTests
{
    private static readonly DateOnly Sep1 = new(2026, 9, 1);
    private static readonly (DateOnly, DateOnly)[] TwoRanges = [(Sep1, Sep1.AddDays(2)), (Sep1.AddDays(7), Sep1.AddDays(7))];

    private readonly List<string> _notices = [];

    public ApplyScheduleViewModelTests() => ReactiveTestSetup.EnsureInitialized();

    internal static Employee Cruz(Action<Employee>? tweak = null)
    {
        var employee = TestRoster.Employee(3, "Cruz");
        tweak?.Invoke(employee);
        return employee;
    }

    private ApplyScheduleViewModel NewEditor(
        IReadOnlyList<Employee>? employees = null, ScheduleEntry? prefill = null, ScheduleType? presetType = null, bool isEditing = false)
    {
        var vm = new ApplyScheduleViewModel(employees ?? [Cruz()], TwoRanges, isEditing, prefill, presetType,
            new AttendanceSettings(), new PayrollPolicy());
        vm.Notify.RegisterHandler(ctx =>
        {
            _notices.Add(ctx.Input.Message);
            ctx.SetOutput(RxVoid.Default);
        });
        return vm;
    }

    private static async Task<ScheduleChoice?> AcceptAsync(ApplyScheduleViewModel vm) =>
        await vm.AcceptCommand.Execute() ? vm.AcceptedChoice : null;

    private static void Type(DefaultedText box, string text) => box.Text = text;

    [Fact]
    public void A_new_schedule_starts_as_a_normal_day_with_the_defaults()
    {
        var vm = NewEditor();

        Assert.Equal("Set Schedule for Selected Days", vm.Title);
        Assert.Equal("Cruz, Juan", vm.EmployeeHeader);
        Assert.Equal("Sep 1 - Sep 3, 2026\nSep 8, 2026\n\nTotal: 4 day(s) across 2 range(s)", vm.SelectedDaysSummary);
        Assert.False(vm.ShowsMixedScheduleNote);
        Assert.Equal(ScheduleType.Normal, vm.SelectedType);
        Assert.Equal("10", vm.WorkTimeText);
        Assert.Equal(new TimeOnly(5, 0), vm.TimeIn);
        Assert.Empty(vm.Segments);

        Assert.True(vm.ClockInBufferBefore.IsDefault);
        Assert.Equal("2", vm.ClockInBufferBefore.Text);
        Assert.Equal("6", vm.ClockOutBufferAfter.Text);
        Assert.True(vm.ClockInBufferBefore.GraysDefault);
        Assert.Equal("0.25", vm.OvertimeRatePercentage.Text);
        Assert.Equal("Use employee default (Eligible)", vm.OvertimeEligibleOptions[0]);
        Assert.Equal("Use employee default (Apply premium)", vm.ApplyOvertimeRateOptions[0]);
        Assert.Equal(0, vm.OvertimeEligibleIndex);

        Assert.True(vm.ShowsWorkTime);
        Assert.True(vm.ShowsTimeIn);
        Assert.True(vm.ShowsNormalBuffers);
        Assert.True(vm.ShowsOvertimeNightDiff);
        Assert.False(vm.ShowsRestDayDuty);
        Assert.False(vm.ShowsLeavePay);
        Assert.False(vm.ShowsRestrictedWindow);
        Assert.False(vm.ShowsSegments);
    }

    [Fact]
    public void One_employees_own_defaults_seed_the_form()
    {
        var vm = NewEditor([Cruz(e =>
        {
            e.DefaultWorkTimeHours = 8.5m;
            e.DefaultLeaveIsPaid = false;
            e.ClockInBufferBeforeHours = 1;
            e.QualifiesForNightDiff = false;
        })]);

        Assert.Equal("8.5", vm.WorkTimeText);
        Assert.False(vm.IsPaidLeave);
        Assert.Equal("1", vm.ClockInBufferBefore.Text);
        Assert.Equal("Use employee default (Not eligible)", vm.NightDiffEligibleOptions[0]);
    }

    [Fact]
    public void Several_employees_show_only_the_defaults_they_share()
    {
        var employees = Enumerable.Range(1, 7)
            .Select(pin => TestRoster.Employee(pin, $"Name{pin}"))
            .ToList();
        employees[0].ClockInBufferBeforeHours = 3;
        employees[1].QualifiesForOvertime = false;
        employees[2].DefaultWorkTimeHours = 9m;
        employees[3].DefaultLeaveIsPaid = false;

        var vm = NewEditor(employees);

        Assert.Equal("Set Schedule for 7 Employees", vm.Title);
        Assert.Equal("7 employees selected: Name1, Juan, Name2, Juan, Name3, Juan, Name4, Juan, Name5, Juan, and 2 more", vm.EmployeeHeader);
        Assert.Equal("(varies)", vm.ClockInBufferBefore.Text);
        Assert.Equal("2", vm.ClockInBufferAfter.Text);
        Assert.Equal("Use employee default (varies)", vm.OvertimeEligibleOptions[0]);
        Assert.Equal("10", vm.WorkTimeText);
        Assert.True(vm.IsPaidLeave);
    }

    [Fact]
    public void The_fields_follow_the_type()
    {
        var vm = NewEditor([Cruz(e => e.QualifiesForRestDayPay = true)]);

        vm.SelectedType = ScheduleType.Leave;
        Assert.True(vm.ShowsLeavePay);
        Assert.False(vm.ShowsWorkTime);
        Assert.False(vm.ShowsOvertimeNightDiff);

        vm.SelectedType = ScheduleType.OfficialBusiness;
        Assert.True(vm.ShowsWorkTime);
        Assert.True(vm.ShowsTimeIn);
        Assert.False(vm.ShowsNormalBuffers);
        Assert.False(vm.ShowsOvertimeNightDiff);

        vm.SelectedType = ScheduleType.Flexible;
        Assert.True(vm.ShowsWorkTime);
        Assert.False(vm.ShowsTimeIn);
        Assert.True(vm.ShowsRestrictedWindow);
        Assert.True(vm.ShowsOvertimeNightDiff);

        vm.SelectedType = ScheduleType.RestDay;
        Assert.True(vm.ShowsRestDayDuty);
        Assert.False(vm.ShowsWorkTime);
        Assert.False(vm.ShowsNormalBuffers);
        Assert.True(vm.ShowsOvertimeNightDiff);
        vm.IsRestDayDuty = true;
        Assert.True(vm.ShowsWorkTime);
        Assert.True(vm.ShowsTimeIn);
        Assert.True(vm.ShowsNormalBuffers);
    }

    [Fact]
    public void Split_shift_starts_with_one_window_but_only_the_first_time()
    {
        var vm = NewEditor();

        vm.SelectedType = ScheduleType.SplitShift;
        Assert.True(vm.ShowsSegments);
        var segment = Assert.Single(vm.Segments);
        Assert.Equal(new TimeOnly(5, 0), segment.TimeIn);
        Assert.Equal(new TimeOnly(21, 0), segment.TimeOut);
        Assert.Equal("1", segment.ClockInBuffer.Text);

        vm.RemoveSegmentCommand.Execute(segment).Subscribe();
        vm.SelectedType = ScheduleType.Normal;
        vm.SelectedType = ScheduleType.SplitShift;
        Assert.Empty(vm.Segments);

        vm.AddSegmentCommand.Execute().Subscribe();
        Assert.Null(Assert.Single(vm.Segments).TimeIn);
    }

    [Fact]
    public void A_rest_day_duty_needs_every_employee_to_qualify()
    {
        var none = NewEditor([Cruz()]);
        Assert.False(none.CanScheduleRestDayDuty);
        Assert.Equal("None of the selected employees are eligible for Rest Day Pay.", none.RestDayDutyToolTip);

        var some = NewEditor([Cruz(e => e.QualifiesForRestDayPay = true), TestRoster.Employee(4, "Dizon")]);
        Assert.False(some.CanScheduleRestDayDuty);
        Assert.Equal("Not all selected employees are eligible for Rest Day Pay.", some.RestDayDutyToolTip);

        var all = NewEditor([Cruz(e => e.QualifiesForRestDayPay = true)]);
        Assert.True(all.CanScheduleRestDayDuty);
        Assert.StartsWith("Unchecked (default)", all.RestDayDutyToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void Editing_prefills_what_the_days_share()
    {
        var prefill = new ScheduleEntry
        {
            ScheduleType = ScheduleType.SplitShift, WorkTimeHours = 8m, OvertimeEligibleOverride = false,
            OvertimeRatePercentageOverride = 0.3m,
            FlexibleSegments =
            [
                new FlexibleSegment { TimeIn = new TimeOnly(13, 0), TimeOut = new TimeOnly(18, 0), ClockOutBufferHours = 0.5 },
                new FlexibleSegment { TimeIn = new TimeOnly(6, 0), TimeOut = new TimeOnly(11, 0) },
            ],
        };

        var vm = NewEditor(prefill: prefill, isEditing: true);

        Assert.Equal("Edit Schedule for Selected Days", vm.Title);
        Assert.Equal(ScheduleType.SplitShift, vm.SelectedType);
        Assert.Equal("8", vm.WorkTimeText);
        Assert.Equal([new TimeOnly(6, 0), new TimeOnly(13, 0)], vm.Segments.Select(s => s.TimeIn!.Value));
        Assert.True(vm.Segments[0].ClockOutBuffer.IsDefault);
        Assert.False(vm.Segments[0].ClockOutBuffer.GraysDefault);
        Assert.False(vm.Segments[1].ClockOutBuffer.IsDefault);
        Assert.Equal("0.5", vm.Segments[1].ClockOutBuffer.Text);
        Assert.Equal(2, vm.OvertimeEligibleIndex);
        Assert.Equal("0.3", vm.OvertimeRatePercentage.Text);
        Assert.False(vm.NightDiffRatePercentage.GraysDefault);
        Assert.Equal(new TimeOnly(5, 0), vm.RestrictedTimeIn);
    }

    [Fact]
    public void The_preset_type_wins_over_the_prefill_and_mixed_days_are_explained()
    {
        var vm = NewEditor(prefill: new ScheduleEntry { ScheduleType = ScheduleType.Normal, TimeIn = new TimeOnly(9, 0) },
            presetType: ScheduleType.Flexible, isEditing: true);
        Assert.Equal(ScheduleType.Flexible, vm.SelectedType);
        Assert.Equal(new TimeOnly(9, 0), vm.TimeIn);

        Assert.True(NewEditor(isEditing: true).ShowsMixedScheduleNote);
    }

    [Fact]
    public void A_saved_rest_day_with_a_window_reopens_as_a_duty()
    {
        var prefill = new ScheduleEntry { ScheduleType = ScheduleType.RestDay, WorkTimeHours = 8m, TimeIn = new TimeOnly(7, 0) };

        Assert.True(NewEditor([Cruz(e => e.QualifiesForRestDayPay = true)], prefill).IsRestDayDuty);
        Assert.False(NewEditor([Cruz()], prefill).IsRestDayDuty);
    }

    [Fact]
    public async Task A_normal_day_settles_its_window_and_typed_overrides()
    {
        var vm = NewEditor();
        vm.WorkTimeText = "9.5";
        vm.TimeIn = new TimeOnly(8, 0);
        Type(vm.ClockOutBufferAfter, "3");
        Type(vm.NightDiffRatePercentage, "0.15");
        vm.OvertimeEligibleIndex = 2;
        vm.ApplyOvertimeRateIndex = 1;

        var choice = await AcceptAsync(vm);

        Assert.NotNull(choice);
        Assert.Equal(ScheduleType.Normal, choice.ScheduleType);
        Assert.Equal(9.5m, choice.WorkTimeHours);
        Assert.Equal(new TimeOnly(8, 0), choice.TimeIn);
        Assert.Null(choice.ClockInBufferBeforeHours);
        Assert.Equal(3, choice.ClockOutBufferAfterHours);
        Assert.Equal(0.15m, choice.NightDiffRatePercentageOverride);
        Assert.Null(choice.OvertimeRatePercentageOverride);
        Assert.False(choice.OvertimeEligibleOverride);
        Assert.Null(choice.NightDiffEligibleOverride);
        Assert.True(choice.ApplyOvertimeRatePercentageOverride);
        Assert.Null(choice.IsPaidLeave);
        Assert.Empty(choice.FlexibleSegments);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task Leave_settles_only_whether_its_paid()
    {
        var vm = NewEditor();
        vm.SelectedType = ScheduleType.Leave;
        vm.IsPaidLeave = false;
        vm.WorkTimeText = "nonsense";

        var choice = await AcceptAsync(vm);

        Assert.Equal(ScheduleType.Leave, choice!.ScheduleType);
        Assert.False(choice.IsPaidLeave);
        Assert.Null(choice.WorkTimeHours);
        Assert.Null(choice.TimeIn);
        Assert.Null(choice.ClockInBufferBeforeHours);
        Assert.Null(choice.OvertimeEligibleOverride);
        Assert.Empty(choice.FlexibleSegments);
    }

    [Fact]
    public async Task Official_business_takes_no_buffers_or_overtime()
    {
        var vm = NewEditor();
        vm.SelectedType = ScheduleType.OfficialBusiness;
        Type(vm.ClockInBufferBefore, "4");
        vm.OvertimeEligibleIndex = 1;

        var choice = await AcceptAsync(vm);

        Assert.Equal(new TimeOnly(5, 0), choice!.TimeIn);
        Assert.Null(choice.ClockInBufferBeforeHours);
        Assert.Null(choice.OvertimeEligibleOverride);
    }

    [Fact]
    public async Task A_plain_rest_day_has_no_window()
    {
        var vm = NewEditor();
        vm.SelectedType = ScheduleType.RestDay;
        vm.WorkTimeText = string.Empty;

        var choice = await AcceptAsync(vm);

        Assert.Null(choice!.WorkTimeHours);
        Assert.Null(choice.TimeIn);
    }

    [Fact]
    public async Task A_flexible_day_needs_a_real_window()
    {
        var vm = NewEditor();
        vm.SelectedType = ScheduleType.Flexible;
        vm.RestrictedTimeOut = vm.RestrictedTimeIn;

        Assert.Null(await AcceptAsync(vm));
        vm.RestrictedTimeOut = null;
        Assert.Null(await AcceptAsync(vm));
        Assert.Equal(["Time in and time out can't be the same.", "Select a time out for the punching window."], _notices);

        vm.RestrictedTimeOut = new TimeOnly(20, 0);
        var choice = await AcceptAsync(vm);
        Assert.Equal(new TimeOnly(5, 0), choice!.RestrictedTimeIn);
        Assert.Equal(new TimeOnly(20, 0), choice.RestrictedTimeOut);
        Assert.Null(choice.TimeIn);
    }

    [Fact]
    public async Task Hours_must_be_a_positive_number()
    {
        var vm = NewEditor();
        vm.WorkTimeText = "0";
        Assert.Null(await AcceptAsync(vm));

        vm.SelectedType = ScheduleType.SplitShift;
        vm.WorkTimeText = "eight";
        Assert.Null(await AcceptAsync(vm));

        Assert.Equal(["Enter a valid work time in hours, e.g. 9 or 9.5.", "Enter a valid required hours total, e.g. 8 or 8.5."], _notices);
    }

    [Fact]
    public async Task An_override_must_be_a_number_of_zero_or_more()
    {
        var vm = NewEditor();
        Type(vm.ClockInBufferAfter, "-1");

        Assert.Null(await AcceptAsync(vm));
        Assert.Equal(
            ["Enter a valid clock-in buffer, after scheduled time in hours (0 or more), or leave it at the grayed-out policy default."],
            _notices);

        vm.ClockInBufferAfter.Text = string.Empty;
        Assert.NotNull(await AcceptAsync(vm));
    }

    [Fact]
    public async Task Split_shift_windows_are_sorted_and_checked_for_overlap_and_length()
    {
        var vm = NewEditor();
        vm.SelectedType = ScheduleType.SplitShift;
        vm.WorkTimeText = "8";
        var first = vm.Segments[0];
        first.TimeIn = new TimeOnly(22, 0);
        first.TimeOut = new TimeOnly(6, 0);   // crosses midnight: 8 hours
        vm.AddSegmentCommand.Execute().Subscribe();
        var second = vm.Segments[1];
        second.TimeIn = new TimeOnly(23, 0);
        second.TimeOut = new TimeOnly(23, 30);

        Assert.Null(await AcceptAsync(vm));
        Assert.StartsWith("Punching windows can't overlap", _notices[^1], StringComparison.Ordinal);

        first.TimeIn = new TimeOnly(18, 0);
        first.TimeOut = new TimeOnly(22, 0);
        Assert.Null(await AcceptAsync(vm));
        Assert.StartsWith("The punching windows add up to only 4.5 hour(s)", _notices[^1], StringComparison.Ordinal);

        second.TimeOut = new TimeOnly(4, 0);
        Type(second.ClockInBuffer, "0.5");
        var choice = await AcceptAsync(vm);

        (TimeOnly, TimeOnly, double?, double?)[] expected =
            [(new TimeOnly(18, 0), new TimeOnly(22, 0), null, null), (new TimeOnly(23, 0), new TimeOnly(4, 0), 0.5, null)];
        Assert.Equal(expected, choice!.FlexibleSegments);
        Assert.Null(choice.TimeIn);
    }

    [Fact]
    public async Task Every_split_shift_window_needs_both_times()
    {
        var vm = NewEditor();
        vm.SelectedType = ScheduleType.SplitShift;
        vm.Segments[0].TimeOut = vm.Segments[0].TimeIn;
        Assert.Null(await AcceptAsync(vm));
        vm.Segments[0].TimeIn = null;
        Assert.Null(await AcceptAsync(vm));

        Assert.StartsWith("Each punching window's start and end time can't be the same", _notices[0], StringComparison.Ordinal);
        Assert.Equal("Select a start time for each punching window.", _notices[1]);

        vm.RemoveSegmentCommand.Execute(vm.Segments[0]).Subscribe();
        Assert.Empty((await AcceptAsync(vm))!.FlexibleSegments);
    }
}

public class ApplyScheduleDialogTests
{
    private static ApplyScheduleDialog NewDialog() => new()
    {
        ViewModel = new ApplyScheduleViewModel([ApplyScheduleViewModelTests.Cruz(e => e.QualifiesForRestDayPay = true)],
            [(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 2))], isEditing: false, prefill: null, presetType: null,
            new AttendanceSettings(), new PayrollPolicy()),
    };

    [Fact]
    public Task Fields_follow_the_type_and_bind_both_ways() => UiThread.RunAsync(async () =>
    {
        var dialog = await UiThread.ShowAsync(NewDialog());
        try
        {
            var vm = dialog.ViewModel!;
            Assert.Equal("Set Schedule for Selected Days", dialog.Title);
            Assert.Equal("Cruz, Juan", dialog.EmployeeHeader.Text);
            Assert.Equal(Visibility.Collapsed, dialog.MixedScheduleNote.Visibility);
            Assert.Equal(ScheduleType.Normal, dialog.TypeCombo.SelectedItem);
            Assert.Equal("10", dialog.WorkTimeBox.Text);
            Assert.Equal(new TimeOnly(5, 0), dialog.TimeInPicker.SelectedTime);
            Assert.Equal(Visibility.Visible, dialog.NormalBufferBorder.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.SegmentsBorder.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.RestDayDutyCheckBox.Visibility);
            Assert.Equal("2", dialog.ClockInBufferBeforeBox.Text);
            Assert.Equal(Brushes.Gray, dialog.ClockInBufferBeforeBox.Foreground);
            Assert.Equal(3, dialog.OvertimeEligibleCombo.Items.Count);
            Assert.Equal(0, dialog.OvertimeEligibleCombo.SelectedIndex);

            dialog.WorkTimeBox.Text = "7";
            dialog.ClockInBufferBeforeBox.Text = "1.5";
            dialog.NightDiffEligibleCombo.SelectedIndex = 2;
            await UiThread.IdleAsync();
            Assert.Equal("7", vm.WorkTimeText);
            Assert.Equal("1.5", vm.ClockInBufferBefore.Text);
            Assert.False(vm.ClockInBufferBefore.IsDefault);
            Assert.NotEqual(Brushes.Gray, dialog.ClockInBufferBeforeBox.Foreground);
            Assert.Equal(2, vm.NightDiffEligibleIndex);

            dialog.TypeCombo.SelectedItem = ScheduleType.RestDay;
            await UiThread.IdleAsync();
            Assert.Equal(ScheduleType.RestDay, vm.SelectedType);
            Assert.Equal(Visibility.Visible, dialog.RestDayDutyCheckBox.Visibility);
            Assert.True(dialog.RestDayDutyCheckBox.IsEnabled);
            Assert.Equal(Visibility.Collapsed, dialog.WorkTimeBox.Visibility);
            dialog.RestDayDutyCheckBox.IsChecked = true;
            await UiThread.IdleAsync();
            Assert.True(vm.IsRestDayDuty);
            Assert.Equal(Visibility.Visible, dialog.WorkTimeBox.Visibility);

            vm.SelectedType = ScheduleType.Leave;
            await UiThread.IdleAsync();
            Assert.Equal(Visibility.Visible, dialog.LeavePayPanel.Visibility);
            Assert.True(dialog.UnpaidLeaveRadio.IsChecked);   // Cruz's own default
            dialog.PaidLeaveRadio.IsChecked = true;
            await UiThread.IdleAsync();
            Assert.True(vm.IsPaidLeave);
            Assert.False(dialog.UnpaidLeaveRadio.IsChecked);

            vm.SelectedType = ScheduleType.SplitShift;
            await UiThread.IdleAsync();
            Assert.Equal(Visibility.Visible, dialog.SegmentsBorder.Visibility);
            Assert.Equal(Visibility.Visible, dialog.AddSegmentButton.Visibility);
            dialog.AddSegmentButton.Command.Execute(null);
            await UiThread.IdleAsync();
            Assert.Equal(2, vm.Segments.Count);
            Assert.Equal(2, dialog.SegmentsList.Items.Count);

            // The first window's row: its pickers and buffer boxes follow its ViewModel.
            var row = (FrameworkElement)dialog.SegmentsList.ItemContainerGenerator.ContainerFromIndex(0);
            var pickers = Descendants<TimeInput>(row).ToList();
            Assert.Equal(new TimeOnly(5, 0), pickers[0].SelectedTime);
            pickers[1].SelectedTime = new TimeOnly(12, 0);
            var buffers = Descendants<TextBox>(row).ToList();
            Assert.Equal("1", buffers[0].Text);
            buffers[0].Text = "0.25";
            await UiThread.IdleAsync();
            Assert.Equal(new TimeOnly(12, 0), vm.Segments[0].TimeOut);
            Assert.Equal("0.25", vm.Segments[0].ClockInBuffer.Text);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task OK_closes_once_the_schedule_is_complete() => UiThread.RunAsync(async () =>
    {
        var dialog = NewDialog();

        var result = await UiThread.ShowDialogAsync(dialog, d =>
        {
            d.ViewModel!.TimeIn = new TimeOnly(7, 30);
            d.OkButton.Command.Execute(null);
            return Task.CompletedTask;
        });

        Assert.True(result);
        Assert.Equal(new TimeOnly(7, 30), dialog.ViewModel!.AcceptedChoice?.TimeIn);
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            // TimeInput's own insides are a different control's business.
            if (child is TimeInput) continue;

            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }
}
