using System.Reactive.Linq;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Schedule;
using Xunit;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Tests;

public class EmployeeEditorViewModelTests
{
    private static readonly Department Bakery = new() { Id = 1, Name = "Bakery" };
    private static readonly Department Kitchen = new() { Id = 2, Name = "Kitchen" };
    private readonly List<string> _notices = [];

    public EmployeeEditorViewModelTests() => ReactiveTestSetup.EnsureInitialized();

    private EmployeeEditorViewModel NewEditor(
        int? preselectedDepartmentId = null, Employee? existing = null, IReadOnlySet<int>? takenPins = null)
    {
        var vm = new EmployeeEditorViewModel([Bakery, Kitchen], preselectedDepartmentId, new PayrollPolicy(), 9.5,
            existing, takenPins);
        vm.Notify.RegisterHandler(ctx =>
        {
            _notices.Add(ctx.Input.Message);
            ctx.SetOutput(RxVoid.Default);
        });
        return vm;
    }

    private static async Task<bool> AcceptAsync(EmployeeEditorViewModel vm) => await vm.AcceptCommand.Execute();

    [Fact]
    public void A_new_employee_starts_with_the_usual_eligibility_and_the_company_defaults_as_placeholders()
    {
        var vm = NewEditor(preselectedDepartmentId: 2);

        Assert.Equal("New Employee", vm.Title);
        Assert.Same(Kitchen, vm.Department);
        Assert.True(vm.QualifiesForOvertime);
        Assert.True(vm.ApplyOvertimeRatePercentageByDefault);
        Assert.True(vm.QualifiesForNightDiff);
        Assert.False(vm.QualifiesForRestDayPay);
        Assert.False(vm.IsMonthly);
        Assert.Equal("Daily rate", vm.RateLabel);
        Assert.Equal(0m, vm.DailyRate);
        Assert.Null(vm.RestDayPremiumPercent);
        Assert.Equal("30 %", vm.RestDayPremiumPlaceholder);
        Assert.Equal("100 %", vm.HolidayPremiumPlaceholder);
        Assert.Equal(9.5.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture), vm.DefaultWorkTimePlaceholder);
        Assert.Null(vm.AcceptedDetails);
    }

    [Fact]
    public void Without_a_preselection_the_first_department_is_picked()
    {
        Assert.Same(Bakery, NewEditor().Department);
    }

    [Fact]
    public void The_rate_label_follows_the_pay_type()
    {
        var vm = NewEditor();
        vm.IsMonthly = true;
        Assert.Equal("Monthly rate", vm.RateLabel);
        vm.IsMonthly = false;
        Assert.Equal("Daily rate", vm.RateLabel);
    }

    [Fact]
    public void Editing_loads_every_field()
    {
        var existing = new Employee
        {
            Id = 30, Pin = 3, LastName = "Cruz", FirstName = "Juan", DepartmentId = 2,
            QualifiesForOvertime = false, QualifiesForRestDayPay = true, EmployeeType = EmployeeType.Monthly,
            DailyRate = 500m, MonthlyRate = 15000m, RestDayWorkPremiumPercentage = 0.35m, DefaultWorkTimeHours = 8m,
            DefaultSss = 450m, ClockOutBufferAfterHours = 2,
        };

        var vm = NewEditor(existing: existing);

        Assert.Equal("Edit Employee", vm.Title);
        Assert.Equal(3, vm.Pin);
        Assert.Equal("Cruz", vm.LastName);
        Assert.Same(Kitchen, vm.Department);
        Assert.False(vm.IsUnassigned);
        Assert.False(vm.QualifiesForOvertime);
        Assert.True(vm.QualifiesForRestDayPay);
        Assert.True(vm.IsMonthly);
        Assert.Equal("Monthly rate", vm.RateLabel);
        Assert.Equal(15000m, vm.MonthlyRate);
        Assert.Equal(35d, vm.RestDayPremiumPercent!.Value, 6);
        Assert.Null(vm.HolidayPremiumPercent);
        Assert.Equal(8d, vm.DefaultWorkTimeHours);
        Assert.Equal(450m, vm.DefaultSss);
        Assert.Equal(2d, vm.ClockOutBufferAfterHours);
    }

    [Fact]
    public void An_unassigned_employee_opens_unassigned()
    {
        var vm = NewEditor(existing: new Employee { Id = 60, Pin = 6, LastName = "Flores", FirstName = "Juan" });

        Assert.True(vm.IsUnassigned);
        Assert.Null(vm.Department);
    }

    [Fact]
    public async Task OK_needs_both_names()
    {
        var vm = NewEditor();
        vm.Pin = 7;
        vm.LastName = "Garcia";

        Assert.False(await AcceptAsync(vm));
        Assert.Equal(["First and last name are required."], _notices);
        Assert.Null(vm.AcceptedDetails);
    }

    [Fact]
    public async Task OK_needs_a_department_unless_left_unassigned()
    {
        var vm = new EmployeeEditorViewModel([], null, new PayrollPolicy(), 10);
        vm.Notify.RegisterHandler(ctx =>
        {
            _notices.Add(ctx.Input.Message);
            ctx.SetOutput(RxVoid.Default);
        });
        vm.Pin = 7;
        vm.LastName = "Garcia";
        vm.FirstName = "Ana";

        Assert.False(await AcceptAsync(vm));
        Assert.Contains("Select a department", _notices.Single(), StringComparison.Ordinal);

        vm.IsUnassigned = true;
        Assert.True(await AcceptAsync(vm));
        Assert.Null(vm.AcceptedDetails!.DepartmentId);
    }

    [Fact]
    public async Task OK_needs_a_free_employee_id()
    {
        var vm = NewEditor(takenPins: new HashSet<int> { 3 });
        vm.LastName = "Garcia";
        vm.FirstName = "Ana";

        Assert.False(await AcceptAsync(vm));
        vm.Pin = 3;
        Assert.False(await AcceptAsync(vm));
        Assert.Equal(
            ["Employee ID is required.", "Employee ID 3 is already assigned to another employee. Choose a different ID."],
            _notices);

        vm.Pin = 4;
        Assert.True(await AcceptAsync(vm));
        Assert.Equal(4, vm.AcceptedDetails!.Pin);
    }

    [Fact]
    public async Task OK_settles_trimmed_names_fractions_and_blank_money_as_zero()
    {
        var vm = NewEditor(preselectedDepartmentId: 1);
        vm.Pin = 7;
        vm.LastName = "  Garcia ";
        vm.FirstName = " Ana";
        vm.DailyRate = 610m;
        vm.DefaultAllowance = null;
        vm.RestDayPremiumPercent = 32.5;
        vm.DefaultWorkTimeHours = 8.256;
        vm.ClockInBufferBeforeHours = 1.5;

        Assert.True(await AcceptAsync(vm));

        var details = vm.AcceptedDetails!;
        Assert.Equal("Garcia", details.LastName);
        Assert.Equal("Ana", details.FirstName);
        Assert.Equal(1, details.DepartmentId);
        Assert.Equal(EmployeeType.Daily, details.EmployeeType);
        Assert.Equal(610m, details.DailyRate);
        Assert.Equal(0m, details.MonthlyRate);
        Assert.Equal(0m, details.DefaultAllowance);
        Assert.Equal(0.325m, details.RestDayWorkPremiumPercentage);
        Assert.Null(details.HolidayPremiumPercentage);
        Assert.Equal(8.26m, details.DefaultWorkTimeHours);
        Assert.Equal(1.5, details.ClockInBufferBeforeHours);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task Only_the_active_pay_types_rate_is_read_and_the_other_is_carried_forward()
    {
        var existing = new Employee
        {
            Id = 30, Pin = 3, LastName = "Cruz", FirstName = "Juan", DepartmentId = 2,
            EmployeeType = EmployeeType.Daily, DailyRate = 500m, MonthlyRate = 12000m,
        };
        var vm = NewEditor(existing: existing);

        vm.DailyRate = 999m;
        vm.IsMonthly = true;
        vm.MonthlyRate = 16000m;

        Assert.True(await AcceptAsync(vm));
        Assert.Equal(EmployeeType.Monthly, vm.AcceptedDetails!.EmployeeType);
        Assert.Equal(16000m, vm.AcceptedDetails.MonthlyRate);
        Assert.Equal(500m, vm.AcceptedDetails.DailyRate);
    }
}

public class TextPromptViewModelTests
{
    public TextPromptViewModelTests() => ReactiveTestSetup.EnsureInitialized();

    [Fact]
    public async Task OK_needs_something_typed_and_settles_it_trimmed()
    {
        var vm = new TextPromptViewModel("New Department", "Department name:", "Bakery");
        var notices = 0;
        vm.Notify.RegisterHandler(ctx =>
        {
            notices++;
            ctx.SetOutput(RxVoid.Default);
        });

        Assert.Equal("Bakery", vm.Text);

        vm.Text = "   ";
        Assert.False(await vm.AcceptCommand.Execute());
        Assert.Equal(1, notices);

        vm.Text = "  Pastry ";
        Assert.True(await vm.AcceptCommand.Execute());
        Assert.Equal("Pastry", vm.Value);
    }
}
