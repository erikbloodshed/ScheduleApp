using System.Windows;
using NSubstitute;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.Views;
using ScheduleApp.Payroll;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public class PayrollWizardDialogTests
{
    private readonly TestRoster _roster = new();
    private readonly IPayrollRunRepository _runs = Substitute.For<IPayrollRunRepository>();
    private readonly IPayrollComputationService _payroll = TestPayroll.Computation();

    public PayrollWizardDialogTests()
    {
        _runs.CreateAsync(Arg.Any<PayrollRun>(), Arg.Any<CancellationToken>())
            .Returns(call => { var run = call.Arg<PayrollRun>(); run.Id = 42; return Task.FromResult(run); });
    }

    private PayrollWizardDialog NewDialog() => new()
    {
        ViewModel = new PayrollWizardViewModel(_roster.Provider, _runs, _payroll,
            new DateTime(2026, 9, 16), new DateTime(2026, 9, 30)),
    };

    private Task WithDialogAsync(Func<PayrollWizardDialog, Task> test) => UiThread.RunAsync(async () =>
    {
        var dialog = await UiThread.ShowAsync(NewDialog());
        try
        {
            await test(dialog);
        }
        finally
        {
            dialog.Close();
        }
    });

    private static async Task NextAsync(PayrollWizardDialog dialog)
    {
        dialog.NextButton.Command.Execute(null);
        await UiThread.IdleAsync();
    }

    [Fact]
    public Task Opens_on_step_one_with_its_label_emphasized() => WithDialogAsync(dialog =>
    {
        Assert.Equal(Visibility.Visible, dialog.Step1Panel.Visibility);
        Assert.Equal(Visibility.Collapsed, dialog.Step2Panel.Visibility);
        Assert.Equal(Visibility.Collapsed, dialog.Step3Panel.Visibility);
        Assert.Equal(FontWeights.Bold, dialog.Step1Label.FontWeight);
        Assert.Equal(FontWeights.Normal, dialog.Step2Label.FontWeight);
        Assert.Same(dialog.FindResource("TextForegroundBrush"), dialog.Step1Label.Foreground);
        Assert.Same(dialog.FindResource("MutedForegroundBrush"), dialog.Step3Label.Foreground);
        Assert.Equal("Next", dialog.NextButton.Label);
        Assert.False(dialog.BackButton.IsEnabled);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Step_one_fields_bind_both_ways_and_explain_problems() => WithDialogAsync(async dialog =>
    {
        var vm = dialog.ViewModel!;
        Assert.Equal(new DateTime(2026, 9, 16), dialog.PeriodStartPicker.DateTime);
        Assert.Equal("September 16-30, 2026", dialog.LabelBox.Text);
        Assert.Equal(Visibility.Collapsed, dialog.PeriodProblemText.Visibility);

        dialog.PeriodEndPicker.DateTime = new DateTime(2026, 9, 1);
        await UiThread.IdleAsync();

        Assert.Equal(new DateTime(2026, 9, 1), vm.PeriodEnd);
        Assert.Equal(Visibility.Visible, dialog.PeriodProblemText.Visibility);
        Assert.Equal("Period end can't be before period start.", dialog.PeriodProblemText.Text);
        Assert.False(dialog.NextButton.IsEnabled);

        dialog.PeriodEndPicker.DateTime = new DateTime(2026, 9, 30);
        dialog.LabelBox.Text = "Mid-September";
        await UiThread.IdleAsync();

        Assert.Equal("Mid-September", vm.Label);
        Assert.Equal(Visibility.Collapsed, dialog.PeriodProblemText.Visibility);
        Assert.True(dialog.NextButton.IsEnabled);
    });

    [Fact]
    public Task Step_two_shows_the_tree_and_its_scope() => WithDialogAsync(async dialog =>
    {
        await NextAsync(dialog);

        Assert.Equal(Visibility.Collapsed, dialog.Step1Panel.Visibility);
        Assert.Equal(Visibility.Visible, dialog.Step2Panel.Visibility);
        Assert.Equal(FontWeights.Bold, dialog.Step2Label.FontWeight);
        Assert.Equal(FontWeights.Normal, dialog.Step1Label.FontWeight);
        Assert.True(dialog.BackButton.IsEnabled);
        Assert.Same(dialog.ViewModel!.VisibleDepartments, dialog.EmployeeTree.ItemsSource);
        Assert.Equal("Whole company (all employees checked)", dialog.ScopeText.Text);

        dialog.ClearButton.Command.Execute(null);
        await UiThread.IdleAsync();
        Assert.Equal("Nothing selected -- check at least one employee to continue", dialog.ScopeText.Text);
        Assert.False(dialog.NextButton.IsEnabled);

        dialog.SearchBox.Text = "Kitchen";
        await UiThread.IdleAsync();
        Assert.Equal("Kitchen", Assert.Single(dialog.ViewModel.VisibleDepartments).Name);
    });

    [Fact]
    public Task Step_three_shows_the_review_and_save_result() => WithDialogAsync(async dialog =>
    {
        await NextAsync(dialog);
        await NextAsync(dialog);

        Assert.Equal(Visibility.Visible, dialog.Step3Panel.Visibility);
        Assert.Equal("Finish", dialog.NextButton.Label);
        Assert.Equal(Visibility.Collapsed, dialog.CalculatingText.Visibility);
        Assert.Equal(Visibility.Collapsed, dialog.CalculationErrorText.Visibility);
        Assert.Equal(Visibility.Visible, dialog.ReviewPanel.Visibility);
        Assert.Same(dialog.ViewModel!.ReviewResults, dialog.ReviewGrid.ItemsSource);
        Assert.Equal(Converters.NumberConverter.Format(6000m), dialog.TotalGrossPayText.Text);
        Assert.Equal(Converters.NumberConverter.Format(600m), dialog.TotalDeductionsText.Text);
        Assert.Equal(Converters.NumberConverter.Format(5400m), dialog.TotalNetPayText.Text);
        Assert.Equal(Visibility.Collapsed, dialog.SavedSummaryText.Visibility);

        dialog.SaveButton.Command.Execute(null);
        await UiThread.IdleAsync();

        Assert.Equal(Visibility.Visible, dialog.SavedSummaryText.Visibility);
        Assert.Equal(Visibility.Visible, dialog.SavedNoteText.Visibility);
        Assert.Equal("Saved as \"September 16-30, 2026\" (run #42).", dialog.SavedSummaryText.Text);
        Assert.False(dialog.SaveButton.IsEnabled);
    });

    [Fact]
    public Task Finish_closes_the_dialog_as_accepted() => UiThread.RunAsync(async () =>
    {
        var result = await UiThread.ShowDialogAsync(NewDialog(), async dialog =>
        {
            await NextAsync(dialog);
            await NextAsync(dialog);
            await NextAsync(dialog);
        });

        Assert.True(result);
    });
}
