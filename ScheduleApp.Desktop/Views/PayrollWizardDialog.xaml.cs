using System.Reactive.Linq;
using System.Windows;
using System.Windows.Media;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Converters;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Build-order Group C's "New Payroll Run…" wizard shell (steps 5-8): a step indicator
/// plus Back/Next/Finish over three pages -- Period + Label, Employees, and Calculate
/// &amp; Review. That third page automatically computes and shows the Employee/Net
/// Pay/Gross Pay/Deductions review grid the moment it's reached (build-order steps 8.1/8.2
/// -- see PayrollWizardViewModel.CalculateReviewAsync), and its "Save Payroll Group"
/// button does the real repository write that creates the PayrollRun (build-order step
/// 8.3 -- see PayrollWizardViewModel.SavePayrollGroupCommand).
///
/// Shown for a PayrollWizardViewModel its opener builds, so there's a fresh one -- and
/// therefore no leftover period/label/tree-selection state -- every time it opens. All
/// navigation and validation lives on that ViewModel; this view binds to it, and closes
/// itself as accepted when Next reports Finish.
/// </summary>
public partial class PayrollWizardDialog
{
    public PayrollWizardDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            // Set by whoever opened the dialog, before showing it.
            var viewModel = ViewModel!;
            ViewInteractions.Register(viewModel, this).DisposeWith(d);

            // Step indicator and content: only the current step's panel shows, and its label
            // is dark and bold while the others stay muted.
            this.OneWayBind(ViewModel, vm => vm.CurrentStepIndex, v => v.Step1Panel.Visibility, step => VisibleWhen(step == 0)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.CurrentStepIndex, v => v.Step2Panel.Visibility, step => VisibleWhen(step == 1)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.CurrentStepIndex, v => v.Step3Panel.Visibility, step => VisibleWhen(step == 2)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.CurrentStepIndex, v => v.Step1Label.FontWeight, step => WeightFor(step == 0)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.CurrentStepIndex, v => v.Step2Label.FontWeight, step => WeightFor(step == 1)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.CurrentStepIndex, v => v.Step3Label.FontWeight, step => WeightFor(step == 2)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.CurrentStepIndex, v => v.Step1Label.Foreground, step => StepBrush(step == 0)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.CurrentStepIndex, v => v.Step2Label.Foreground, step => StepBrush(step == 1)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.CurrentStepIndex, v => v.Step3Label.Foreground, step => StepBrush(step == 2)).DisposeWith(d);

            // Step 1
            this.Bind(ViewModel, vm => vm.PeriodStart, v => v.PeriodStartPicker.DateTime).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.PeriodEnd, v => v.PeriodEndPicker.DateTime).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.Label, v => v.LabelBox.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.PeriodValidationMessage, v => v.PeriodProblemText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.PeriodValidationMessage, v => v.PeriodProblemText.Visibility,
                message => VisibleWhen(message is not null)).DisposeWith(d);

            // Step 2
            this.Bind(ViewModel, vm => vm.SearchText, v => v.SearchBox.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.VisibleDepartments, v => v.EmployeeTree.ItemsSource).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.SelectionScopeText, v => v.ScopeText.Text).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.SelectAllTreeCommand, v => v.SelectAllButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ClearTreeSelectionCommand, v => v.ClearButton).DisposeWith(d);

            // Step 3 -- the calculating pulse, the error and the grid are mutually exclusive
            // (see PayrollWizardViewModel.IsReviewReady).
            this.OneWayBind(ViewModel, vm => vm.IsCalculating, v => v.CalculatingText.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.CalculationErrorMessage, v => v.CalculationErrorText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.CalculationErrorMessage, v => v.CalculationErrorText.Visibility,
                message => VisibleWhen(message is not null)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.IsReviewReady, v => v.ReviewPanel.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ReviewResults, v => v.ReviewGrid.ItemsSource).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.TotalGrossPay, v => v.TotalGrossPayText.Text, NumberConverter.Format).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.TotalDeductions, v => v.TotalDeductionsText.Text, NumberConverter.Format).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.TotalNetPay, v => v.TotalNetPayText.Text, NumberConverter.Format).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.SavePayrollGroupCommand, v => v.SaveButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.IsSaving, v => v.SavingText.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.SaveErrorMessage, v => v.SaveErrorText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.SaveErrorMessage, v => v.SaveErrorText.Visibility,
                message => VisibleWhen(message is not null)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.SavedRunSummary, v => v.SavedSummaryText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.SavedRunSummary, v => v.SavedSummaryText.Visibility,
                summary => VisibleWhen(summary is not null)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.SavedRunSummary, v => v.SavedNoteText.Visibility,
                summary => VisibleWhen(summary is not null)).DisposeWith(d);

            // Navigation -- Finish (Next on the last step) closes the dialog as accepted.
            this.BindCommand(ViewModel, vm => vm.BackCommand, v => v.BackButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.NextCommand, v => v.NextButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.NextButtonText, v => v.NextButton.Label).DisposeWith(d);
            viewModel.NextCommand
                .Where(finished => finished)
                .Subscribe(_ => DialogResult = true)
                .DisposeWith(d);

            viewModel.LoadEmployeeTreeCommand.Execute().Subscribe().DisposeWith(d);
        });
    }

    private static Visibility VisibleWhen(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private static FontWeight WeightFor(bool isCurrentStep) => isCurrentStep ? FontWeights.Bold : FontWeights.Normal;

    private Brush StepBrush(bool isCurrentStep) =>
        (Brush)FindResource(isCurrentStep ? "TextForegroundBrush" : "MutedForegroundBrush");
}
