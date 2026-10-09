using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Input;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Hosts the Day Punch Pairing editor for one employee/day (see
/// <see cref="ViewModels.Attendance.DayPunchPairingEditorViewModel"/>). Touches no repository
/// itself -- it collects an intent (the editor's Outcome) and closes; the launcher
/// (DayPunchPairingEditorLauncher) does the save or delete and the AttendanceDataVersion bump.
/// </summary>
public partial class DayPunchPairingDialog
{
    /// <summary>Shown for a DayPunchPairingEditorViewModel the launcher builds (see
    /// ReactiveViewModel.ShowDialog); the view locator creates it through this
    /// constructor.</summary>
    public DayPunchPairingDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            // Set by whoever opened the dialog, before showing it.
            var viewModel = ViewModel!;

            // Deleting a manual punch and Reset to Automatic ask first; adding or editing one
            // opens the punch-time dialog.
            ViewInteractions.Register(viewModel, this).DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.Title, v => v.Title).DisposeWith(d);
            Editor.ViewModel = viewModel;

            this.BindCommand(ViewModel, vm => vm.ResetToAutomaticCommand, v => v.ResetButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsResetToAutomatic, v => v.ResetButton.Visibility, VisibleWhen).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.SaveCommand, v => v.SaveButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsSave, v => v.SaveButton.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.CloseText, v => v.CancelButton.Label).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsSave, v => v.CancelButton.IsDefault, showsSave => !showsSave).DisposeWith(d);

            Observable.Merge(viewModel.SaveCommand, viewModel.ResetToAutomaticCommand)
                .Where(accepted => accepted)
                .Subscribe(_ => DialogResult = true)
                .DisposeWith(d);

            // Ctrl+Z on the window, not the editor control, so it works wherever focus sits.
            var undo = new KeyBinding(viewModel.UndoCommand, Key.Z, ModifierKeys.Control);
            InputBindings.Add(undo);
            Disposable.Create(() => InputBindings.Remove(undo)).DisposeWith(d);
        });
    }

    private static Visibility VisibleWhen(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
}
