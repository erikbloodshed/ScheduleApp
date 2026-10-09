using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NSubstitute;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.Views;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public class LoadPayrollGroupDialogTests
{
    private readonly IPayrollRunRepository _repository = Substitute.For<IPayrollRunRepository>();
    private List<PayrollRun> _saved =
    [
        new() { Id = 2, Label = "Oct 1-15" },
        new() { Id = 1, Label = "Sep 16-30" },
    ];

    private LoadPayrollGroupDialog NewDialog() => new() { ViewModel = new LoadPayrollGroupViewModel(_repository) };

    public LoadPayrollGroupDialogTests() =>
        _repository.ListAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(_saved.ToList()));

    [Fact]
    public Task The_view_locator_finds_the_dialog_for_its_view_model() => UiThread.RunAsync(() =>
    {
        var vm = new LoadPayrollGroupViewModel(_repository);

        var view = ReactiveUI.Binding.ViewLocator.GetCurrent().ResolveView(vm, null);

        var dialog = Assert.IsType<LoadPayrollGroupDialog>(view);
        Assert.Same(vm, dialog.ViewModel);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Lists_the_runs_and_binds_the_selection() => UiThread.RunAsync(async () =>
    {
        var dialog = await UiThread.ShowAsync(NewDialog());
        try
        {
            var vm = dialog.ViewModel!;
            Assert.Equal(2, dialog.RunsListBox.Items.Count);
            Assert.Equal(Visibility.Collapsed, dialog.NoRunsText.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.LoadingIndicator.Visibility);
            Assert.False(dialog.DeleteButton.IsEnabled);

            dialog.RunsListBox.SelectedIndex = 1;
            await UiThread.IdleAsync();
            Assert.Same(vm.Runs[1], vm.SelectedRun);
            Assert.True(dialog.DeleteButton.IsEnabled);
            Assert.Same(vm.ChooseCommand, dialog.LoadButton.Command);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task Shows_the_empty_state_when_nothing_is_saved() => UiThread.RunAsync(async () =>
    {
        _saved = [];
        var dialog = await UiThread.ShowAsync(NewDialog());
        try
        {
            Assert.Equal(Visibility.Visible, dialog.NoRunsText.Visibility);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task Load_closes_with_the_picked_run() => UiThread.RunAsync(async () =>
    {
        var dialog = NewDialog();

        var result = await UiThread.ShowDialogAsync(dialog, d =>
        {
            d.RunsListBox.SelectedIndex = 0;
            d.LoadButton.Command.Execute(null);
            return Task.CompletedTask;
        });

        Assert.True(result);
        Assert.Equal("Oct 1-15", dialog.ViewModel!.ChosenRun?.Label);
    });

    [Fact]
    public Task Double_clicking_a_row_loads_it() => UiThread.RunAsync(async () =>
    {
        var dialog = NewDialog();

        var result = await UiThread.ShowDialogAsync(dialog, d =>
        {
            d.RunsListBox.SelectedIndex = 1;
            d.RunsListBox.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = Control.MouseDoubleClickEvent,
            });
            return Task.CompletedTask;
        });

        Assert.True(result);
        Assert.Equal("Sep 16-30", dialog.ViewModel!.ChosenRun?.Label);
    });
}
