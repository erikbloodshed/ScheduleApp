using System.ComponentModel;
using System.Reactive.Linq;
using System.Windows.Input;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.ViewModels;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public class LoadPayrollGroupViewModelTests
{
    private readonly IPayrollRunRepository _repository = Substitute.For<IPayrollRunRepository>();
    private readonly List<PayrollRun> _saved = [Run(2, "Oct 1-15"), Run(1, "Sep 16-30", employees: 1)];
    private readonly LoadPayrollGroupViewModel _vm;

    public LoadPayrollGroupViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _repository.ListAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(_saved.ToList()));
        _repository.DeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => { _saved.RemoveAll(r => r.Id == call.Arg<int>()); return Task.CompletedTask; });
        _vm = new LoadPayrollGroupViewModel(_repository);
    }

    private static PayrollRun Run(int id, string label, int employees = 3) => new()
    {
        Id = id,
        Label = label,
        Employees = [.. Enumerable.Range(1, employees).Select(i => new PayrollRunEmployee { EmployeeId = i })],
    };

    private static bool CanExecute(ICommand command) => command.CanExecute(null);

    [Fact]
    public async Task Lists_the_saved_runs_in_repository_order()
    {
        Assert.True(_vm.HasNoRuns);

        await _vm.LoadRunsCommand.Execute();

        Assert.Equal(["Oct 1-15", "Sep 16-30"], _vm.Runs.Select(r => r.Run.Label));
        Assert.Equal(["(3 employees)", "(1 employee)"], _vm.Runs.Select(r => r.EmployeeCountText));
        Assert.False(_vm.HasNoRuns);
        Assert.False(_vm.IsLoading);
    }

    [Fact]
    public async Task No_saved_runs_says_so_once_loaded()
    {
        _saved.Clear();
        var hasNoRunsChanges = new List<bool>();
        ((INotifyPropertyChanged)_vm).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LoadPayrollGroupViewModel.HasNoRuns))
                hasNoRunsChanges.Add(_vm.HasNoRuns);
        };

        await _vm.LoadRunsCommand.Execute();

        Assert.True(_vm.HasNoRuns);
        Assert.Contains(true, hasNoRunsChanges);
    }

    [Fact]
    public async Task Delete_needs_a_selection()
    {
        await _vm.LoadRunsCommand.Execute();
        Assert.False(CanExecute(_vm.DeletePayrollRunCommand));

        _vm.SelectedRun = _vm.Runs[0];
        Assert.True(CanExecute(_vm.DeletePayrollRunCommand));

        _vm.SelectedRun = null;
        Assert.False(CanExecute(_vm.DeletePayrollRunCommand));
    }

    [Fact]
    public async Task Delete_asks_first_then_reloads_without_a_selection()
    {
        await _vm.LoadRunsCommand.Execute();
        _vm.SelectedRun = _vm.Runs[1];
        var answer = false;
        _vm.Confirm.RegisterHandler(ctx => ctx.SetOutput(answer));

        await _vm.DeletePayrollRunCommand.Execute();
        await _repository.DidNotReceive().DeleteAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());

        answer = true;
        await _vm.DeletePayrollRunCommand.Execute();

        await _repository.Received(1).DeleteAsync(1, Arg.Any<CancellationToken>());
        Assert.Equal(["Oct 1-15"], _vm.Runs.Select(r => r.Run.Label));
        Assert.Null(_vm.SelectedRun);
    }

    [Fact]
    public async Task Choose_needs_a_selection()
    {
        await _vm.LoadRunsCommand.Execute();
        string? notice = null;
        _vm.Notify.RegisterHandler(ctx => { notice = ctx.Input.Message; ctx.SetOutput(default); });

        Assert.False(await _vm.ChooseCommand.Execute());
        Assert.Equal("Select a payroll run to load.", notice);
        Assert.Null(_vm.ChosenRun);

        _vm.SelectedRun = _vm.Runs[1];
        Assert.True(await _vm.ChooseCommand.Execute());
        Assert.Same(_saved[1], _vm.ChosenRun);
    }

    [Fact]
    public async Task A_failed_delete_is_reported()
    {
        await _vm.LoadRunsCommand.Execute();
        _vm.SelectedRun = _vm.Runs[0];
        _vm.Confirm.RegisterHandler(ctx => ctx.SetOutput(true));
        string? notice = null;
        _vm.Notify.RegisterHandler(ctx => { notice = ctx.Input.Message; ctx.SetOutput(default); });
        _repository.DeleteAsync(2, Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("locked"));

        await _vm.DeletePayrollRunCommand.Execute();

        Assert.Equal("Could not delete \"Oct 1-15\".\n\nlocked", notice);
        Assert.Equal(2, _vm.Runs.Count);
    }
}
