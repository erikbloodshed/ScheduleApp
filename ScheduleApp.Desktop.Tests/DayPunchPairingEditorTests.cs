using System.Reactive.Linq;
using System.Windows;
using System.Windows.Input;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScheduleApp.Attendance;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;
using Xunit;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Tests;

/// <summary>A day's punches for Cruz (Pin 3): 8:00 and 12:00 pair up, the 17:00 one is left
/// over -- a Partial day to work on.</summary>
internal static class TestPairingDay
{
    public static readonly DateOnly Date = new(2026, 9, 16);
    public static readonly Employee Cruz = TestRoster.Employee(3, "Cruz");
    public static readonly AttendancePolicy Policy = new();

    public static ScheduleEntry Schedule(ScheduleType type = ScheduleType.Flexible) => new()
    {
        EmployeeId = 3,
        Date = Date,
        ScheduleType = type,
        WorkTimeHours = 8,
        TimeIn = type == ScheduleType.Flexible ? null : new TimeOnly(8, 0),
    };

    public static List<AttendanceLog> Punches() =>
    [
        Punch(1, 8, 0),
        Punch(2, 12, 0),
        Punch(3, 17, 0),
    ];

    public static AttendanceLog Punch(int id, int hour, int minute) => new()
    {
        Id = id,
        EmployeeId = 3,
        Timestamp = Date.ToDateTime(new TimeOnly(hour, minute)),
        PunchType = 0,
        Source = AttendanceLogSource.File,
    };

    public static DayPunchPairingEditorViewModel Editor(
        IManualAttendanceLogRepository manualLogs, ScheduleType type = ScheduleType.Flexible, PunchStatus? status = PunchStatus.Partial,
        DayPunchPairing? existing = null)
    {
        var schedule = Schedule(type);
        return new DayPunchPairingEditorViewModel(
            Cruz, schedule, Punches(), PunchCandidateWindows.For(schedule, Policy),
            new PunchWindow(Date.ToDateTime(TimeOnly.MinValue), Date.ToDateTime(TimeOnly.MaxValue)),
            Policy, existing, manualLogs, status);
    }
}

public class DayPunchPairingEditorViewModelTests
{
    private readonly IManualAttendanceLogRepository _manualLogs = Substitute.For<IManualAttendanceLogRepository>();
    private bool _confirmAnswer = true;
    private Notice? _notice;

    public DayPunchPairingEditorViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _manualLogs.AddAsync(Arg.Any<ManualAttendanceLog>(), Arg.Any<CancellationToken>())
            .Returns(call => { var log = call.Arg<ManualAttendanceLog>(); log.Id = 50; return Task.FromResult(log); });
    }

    private DayPunchPairingEditorViewModel NewEditor(ScheduleType type = ScheduleType.Flexible, PunchStatus? status = PunchStatus.Partial,
        Func<ReactiveViewModel, Task<bool>>? dialog = null, DayPunchPairing? existing = null)
    {
        var editor = TestPairingDay.Editor(_manualLogs, type, status, existing);
        editor.Confirm.RegisterHandler(ctx => ctx.SetOutput(_confirmAnswer));
        editor.Notify.RegisterHandler(ctx =>
        {
            _notice = ctx.Input;
            ctx.SetOutput(RxVoid.Default);
        });
        editor.ShowDialog.RegisterHandler(async ctx => ctx.SetOutput(dialog is null ? false : await dialog(ctx.Input)));
        return editor;
    }

    private static bool CanExecute(ICommand command) => command.CanExecute(null);

    [Fact]
    public void A_flexible_day_opens_on_its_time_order_pairing()
    {
        var editor = NewEditor();

        Assert.Equal($"Edit Punch Pairing — {TestPairingDay.Cruz.DisplayName}, Sep 16, 2026", editor.Title);
        Assert.True(editor.ShowsSave);
        Assert.False(editor.ShowsResetToAutomatic);
        Assert.Equal("Cancel", editor.CloseText);
        Assert.False(editor.IsReadOnly);
        Assert.Equal(1, editor.UnpairedCount);
        Assert.Equal(PunchStatus.Partial, editor.PreviewStatus);
        Assert.True(editor.Rows[^1].IsEmpty);         // the trailing spare to drag into
        Assert.False(CanExecute(editor.UndoCommand));
        Assert.False(CanExecute(editor.RemoveEmptySegmentsCommand));
    }

    [Fact]
    public async Task Moves_enable_undo_and_undo_restores_the_layout()
    {
        var editor = NewEditor();
        var orphan = editor.Rows.Single(r => r.IsIncomplete);
        var noon = editor.Rows[0].OutPunch!;

        editor.MoveCell(noon, orphan, ColumnSlot.Out);      // 8:00 alone, then 17:00 + 12:00

        Assert.True(CanExecute(editor.UndoCommand));

        await editor.UndoCommand.Execute();
        Assert.Same(noon.Punch, editor.Rows[0].OutPunch!.Punch);
        Assert.False(CanExecute(editor.UndoCommand));
    }

    [Fact]
    public async Task Add_segment_then_remove_empty_tidies_up()
    {
        var editor = NewEditor();

        await editor.AddRowCommand.Execute();
        Assert.True(CanExecute(editor.RemoveEmptySegmentsCommand));

        await editor.RemoveEmptySegmentsCommand.Execute();
        Assert.Single(editor.Rows, r => r.IsEmpty);
        Assert.False(CanExecute(editor.RemoveEmptySegmentsCommand));
    }

    [Fact]
    public async Task Save_and_reset_settle_the_outcome()
    {
        var editor = NewEditor(existing: new DayPunchPairing { EmployeeId = 3, Date = TestPairingDay.Date, Slots = [] });
        Assert.True(editor.ShowsResetToAutomatic);

        _confirmAnswer = false;
        Assert.False(await editor.ResetToAutomaticCommand.Execute());
        Assert.Equal(DayPunchPairingOutcome.Save, editor.Outcome);

        _confirmAnswer = true;
        Assert.True(await editor.ResetToAutomaticCommand.Execute());
        Assert.Equal(DayPunchPairingOutcome.ResetToAutomatic, editor.Outcome);

        Assert.True(await editor.SaveCommand.Execute());
        Assert.Equal(DayPunchPairingOutcome.Save, editor.Outcome);
    }

    [Fact]
    public void A_complete_non_flexible_day_is_a_viewer()
    {
        var editor = NewEditor(ScheduleType.Normal, PunchStatus.Complete);

        Assert.True(editor.IsReadOnly);
        Assert.False(editor.ShowsSave);
        Assert.Equal("Close", editor.CloseText);
        Assert.StartsWith("Punches — ", editor.Title);
    }

    [Fact]
    public async Task Adding_a_manual_punch_fills_the_slot_and_saves_it_at_once()
    {
        PunchTimeEntryViewModel? asked = null;
        var editor = NewEditor(dialog: async dialog =>
        {
            asked = Assert.IsType<PunchTimeEntryViewModel>(dialog);
            asked.Time = new TimeOnly(13, 0);
            return await asked.AcceptCommand.Execute();
        });
        var target = editor.Rows[^1];

        await editor.AddManualPunchCommand.Execute((target, ColumnSlot.In));

        Assert.Equal("Time In · Wednesday, September 16, 2026", asked!.SlotText);
        await _manualLogs.Received(1).AddAsync(
            Arg.Is<ManualAttendanceLog>(l => l.EmployeeId == 3 && l.Timestamp == TestPairingDay.Date.ToDateTime(new TimeOnly(13, 0)) && l.PunchType == 0),
            Arg.Any<CancellationToken>());
        Assert.True(target.InPunch!.IsManual);
        Assert.True(editor.ManualPunchesChanged);
        Assert.False(CanExecute(editor.UndoCommand));     // a write Undo can't reverse clears history
    }

    [Fact]
    public async Task A_failed_punch_write_says_why()
    {
        _manualLogs.AddAsync(Arg.Any<ManualAttendanceLog>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("locked"));
        var editor = NewEditor(dialog: async dialog =>
        {
            var entry = (PunchTimeEntryViewModel)dialog;
            entry.Time = new TimeOnly(13, 0);
            return await entry.AcceptCommand.Execute();
        });

        ((ICommand)editor.AddManualPunchCommand).Execute((editor.Rows[^1], ColumnSlot.In));
        await Until.TrueAsync(() => _notice is not null, "the failure notice");

        Assert.Equal("Couldn't save the punch: locked", _notice!.Message);
        Assert.False(editor.ManualPunchesChanged);
    }
}

public sealed class DayPunchPairingEditorLauncherTests
{
    private readonly IScheduleRepository _schedules = Substitute.For<IScheduleRepository>();
    private readonly IAttendanceLogRepository _logs = Substitute.For<IAttendanceLogRepository>();
    private readonly IManualAttendanceLogRepository _manualLogs = Substitute.For<IManualAttendanceLogRepository>();
    private readonly IDayPunchPairingRepository _pairings = Substitute.For<IDayPunchPairingRepository>();
    private readonly IStatusBarService _statusBar = Substitute.For<IStatusBarService>();
    private readonly AttendanceDataVersion _dataVersion = new();
    private readonly DayPunchPairingEditorLauncher _launcher;
    private readonly TestHost _host = new();

    /// <summary>Stands in for the ViewModel whose ShowDialog the launcher goes through.</summary>
    private sealed class TestHost : ReactiveViewModel;

    public DayPunchPairingEditorLauncherTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _schedules.GetScheduleEntriesForPeriodAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<ScheduleEntry> { TestPairingDay.Schedule() }));
        _logs.GetLogsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(TestPairingDay.Punches()));
        _manualLogs.GetLogsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<ManualAttendanceLog>()));
        _launcher = new DayPunchPairingEditorLauncher(_schedules, _logs, _manualLogs, _pairings,
            new AttendanceSettings(), _dataVersion, _statusBar);
    }

    private void Answer(Func<DayPunchPairingEditorViewModel, Task<bool>> dialog) =>
        _host.ShowDialog.RegisterHandler(async ctx => ctx.SetOutput(await dialog((DayPunchPairingEditorViewModel)ctx.Input)));

    [Fact]
    public async Task Saving_a_flexible_day_persists_its_pairing()
    {
        Answer(async editor => await editor.SaveCommand.Execute());

        var saved = await _launcher.OpenAsync(TestPairingDay.Cruz, TestPairingDay.Date, PunchStatus.Partial, _host.ShowDialog);

        Assert.True(saved);
        await _pairings.Received(1).SaveAsync(Arg.Is<DayPunchPairing>(p => p.EmployeeId == 3 && p.Slots.Count == 3), Arg.Any<CancellationToken>());
        Assert.Equal(1, _dataVersion.PairingVersion);
    }

    [Fact]
    public async Task Cancelling_saves_nothing()
    {
        Answer(_ => Task.FromResult(false));

        var saved = await _launcher.OpenAsync(TestPairingDay.Cruz, TestPairingDay.Date, PunchStatus.Partial, _host.ShowDialog);

        Assert.False(saved);
        await _pairings.DidNotReceiveWithAnyArgs().SaveAsync(default!, default);
        Assert.Equal(0, _dataVersion.PairingVersion);
    }

    [Fact]
    public async Task Reset_to_automatic_deletes_the_saved_pairing()
    {
        _pairings.GetAsync(3, TestPairingDay.Date, Arg.Any<CancellationToken>())
            .Returns(new DayPunchPairing { EmployeeId = 3, Date = TestPairingDay.Date, Slots = [] });
        Answer(async editor =>
        {
            editor.Confirm.RegisterHandler(ctx => ctx.SetOutput(true));
            return await editor.ResetToAutomaticCommand.Execute();
        });

        await _launcher.OpenAsync(TestPairingDay.Cruz, TestPairingDay.Date, PunchStatus.Partial, _host.ShowDialog);

        await _pairings.Received(1).DeleteAsync(3, TestPairingDay.Date, Arg.Any<CancellationToken>());
        Assert.Equal(1, _dataVersion.PairingVersion);
    }

    [Fact]
    public async Task A_day_with_no_schedule_opens_nothing()
    {
        _schedules.GetScheduleEntriesForPeriodAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<IReadOnlyCollection<int>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new List<ScheduleEntry>()));
        var opened = false;
        Answer(_ => { opened = true; return Task.FromResult(false); });

        Assert.False(await _launcher.OpenAsync(TestPairingDay.Cruz, TestPairingDay.Date, PunchStatus.Partial, _host.ShowDialog));
        Assert.False(opened);
        _statusBar.Received().Show("No schedule", Arg.Any<string>(), StatusKind.Caution, Arg.Any<TimeSpan>());
    }
}

public class DayPunchPairingDialogTests
{
    private readonly IManualAttendanceLogRepository _manualLogs = Substitute.For<IManualAttendanceLogRepository>();

    [Fact]
    public Task Shows_the_grid_footer_and_buttons_for_a_flexible_day() => UiThread.RunAsync(async () =>
    {
        var dialog = await UiThread.ShowAsync(new DayPunchPairingDialog { ViewModel = TestPairingDay.Editor(_manualLogs) });
        try
        {
            var editor = dialog.Editor;
            Assert.StartsWith("Edit Punch Pairing", dialog.Title);
            Assert.Same(dialog.ViewModel, editor.ViewModel);
            Assert.Same(dialog.ViewModel!.Rows, editor.RowsList.ItemsSource);
            Assert.Equal(Visibility.Visible, editor.Toolbar.Visibility);
            Assert.Equal(Visibility.Visible, editor.PreviewFooter.Visibility);
            Assert.Equal(Visibility.Collapsed, editor.PunchCountFooter.Visibility);
            Assert.Equal(dialog.ViewModel.PreviewStatusText, editor.PreviewStatusText.Text);
            Assert.Equal("1 punch still unpaired", editor.UnpairedText.Text);
            Assert.Equal(Visibility.Visible, dialog.SaveButton.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.ResetButton.Visibility);
            Assert.Equal("Cancel", dialog.CancelButton.Label);
            Assert.Contains(dialog.InputBindings.OfType<KeyBinding>(), b => b.Key == Key.Z && ReferenceEquals(b.Command, dialog.ViewModel.UndoCommand));
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task A_viewer_hides_editing_and_closes_with_close() => UiThread.RunAsync(async () =>
    {
        var dialog = await UiThread.ShowAsync(new DayPunchPairingDialog
        {
            ViewModel = TestPairingDay.Editor(_manualLogs, ScheduleType.Normal, PunchStatus.Complete),
        });
        try
        {
            Assert.Equal(Visibility.Collapsed, dialog.Editor.Toolbar.Visibility);
            Assert.Equal(Visibility.Visible, dialog.Editor.PunchCountFooter.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.SaveButton.Visibility);
            Assert.Equal("Close", dialog.CancelButton.Label);
            Assert.True(dialog.CancelButton.IsDefault);
        }
        finally
        {
            dialog.Close();
        }
    });

    [Fact]
    public Task Save_closes_as_accepted() => UiThread.RunAsync(async () =>
    {
        var dialog = new DayPunchPairingDialog { ViewModel = TestPairingDay.Editor(_manualLogs) };

        var result = await UiThread.ShowDialogAsync(dialog, d =>
        {
            d.SaveButton.Command.Execute(null);
            return Task.CompletedTask;
        });

        Assert.True(result);
    });
}
