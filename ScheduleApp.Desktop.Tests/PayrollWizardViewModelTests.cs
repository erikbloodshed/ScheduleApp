using System.Reactive.Linq;
using System.Windows.Input;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Payroll;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public class PayrollWizardViewModelTests
{
    private readonly TestRoster _roster = new();
    private readonly IPayrollRunRepository _runs = Substitute.For<IPayrollRunRepository>();
    private readonly IPayrollComputationService _payroll = TestPayroll.Computation();
    private readonly PayrollWizardViewModel _vm;

    public PayrollWizardViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _runs.CreateAsync(Arg.Any<PayrollRun>(), Arg.Any<CancellationToken>())
            .Returns(call => { var run = call.Arg<PayrollRun>(); run.Id = 42; return Task.FromResult(run); });

        _vm = new PayrollWizardViewModel(_roster.Provider, _runs, _payroll,
            new DateTime(2026, 9, 16), new DateTime(2026, 9, 30));
    }

    private static readonly int[] KitchenPins = [3, 4, 5];

    private static bool CanExecute(ICommand command) => command.CanExecute(null);

    private async Task ToReviewAsync()
    {
        await _vm.LoadEmployeeTreeCommand.Execute();
        await _vm.NextCommand.Execute();
        await _vm.NextCommand.Execute();
    }

    [Fact]
    public void Starts_on_step_one_with_a_label_from_the_period()
    {
        Assert.Equal(0, _vm.CurrentStepIndex);
        Assert.Equal("September 16-30, 2026", _vm.Label);
        Assert.True(_vm.IsPeriodAndLabelValid);
        Assert.Null(_vm.PeriodValidationMessage);
        Assert.Equal("Next", _vm.NextButtonText);
        Assert.False(CanExecute(_vm.BackCommand));
        Assert.True(CanExecute(_vm.NextCommand));
    }

    [Fact]
    public void The_label_follows_the_period_until_typed_over()
    {
        _vm.PeriodEnd = new DateTime(2026, 10, 5);
        Assert.Equal("Sep 16 - Oct 5, 2026", _vm.Label);

        _vm.PeriodStart = new DateTime(2025, 12, 29);
        Assert.Equal("Dec 29, 2025 - Oct 5, 2026", _vm.Label);

        _vm.Label = "Year-end special";
        _vm.PeriodStart = new DateTime(2026, 9, 1);
        Assert.Equal("Year-end special", _vm.Label);

        // Typing a label matching the period isn't enough -- only typing back exactly the
        // last label auto-fill itself wrote un-latches it.
        _vm.Label = "Sep 1 - Oct 5, 2026";
        _vm.PeriodEnd = new DateTime(2026, 9, 20);
        Assert.Equal("Sep 1 - Oct 5, 2026", _vm.Label);

        _vm.Label = "Dec 29, 2025 - Oct 5, 2026";
        _vm.PeriodEnd = new DateTime(2026, 9, 15);
        Assert.Equal("September 1-15, 2026", _vm.Label);
    }

    [Fact]
    public void Step_one_explains_what_blocks_next()
    {
        _vm.PeriodEnd = null;
        Assert.Equal("Choose a period start and end date.", _vm.PeriodValidationMessage);
        Assert.False(CanExecute(_vm.NextCommand));

        _vm.PeriodEnd = new DateTime(2026, 9, 1);
        Assert.Equal("Period end can't be before period start.", _vm.PeriodValidationMessage);
        Assert.False(_vm.IsPeriodAndLabelValid);

        _vm.PeriodEnd = new DateTime(2026, 9, 30);
        _vm.Label = "  ";
        Assert.Equal("Enter a label for this payroll run.", _vm.PeriodValidationMessage);
        Assert.False(CanExecute(_vm.NextCommand));

        _vm.Label = "Mid-September";
        Assert.Null(_vm.PeriodValidationMessage);
        Assert.True(CanExecute(_vm.NextCommand));
    }

    [Fact]
    public async Task Step_two_needs_someone_checked()
    {
        await _vm.LoadEmployeeTreeCommand.Execute();
        await _vm.NextCommand.Execute();

        Assert.Equal(1, _vm.CurrentStepIndex);
        Assert.Equal(6, _vm.SelectedEmployeeCount);
        Assert.Equal("Whole company (all employees checked)", _vm.SelectionScopeText);
        Assert.True(CanExecute(_vm.BackCommand));
        Assert.True(CanExecute(_vm.NextCommand));

        await _vm.ClearTreeSelectionCommand.Execute();
        Assert.Equal("Nothing selected -- check at least one employee to continue", _vm.SelectionScopeText);
        Assert.False(CanExecute(_vm.NextCommand));

        _vm.Departments[0].IsSelected = true;
        Assert.Equal("Department: Bakery", _vm.SelectionScopeText);
        Assert.True(CanExecute(_vm.NextCommand));

        await _vm.BackCommand.Execute();
        Assert.Equal(0, _vm.CurrentStepIndex);
    }

    [Fact]
    public async Task Reaching_step_three_calculates_the_review()
    {
        await _vm.LoadEmployeeTreeCommand.Execute();
        await _vm.NextCommand.Execute();
        await _vm.ClearTreeSelectionCommand.Execute();
        _vm.Departments[1].IsSelected = true;           // Kitchen: 3 people

        await _vm.NextCommand.Execute();

        Assert.Equal(2, _vm.CurrentStepIndex);
        Assert.True(_vm.IsLastStep);
        Assert.Equal("Finish", _vm.NextButtonText);
        Assert.Equal([3, 4, 5], _vm.ReviewResults.Select(r => r.EmployeeId));
        Assert.Equal(3000m, _vm.TotalGrossPay);
        Assert.Equal(300m, _vm.TotalDeductions);
        Assert.Equal(2700m, _vm.TotalNetPay);
        Assert.True(_vm.IsReviewReady);
        Assert.False(_vm.IsCalculating);
        await _payroll.Received(1).PrepareBatchAsync(
            Arg.Is<IReadOnlyCollection<int>>(p => p.Order().SequenceEqual(KitchenPins)),
            new DateOnly(2026, 9, 16), new DateOnly(2026, 9, 30), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_calculation_says_why_and_shows_no_grid()
    {
        _payroll.PrepareBatchAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("database offline"));

        await ToReviewAsync();

        Assert.Equal("Couldn't calculate payroll for review: database offline", _vm.CalculationErrorMessage);
        Assert.False(_vm.IsReviewReady);
        Assert.Empty(_vm.ReviewResults);
        Assert.Equal(0m, _vm.TotalNetPay);
        Assert.True(CanExecute(_vm.BackCommand));
    }

    [Fact]
    public async Task Save_writes_the_run_once()
    {
        await ToReviewAsync();
        Assert.True(CanExecute(_vm.SavePayrollGroupCommand));
        Assert.Null(_vm.SavedRunSummary);

        await _vm.SavePayrollGroupCommand.Execute();

        await _runs.Received(1).CreateAsync(
            Arg.Is<PayrollRun>(r => r.Label == "September 16-30, 2026"
                && r.PeriodStart == new DateOnly(2026, 9, 16)
                && r.Employees.Count == 6),
            Arg.Any<CancellationToken>());
        Assert.Equal(42, _vm.SavedRun?.Id);
        Assert.Equal(6, _vm.SavedEmployees?.Count);
        Assert.Equal("Saved as \"September 16-30, 2026\" (run #42).", _vm.SavedRunSummary);
        Assert.False(CanExecute(_vm.SavePayrollGroupCommand));
    }

    [Fact]
    public async Task A_failed_save_says_why_and_can_be_retried()
    {
        await ToReviewAsync();
        _runs.CreateAsync(Arg.Any<PayrollRun>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("locked"));

        await _vm.SavePayrollGroupCommand.Execute();

        Assert.Equal("Couldn't save this payroll run: locked", _vm.SaveErrorMessage);
        Assert.Null(_vm.SavedRun);
        Assert.True(CanExecute(_vm.SavePayrollGroupCommand));
    }
}
