using System.Windows;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Schedule;
using ScheduleApp.Desktop.Views;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public class EmployeeDialogTests
{
    private static readonly Department Bakery = new() { Id = 1, Name = "Bakery" };
    private static readonly Department Kitchen = new() { Id = 2, Name = "Kitchen" };

    private static EmployeeDialog NewDialog(Employee? existing = null) => new()
    {
        ViewModel = new EmployeeEditorViewModel([Bakery, Kitchen], 2, new PayrollPolicy(), 10, existing),
    };

    [Fact]
    public Task Fields_bind_both_ways() => UiThread.RunAsync(async () =>
    {
        var dialog = await UiThread.ShowAsync(NewDialog(new Employee
        {
            Id = 30, Pin = 3, LastName = "Cruz", FirstName = "Juan", DepartmentId = 2, DailyRate = 500m,
            QualifiesForRestDayPay = true,
        }));
        try
        {
            var vm = dialog.ViewModel!;
            Assert.Equal("Edit Employee", dialog.Title);
            Assert.Equal(3, dialog.EmployeeIdBox.Value);
            Assert.Equal("Cruz", dialog.LastNameBox.Text);
            Assert.Same(Kitchen, dialog.DepartmentCombo.SelectedItem);
            Assert.True(dialog.DepartmentCombo.IsEnabled);
            Assert.Equal("Daily rate", dialog.RateLabel.Text);
            Assert.Equal(Visibility.Visible, dialog.DailyRateBox.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.MonthlyRateBox.Visibility);
            Assert.Equal(Visibility.Visible, dialog.RestDayWorkPremiumPercentageBox.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.HolidayPremiumPercentageBox.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.PremiumPayFieldPanel.Visibility);
            Assert.Equal("30 %", dialog.RestDayWorkPremiumPercentageBox.WatermarkText);

            dialog.FirstNameBox.Text = "Pedro";
            dialog.UnassignedCheck.IsChecked = true;
            dialog.QualifiesForPremiumPayCheck.IsChecked = true;
            dialog.PayTypeMonthlyRadio.IsChecked = true;
            await UiThread.IdleAsync();

            Assert.Equal("Pedro", vm.FirstName);
            Assert.True(vm.IsUnassigned);
            Assert.False(dialog.DepartmentCombo.IsEnabled);
            Assert.True(vm.QualifiesForPremiumPay);
            Assert.Equal(Visibility.Visible, dialog.HolidayPremiumPercentageBox.Visibility);
            Assert.Equal(Visibility.Visible, dialog.PremiumPayFieldPanel.Visibility);
            Assert.True(vm.IsMonthly);
            Assert.Equal("Monthly rate", dialog.RateLabel.Text);
            Assert.Equal(Visibility.Collapsed, dialog.DailyRateBox.Visibility);
            Assert.Equal(Visibility.Visible, dialog.MonthlyRateBox.Visibility);

            vm.IsMonthly = false;
            await UiThread.IdleAsync();
            Assert.True(dialog.PayTypeDailyRadio.IsChecked);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task OK_closes_once_the_details_are_complete() => UiThread.RunAsync(async () =>
    {
        var dialog = NewDialog();

        var result = await UiThread.ShowDialogAsync(dialog, d =>
        {
            d.ViewModel!.Pin = 7;
            d.ViewModel.LastName = "Garcia";
            d.ViewModel.FirstName = "Ana";
            d.OkButton.Command.Execute(null);
            return Task.CompletedTask;
        });

        Assert.True(result);
        Assert.Equal(7, dialog.ViewModel!.AcceptedDetails?.Pin);
        Assert.Equal(2, dialog.ViewModel.AcceptedDetails?.DepartmentId);
        Assert.Equal(EmployeeType.Daily, dialog.ViewModel.AcceptedDetails?.EmployeeType);
    });
}

public class InputDialogTests
{
    [Fact]
    public Task Shows_the_prompt_and_closes_with_the_typed_text() => UiThread.RunAsync(async () =>
    {
        var dialog = new InputDialog { ViewModel = new TextPromptViewModel("New Department", "Department name:", "Bakery") };

        var result = await UiThread.ShowDialogAsync(dialog, async d =>
        {
            Assert.Equal("New Department", d.Title);
            Assert.Equal("Department name:", d.PromptText.Text);
            Assert.Equal("Bakery", d.ValueBox.Text);

            d.ValueBox.Text = " Pastry ";
            await UiThread.IdleAsync();
            d.OkButton.Command.Execute(null);
        });

        Assert.True(result);
        Assert.Equal("Pastry", dialog.ViewModel!.Value);
    });
}
