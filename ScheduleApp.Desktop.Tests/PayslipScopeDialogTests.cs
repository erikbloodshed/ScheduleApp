using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.Views;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public class PayslipScopeDialogTests
{
    private readonly TestRoster _roster = new();

    private Task WithDialogAsync(Func<PayslipScopeDialog, Task> test) => UiThread.RunAsync(async () =>
    {
        var dialog = await UiThread.ShowAsync(new PayslipScopeDialog
        {
            ViewModel = new PayslipScopeViewModel(_roster.Provider)
            {
                Title = "Print Payslips",
                Description = "Pick who to print.",
                ConfirmText = "Print",
                ConfirmToolTip = "Print the checked employees' payslips",
                PeriodStart = new DateTime(2026, 9, 16),
                PeriodEnd = new DateTime(2026, 9, 30),
                PresetSelection = [TestRoster.Employee(1, "Alcantara")],
            },
        });
        try
        {
            await test(dialog);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task Shows_its_captions_and_the_loaded_tree() => WithDialogAsync(dialog =>
    {
        Assert.Equal("Print Payslips", dialog.Title);
        Assert.Equal("Pick who to print.", dialog.DescriptionText.Text);
        Assert.Equal("Print", dialog.ConfirmButton.Label);
        Assert.Same(dialog.ViewModel!.VisibleDepartments, dialog.EmployeeTree.ItemsSource);
        Assert.Equal(3, dialog.ViewModel.VisibleDepartments.Count);
        Assert.Equal("1 employee selected", dialog.ScopeText.Text);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Period_pickers_bind_both_ways() => WithDialogAsync(async dialog =>
    {
        var vm = dialog.ViewModel!;
        Assert.Equal(new DateTime(2026, 9, 16), dialog.PeriodStartPicker.DateTime);
        Assert.Equal(new DateTime(2026, 9, 30), dialog.PeriodEndPicker.DateTime);

        dialog.PeriodEndPicker.DateTime = new DateTime(2026, 10, 15);
        await UiThread.IdleAsync();
        Assert.Equal(new DateTime(2026, 10, 15), vm.PeriodEnd);

        vm.PeriodStart = new DateTime(2026, 10, 1);
        await UiThread.IdleAsync();
        Assert.Equal(new DateTime(2026, 10, 1), dialog.PeriodStartPicker.DateTime);
    });

    [Fact]
    public Task Search_box_filters_the_tree() => WithDialogAsync(async dialog =>
    {
        dialog.SearchBox.Text = "Kitchen";
        await UiThread.IdleAsync();

        Assert.Equal("Kitchen", dialog.ViewModel!.SearchText);
        Assert.Equal("Kitchen", Assert.Single(dialog.ViewModel.VisibleDepartments).Name);
    });

    [Fact]
    public Task Buttons_carry_their_commands_and_the_scope_text_follows() => WithDialogAsync(async dialog =>
    {
        var vm = dialog.ViewModel!;
        Assert.Same(vm.SelectAllTreeCommand, dialog.SelectAllButton.Command);
        Assert.Same(vm.ClearTreeSelectionCommand, dialog.ClearButton.Command);
        Assert.Same(vm.AcceptCommand, dialog.ConfirmButton.Command);
        Assert.True(dialog.SelectAllButton.IsEnabled);

        dialog.SelectAllButton.Command.Execute(null);
        await UiThread.IdleAsync();

        Assert.Equal("Whole company (all employees checked)", dialog.ScopeText.Text);
        Assert.False(dialog.SelectAllButton.IsEnabled);
    });
}
