using System.Reactive.Linq;
using System.Windows;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.Utilities;
using ScheduleApp.Desktop.ViewModels.Attendance;

namespace ScheduleApp.Desktop.Views;

/// <summary>One punch time for a single In/Out slot of the Day Punch Pairing editor -- see
/// <see cref="PunchTimeEntryViewModel"/>.</summary>
public partial class PunchTimeEntryDialog
{
    /// <summary>Shown for a PunchTimeEntryViewModel its opener builds (see
    /// ReactiveViewModel.ShowDialog); the view locator creates it through this
    /// constructor.</summary>
    public PunchTimeEntryDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            // Set by whoever opened the dialog, before showing it.
            var viewModel = ViewModel!;
            ViewInteractions.Register(viewModel, this).DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.Title, v => v.Title).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.SaveText, v => v.SaveButton.Label).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.EmployeeName, v => v.ContextEmployeeText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.SlotText, v => v.ContextSlotText.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.Time, v => v.TimeBox.SelectedTime).DisposeWith(d);
            DefaultTextBox.Bind(ReasonBox, viewModel.Reason).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.EnteredBy, v => v.EnteredByBox.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ErrorMessage, v => v.ErrorText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ErrorMessage, v => v.ErrorText.Visibility,
                message => message is null ? Visibility.Collapsed : Visibility.Visible).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.AcceptCommand, v => v.SaveButton).DisposeWith(d);
            viewModel.AcceptCommand
                .Where(accepted => accepted)
                .Subscribe(_ => DialogResult = true)
                .DisposeWith(d);
        });

        Loaded += (_, _) => TimeBox.FocusHour();
    }
}
