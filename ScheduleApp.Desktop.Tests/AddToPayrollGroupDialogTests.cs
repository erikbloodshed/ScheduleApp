using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.Views;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public class AddToPayrollGroupDialogTests
{
    private readonly TestRoster _roster = new();

    private AddToPayrollGroupDialog NewDialog() => new()
    {
        ViewModel = new AddToPayrollGroupViewModel(
            EmployeeTreeBuilder.Build([_roster.Bakery, _roster.Kitchen], _roster.Unassigned),
            [.. _roster.Kitchen.Employees]),
    };

    [Fact]
    public Task Shows_the_roster_with_the_group_checked() => UiThread.RunAsync(async () =>
    {
        var dialog = await UiThread.ShowAsync(NewDialog());
        try
        {
            var vm = dialog.ViewModel!;
            Assert.Same(vm.VisibleDepartments, dialog.EmployeeTree.ItemsSource);
            Assert.Equal("3 employees checked.", dialog.ScopeText.Text);
            Assert.All(vm.VisibleDepartments, d => Assert.True(d.IsExpanded));

            TestRoster.Node(vm.VisibleDepartments, 6).IsSelected = true;
            await UiThread.IdleAsync();
            Assert.Equal("4 employees checked.", dialog.ScopeText.Text);

            dialog.SearchBox.Text = "Bakery";
            await UiThread.IdleAsync();
            Assert.Equal("Bakery", Assert.Single(vm.VisibleDepartments).Name);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task Add_selected_closes_with_everyone_checked() => UiThread.RunAsync(async () =>
    {
        var dialog = NewDialog();

        var result = await UiThread.ShowDialogAsync(dialog, d =>
        {
            TestRoster.Node(d.ViewModel!.VisibleDepartments, 1).IsSelected = true;
            d.AddButton.Command.Execute(null);
            return Task.CompletedTask;
        });

        Assert.True(result);
        Assert.Equal([1, 3, 4, 5], dialog.ViewModel!.CheckedEmployees!.Select(e => e.Pin).Order());
    });
}
