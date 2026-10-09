using System.ComponentModel;
using System.Windows.Input;
using NSubstitute;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

public sealed class AttendanceBusyStateTests : IDisposable
{
    private readonly IStatusBarService _statusBar = Substitute.For<IStatusBarService>();
    private readonly AttendanceBusyState _busy;

    public AttendanceBusyStateTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _busy = new AttendanceBusyState(_statusBar);
    }

    public void Dispose() => _busy.Dispose();

    [Fact]
    public async Task RunAsync_is_running_with_a_live_token_only_while_the_action_runs()
    {
        bool? runningInside = null, visiblyInside = null;
        var token = CancellationToken.None;

        await _busy.RunAsync(visibly: true, ct =>
        {
            runningInside = _busy.IsRunning;
            visiblyInside = _busy.IsVisiblyRunning;
            token = ct;
            return Task.CompletedTask;
        });

        Assert.True(runningInside);
        Assert.True(visiblyInside);
        Assert.True(token.CanBeCanceled);
        Assert.False(_busy.IsRunning);
        Assert.False(_busy.IsVisiblyRunning);
        Assert.False(_busy.Token.CanBeCanceled);
    }

    [Fact]
    public async Task A_visible_run_shows_then_clears_the_status_bar_progress()
    {
        await _busy.RunAsync(visibly: true, _ => Task.CompletedTask);

        Received.InOrder(() =>
        {
            _statusBar.ShowProgress("Working…");
            _statusBar.ClearProgress();
        });
    }

    [Fact]
    public async Task A_silent_run_leaves_the_status_bar_alone()
    {
        await _busy.RunAsync(visibly: false, _ => Task.CompletedTask);

        _statusBar.DidNotReceive().ShowProgress(Arg.Any<string>());
    }

    [Fact]
    public async Task Cancel_cancels_the_running_action_quietly()
    {
        Exception? reported = null;
        var started = new TaskCompletionSource();

        var run = _busy.RunAsync(visibly: true, async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, ex => reported = ex);

        await started.Task;
        _busy.Cancel();
        await run;

        Assert.Null(reported);
        Assert.False(_busy.IsRunning);
    }

    [Fact]
    public async Task A_failure_goes_to_onError()
    {
        Exception? reported = null;

        await _busy.RunAsync(visibly: false, _ => throw new InvalidOperationException("boom"), ex => reported = ex);

        Assert.IsType<InvalidOperationException>(reported);
        Assert.False(_busy.IsRunning);
    }

    [Fact]
    public async Task A_nested_call_rides_along_without_ending_the_outer_run()
    {
        bool? runningAfterInner = null;

        await _busy.RunAsync(visibly: false, async _ =>
        {
            await _busy.RunAsync(visibly: false, _ => Task.CompletedTask);
            runningAfterInner = _busy.IsRunning;
        });

        Assert.True(runningAfterInner);
        Assert.False(_busy.IsRunning);
    }

    [Fact]
    public async Task Cancel_command_is_enabled_only_while_visibly_running()
    {
        ICommand cancel = _busy.CancelCommand;
        bool? enabledInside = null;

        Assert.False(cancel.CanExecute(null));
        await _busy.RunAsync(visibly: true, _ =>
        {
            enabledInside = cancel.CanExecute(null);
            return Task.CompletedTask;
        });

        Assert.True(enabledInside);
        Assert.False(cancel.CanExecute(null));

        await _busy.RunAsync(visibly: false, _ =>
        {
            enabledInside = cancel.CanExecute(null);
            return Task.CompletedTask;
        });
        Assert.False(enabledInside);
    }

    [Fact]
    public async Task The_token_source_is_in_place_before_anyone_hears_IsRunning_change()
    {
        var tokenLiveWhenRaised = new List<bool>();
        ((INotifyPropertyChanged)_busy).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AttendanceBusyState.IsRunning))
                tokenLiveWhenRaised.Add(_busy.Token.CanBeCanceled);
        };

        await _busy.RunAsync(visibly: false, _ => Task.CompletedTask);

        Assert.Equal([true, false], tokenLiveWhenRaised);
    }
}
