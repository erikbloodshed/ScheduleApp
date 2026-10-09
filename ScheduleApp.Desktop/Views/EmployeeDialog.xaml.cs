using System.Reactive.Linq;
using System.Windows;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.Reactive;

namespace ScheduleApp.Desktop.Views;

/// <summary>Add/Edit Employee -- see <see cref="ViewModels.Schedule.EmployeeEditorViewModel"/>.</summary>
public partial class EmployeeDialog
{
    /// <summary>Shown for an EmployeeEditorViewModel its opener builds (see
    /// ReactiveViewModel.ShowDialog); the view locator creates it through this
    /// constructor.</summary>
    public EmployeeDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            // Set by whoever opened the dialog, before showing it.
            var viewModel = ViewModel!;
            ViewInteractions.Register(viewModel, this).DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.Title, v => v.Title).DisposeWith(d);

            // Info
            this.Bind(ViewModel, vm => vm.Pin, v => v.EmployeeIdBox.Value).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.LastName, v => v.LastNameBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.FirstName, v => v.FirstNameBox.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Departments, v => v.DepartmentCombo.ItemsSource).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.Department, v => v.DepartmentCombo.SelectedItem,
                department => department!, item => item as Department).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.IsUnassigned, v => v.UnassignedCheck.IsChecked, on => on, check => check == true).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.IsUnassigned, v => v.DepartmentCombo.IsEnabled, unassigned => !unassigned).DisposeWith(d);

            // Attendance -- blank inherits the company default, shown as the placeholder.
            this.Bind(ViewModel, vm => vm.DefaultWorkTimeHours, v => v.DefaultWorkTimeHoursBox.Value).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.DefaultWorkTimePlaceholder, v => v.DefaultWorkTimeHoursBox.WatermarkText).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.ClockInBufferBeforeHours, v => v.ClockInBufferBeforeHoursBox.Value).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.ClockInBufferAfterHours, v => v.ClockInBufferAfterHoursBox.Value).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.ClockOutBufferBeforeHours, v => v.ClockOutBufferBeforeHoursBox.Value).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.ClockOutBufferAfterHours, v => v.ClockOutBufferAfterHoursBox.Value).DisposeWith(d);

            // Payroll -- eligibility
            this.Bind(ViewModel, vm => vm.QualifiesForOvertime, v => v.QualifiesForOvertimeCheck.IsChecked, on => on, check => check == true).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.ApplyOvertimeRatePercentageByDefault, v => v.ApplyOvertimeRatePercentageByDefaultCheck.IsChecked,
                on => on, check => check == true).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.QualifiesForNightDiff, v => v.QualifiesForNightDiffCheck.IsChecked, on => on, check => check == true).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.ExemptFromUndertimeDeduction, v => v.ExemptFromUndertimeDeductionCheck.IsChecked,
                on => on, check => check == true).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.QualifiesForRestDayPay, v => v.QualifiesForRestDayPayCheck.IsChecked, on => on, check => check == true).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.QualifiesForPremiumPay, v => v.QualifiesForPremiumPayCheck.IsChecked, on => on, check => check == true).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.DefaultLeaveIsPaid, v => v.DefaultLeaveIsPaidCheck.IsChecked, on => on, check => check == true).DisposeWith(d);

            // Payroll -- pay type and rates. Only the active pay type's rate box shows.
            this.Bind(ViewModel, vm => vm.IsMonthly, v => v.PayTypeMonthlyRadio.IsChecked, on => on, check => check == true).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.IsMonthly, v => v.PayTypeDailyRadio.IsChecked, monthly => (bool?)!monthly).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.RateLabel, v => v.RateLabel.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.IsMonthly, v => v.DailyRateBox.Visibility, monthly => VisibleWhen(!monthly)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.IsMonthly, v => v.MonthlyRateBox.Visibility, VisibleWhen).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.DailyRate, v => v.DailyRateBox.Value).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.MonthlyRate, v => v.MonthlyRateBox.Value).DisposeWith(d);

            // Premiums -- each shows only once its eligibility is checked; a hidden value
            // still saves.
            this.Bind(ViewModel, vm => vm.RestDayPremiumPercent, v => v.RestDayWorkPremiumPercentageBox.PercentValue).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.RestDayPremiumPlaceholder, v => v.RestDayWorkPremiumPercentageBox.WatermarkText).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.QualifiesForRestDayPay, v => v.RestDayWorkPremiumPercentageBox.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.QualifiesForRestDayPay, v => v.RestDayWorkPremiumPercentageLabel.Visibility, VisibleWhen).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.HolidayPremiumPercent, v => v.HolidayPremiumPercentageBox.PercentValue).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.HolidayPremiumPlaceholder, v => v.HolidayPremiumPercentageBox.WatermarkText).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.QualifiesForPremiumPay, v => v.HolidayPremiumPercentageBox.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.QualifiesForPremiumPay, v => v.HolidayPremiumPercentageLabel.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.QualifiesForPremiumPay, v => v.PremiumPayFieldPanel.Visibility, VisibleWhen).DisposeWith(d);

            // Statutory and pay-adjustment defaults
            this.Bind(ViewModel, vm => vm.DefaultSss, v => v.DefaultSssBox.Value).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.DefaultPhilHealth, v => v.DefaultPhilHealthBox.Value).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.DefaultPagIbig, v => v.DefaultPagIbigBox.Value).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.DefaultPremiumPay, v => v.DefaultPremiumPayBox.Value).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.DefaultAllowance, v => v.DefaultAllowanceBox.Value).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.DefaultCashAdvance, v => v.DefaultCashAdvanceBox.Value).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.AcceptCommand, v => v.OkButton).DisposeWith(d);
            viewModel.AcceptCommand
                .Where(accepted => accepted)
                .Subscribe(_ => DialogResult = true)
                .DisposeWith(d);
        });

        Loaded += (_, _) => EmployeeIdBox.Focus();
    }

    private static Visibility VisibleWhen(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
}
