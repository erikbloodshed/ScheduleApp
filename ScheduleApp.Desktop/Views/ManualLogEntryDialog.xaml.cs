using System.Reactive.Linq;
using System.Windows;
using System.Windows.Input;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.Utilities;
using ScheduleApp.Desktop.ViewModels.Attendance;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Collects the details for one manual attendance entry -- see
/// <see cref="ManualLogEntryViewModel"/>, which owns the employee suggestions, the reference-only
/// machine punches and Save's validation. What's left here is keyboard and mouse handling for the
/// suggestion list.
/// </summary>
public partial class ManualLogEntryDialog
{
    /// <summary>Shown for a ManualLogEntryViewModel its opener builds (see
    /// ReactiveViewModel.ShowDialog); the view locator creates it through this
    /// constructor.</summary>
    public ManualLogEntryDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            // Set by whoever opened the dialog, before showing it.
            var viewModel = ViewModel!;
            ViewInteractions.Register(viewModel, this).DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.Title, v => v.Title).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.SaveText, v => v.SaveButton.Label).DisposeWith(d);

            this.Bind(ViewModel, vm => vm.EmployeeText, v => v.EmployeeBox.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Suggestions, v => v.EmployeeSuggestionsList.ItemsSource).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.IsSuggestionsOpen, v => v.EmployeeSuggestionsPopup.IsOpen).DisposeWith(d);
            Observable.FromEventPattern<RoutedEventHandler, RoutedEventArgs>(
                    handler => EmployeeBox.LostFocus += handler,
                    handler => EmployeeBox.LostFocus -= handler)
                .Subscribe(_ => viewModel.CommitEmployee())
                .DisposeWith(d);

            // Opened from a calendar tile, the employee and date are already decided.
            this.OneWayBind(ViewModel, vm => vm.IsEmployeeAndDateLocked, v => v.EmployeeBox.IsEnabled, locked => !locked).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.IsEmployeeAndDateLocked, v => v.DateBox.IsEnabled, locked => !locked).DisposeWith(d);

            this.Bind(ViewModel, vm => vm.Date, v => v.DateBox.DateTime).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.Time, v => v.TimeBox.SelectedTime).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.PunchType, v => v.PunchTypeCombo.SelectedIndex).DisposeWith(d);
            DefaultTextBox.Bind(ReasonBox, viewModel.Reason).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.EnteredBy, v => v.EnteredByBox.Text).DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.MachinePunches, v => v.MachinePunchesList.ItemsSource).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.MachinePunchesMessage, v => v.MachinePunchesPlaceholder.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.MachinePunchesMessage, v => v.MachinePunchesPlaceholder.Visibility,
                message => message is null ? Visibility.Collapsed : Visibility.Visible).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.AcceptCommand, v => v.SaveButton).DisposeWith(d);
            viewModel.AcceptCommand
                .Where(accepted => accepted)
                .Subscribe(_ => DialogResult = true)
                .DisposeWith(d);
        });

        Loaded += (_, _) => EmployeeBox.Focus();
    }

    /// <summary>Down/Up move the highlight through the open suggestion list; Enter picks the
    /// highlighted one (with nothing highlighted, Enter falls through to the dialog's default
    /// button); Escape closes the list without closing the dialog.</summary>
    private void EmployeeBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!EmployeeSuggestionsPopup.IsOpen)
            return;

        switch (e.Key)
        {
            case Key.Down:
                EmployeeSuggestionsList.SelectedIndex =
                    Math.Min(EmployeeSuggestionsList.SelectedIndex + 1, EmployeeSuggestionsList.Items.Count - 1);
                e.Handled = true;
                break;

            case Key.Up:
                EmployeeSuggestionsList.SelectedIndex = Math.Max(EmployeeSuggestionsList.SelectedIndex - 1, 0);
                e.Handled = true;
                break;

            case Key.Enter when EmployeeSuggestionsList.SelectedItem is string picked:
                Pick(picked);
                e.Handled = true;
                break;

            case Key.Escape:
                EmployeeSuggestionsPopup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void EmployeeSuggestionsList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (EmployeeSuggestionsList.SelectedItem is string picked)
            Pick(picked);
    }

    private void Pick(string suggestion)
    {
        ViewModel?.PickSuggestion(suggestion);
        EmployeeBox.CaretIndex = EmployeeBox.Text.Length;
        EmployeeBox.Focus();
    }
}
