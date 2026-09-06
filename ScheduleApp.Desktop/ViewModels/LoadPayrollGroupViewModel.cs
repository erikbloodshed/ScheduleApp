using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Data.Repositories;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Backs LoadPayrollGroupDialog's list -- build-order step 10, "Load Payroll Group…" on
/// PayrollPage. Considerably simpler than PayrollWizardViewModel/PayslipScopeViewModel:
/// there's no checkbox tree here, just IPayrollRunRepository.ListAsync's own rows (each
/// already carrying its Employees membership list -- see that method's own doc comment)
/// shown newest-first exactly as the repository returns them, with a single pick-one
/// selection rather than a multi-select scope.
///
/// Also owns deleting a saved run (DeletePayrollRunAsync) -- the dialog's only other
/// write besides the implicit "load" a pick-and-Load click represents. Not registered in
/// DI, same "constructed directly by whoever opens the dialog" convention
/// PayslipScopeViewModel/PayrollWizardViewModel already follow (see either class's own doc
/// comment) -- so there's no leftover Runs/SelectedRun state between opens, and no
/// IStatusBarService reference to thread through (that's the host page's, not this
/// dialog's -- see DeletePayrollRunAsync's own doc comment for why this uses MessageBox
/// instead, same as LoadPayrollGroupDialog.LoadButton_Click's own validation message).
/// </summary>
public partial class LoadPayrollGroupViewModel : ObservableObject
{
    private readonly IPayrollRunRepository _payrollRunRepository;

    public LoadPayrollGroupViewModel(IPayrollRunRepository payrollRunRepository)
    {
        _payrollRunRepository = payrollRunRepository;
    }

    /// <summary>Every saved run, newest first -- a straight pass-through of
    /// IPayrollRunRepository.ListAsync's own ordering (see that method's own doc comment),
    /// not re-sorted here, just wrapped one-for-one in <see cref="LoadPayrollGroupRunItem"/>
    /// so the list's own display text (EmployeeCountText's pluralization) doesn't need a
    /// converter. What LoadPayrollGroupDialog's ListBox binds to.</summary>
    public ObservableCollection<LoadPayrollGroupRunItem> Runs { get; } = new();

    [ObservableProperty]
    private LoadPayrollGroupRunItem? selectedRun;

    /// <summary>Delete button only makes sense once something's picked -- same
    /// "nothing selected, nothing to act on" gating LoadButton_Click already enforces by
    /// hand (via its own MessageBox warning) for Load; this one's a RelayCommand
    /// CanExecute instead since there's no separate button-click validation path to
    /// reuse the way Load's has.</summary>
    partial void OnSelectedRunChanged(LoadPayrollGroupRunItem? value) =>
        DeletePayrollRunCommand.NotifyCanExecuteChanged();

    [ObservableProperty]
    private bool isLoading;

    /// <summary>Shown in place of the list while it's empty and nothing's loading --
    /// distinguishes "nothing's been saved yet" from a list that's merely still loading
    /// (IsLoading covers that state on its own, via the same progress-bar pattern
    /// PayrollViewModel.IsBusy already uses elsewhere on this tab).</summary>
    public bool HasNoRuns => !IsLoading && Runs.Count == 0;

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoRuns));

        // Same "don't let a second write start while ListAsync/DeleteAsync is already
        // touching the shared ScheduleDbContext" reasoning PayrollViewModel's own
        // _busy-gated commands follow, just via this dialog's single IsLoading flag
        // instead of a shared AttendanceBusyState -- a single-purpose picker dialog has
        // no other work IsLoading would need to coordinate with.
        DeletePayrollRunCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Run once, from LoadPayrollGroupDialog's own Loaded handler -- same "load on
    /// open, not lazily" convention PayslipScopeViewModel.LoadEmployeeTreeCommand and
    /// PayrollWizardViewModel.LoadEmployeeTreeCommand both follow for their own trees. Also
    /// re-run by DeletePayrollRunAsync below after a successful delete, so the list (and
    /// SelectedRun/HasNoRuns) reflect what's actually left on disk rather than the row
    /// just being spliced out of Runs by hand.</summary>
    [RelayCommand]
    private async Task LoadRunsAsync()
    {
        IsLoading = true;
        SelectedRun = null;

        try
        {
            Runs.Clear();
            foreach (var run in await _payrollRunRepository.ListAsync())
                Runs.Add(new LoadPayrollGroupRunItem(run));
        }
        finally
        {
            // Flips HasNoRuns' own notification via OnIsLoadingChanged below -- while
            // IsLoading was still true above, HasNoRuns was already forced false
            // regardless of Runs.Count, so there's nothing worth notifying until this
            // actually lands.
            IsLoading = false;
        }
    }

    /// <summary>Bound to LoadPayrollGroupDialog's own Delete… button -- permanently removes
    /// SelectedRun via IPayrollRunRepository.DeleteAsync (see that method's own doc comment:
    /// membership rows cascade with it, but the underlying PayrollAdjustment rows for its
    /// employees/period are untouched -- this only forgets the saved grouping, not any
    /// payroll data). Same Yes/No MessageBox confirmation as
    /// PayrollViewModel.DeleteAdjustmentAsync/ManualEntryEditorViewModel.
    /// DeleteManualEntryAsync -- there's no status bar to show a caution/success message on
    /// from inside a modal dialog (see StatusBarNotificationExtensions' own doc comment for
    /// why that's a MainWindow-level concept this dialog was never given a reference to),
    /// so both the confirmation and any failure are plain MessageBox calls, matching
    /// LoadButton_Click's own validation warning right below this in the dialog.
    ///
    /// Deliberately does not close the dialog -- unlike Load (a whole-dialog confirm), this
    /// is a one-off action against a single row; the person may want to delete several, or
    /// delete one and then still pick and Load a different one, in the same visit. Reloads
    /// the whole list afterward via LoadRunsAsync (same round trip Load performs on open)
    /// rather than just removing the item from Runs by hand, so the list can't drift from
    /// what IPayrollRunRepository.DeleteAsync actually left in the database.
    ///
    /// If SelectedRun happens to be the run currently active on PayrollPage
    /// (PayrollViewModel.ActivePayrollRunId), deleting it here has no effect on that
    /// tab's already-loaded period/checklist/figures -- see ActivePayrollRunId's own doc
    /// comment: it's write-only, set once a run is saved/loaded and never read back to
    /// re-fetch anything, so there's nothing on PayrollPage for this to invalidate.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteRun))]
    private async Task DeletePayrollRunAsync()
    {
        if (SelectedRun is not { } item) return;

        var confirm = MessageBox.Show(
            $"Delete \"{item.Run.Label}\"? This can't be undone. The employees' own payroll " +
            "figures for that period aren't affected -- only this saved run record.",
            "Delete payroll group", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _payrollRunRepository.DeleteAsync(item.Run.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not delete \"{item.Run.Label}\".\n\n{ex.Message}",
                "Delete payroll group", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        await LoadRunsAsync();
    }

    /// <summary>Same !IsLoading-only guard as PayrollViewModel's own busy-gated commands
    /// follow against _busy.IsRunning, for the same reason -- LoadRunsAsync (whether from
    /// the initial Loaded handler or this method's own post-delete reload) reads through
    /// the same shared, app-lifetime-scoped ScheduleDbContext DeleteAsync itself writes
    /// through, so letting a second click fire mid-load/mid-delete would risk the same race
    /// _busy exists to prevent over on PayrollPage.</summary>
    private bool CanDeleteRun() => SelectedRun is not null && !IsLoading;
}

/// <summary>One row in LoadPayrollGroupDialog's list -- a thin display wrapper around a
/// saved PayrollRun, not a copy of its data, so nothing here can drift out of sync with
/// what LoadPayrollGroupDialog.SelectedRun eventually reads. Exists purely so
/// EmployeeCountText can apply the same "1 employee" vs "N employees" pluralization
/// PayslipScopeViewModel.SelectionScopeText/PayrollWizardViewModel.SelectionScopeText
/// already use for their own employee counts, without a value converter for what's a
/// one-line, list-item-only piece of text.</summary>
public sealed class LoadPayrollGroupRunItem(PayrollRun run)
{
    public PayrollRun Run { get; } = run;

    public string EmployeeCountText =>
        Run.Employees.Count == 1 ? "(1 employee)" : $"({Run.Employees.Count} employees)";
}
