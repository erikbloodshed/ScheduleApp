using System.Windows;
using System.Windows.Input;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Build-order step 10's "Load Payroll Group…" dialog: a plain list of every saved
/// PayrollRun (IPayrollRunRepository.ListAsync, newest first -- see
/// LoadPayrollGroupViewModel's own doc comment), pick one and click Load (or double-click
/// the row) to reopen it. Unlike PayrollWizardDialog/PayslipScopeDialog, there's no
/// department/employee tree here and nothing to resolve Pins back to actual Employee
/// objects with -- that round trip needs a live query against IScheduleRepository (an
/// employee may have been added, renamed, or deleted since this run was saved), which
/// only PayrollViewModel has a reason to hold a reference to, so it's done by
/// PayrollRunViewModel.LoadPayrollGroupAsync after this dialog returns, not here (see that
/// method's own doc comment). This dialog's only job is picking *which* saved run.
///
/// Constructed directly (`new LoadPayrollGroupDialog(...)`), not through DI -- same
/// convention every other dialog on this tab follows (see PayslipScopeDialog's own doc
/// comment for why).
/// </summary>
public partial class LoadPayrollGroupDialog : Window
{
    private readonly LoadPayrollGroupViewModel _viewModel;

    /// <summary>The run picked via Load/double-click, once ShowDialog() returns true --
    /// null if the dialog was cancelled. A plain domain PayrollRun (not
    /// LoadPayrollGroupRunItem, which exists purely for this dialog's own list-item
    /// display text -- see that class's own doc comment) since nothing outside this
    /// dialog has any reason to know that wrapper type exists.</summary>
    public PayrollRun? SelectedRun { get; private set; }

    public LoadPayrollGroupDialog(IPayrollRunRepository payrollRunRepository)
    {
        InitializeComponent();

        _viewModel = new LoadPayrollGroupViewModel(payrollRunRepository);
        DataContext = _viewModel;

        Loaded += async (_, _) => await _viewModel.LoadRunsCommand.ExecuteAsync(null);
    }

    private void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedRun is not { } item)
        {
            MessageBox.Show(
                "Select a payroll run to load.", "Choose a run", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SelectedRun = item.Run;
        DialogResult = true;
    }

    /// <summary>Same "double-click a row to confirm without reaching for the button"
    /// convenience the PayrollGroupRows/PayrollGroupGrid selection elsewhere on this tab
    /// don't need (those change a selection, not confirm a whole dialog) -- here it's
    /// worth it since picking a run and clicking Load are the entire interaction. Only
    /// fires the same validation LoadButton_Click already has, on whatever row was
    /// actually under the cursor (SelectedItem binding has already updated by the time
    /// MouseDoubleClick fires) -- a double-click on empty list space below the last row
    /// leaves SelectedRun at whatever it was, same as clicking there does.</summary>
    private void RunsListBox_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) =>
        LoadButton_Click(sender, e);
}
