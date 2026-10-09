using System.Reactive.Linq;
using System.Windows;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Core.Enums;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.Utilities;

namespace ScheduleApp.Desktop.Views;

/// <summary>Set/Edit Schedule -- see <see cref="ViewModels.Schedule.ApplyScheduleViewModel"/>.</summary>
public partial class ApplyScheduleDialog
{
    /// <summary>Shown for an ApplyScheduleViewModel its opener builds (see
    /// ReactiveViewModel.ShowDialog); the view locator creates it through this
    /// constructor.</summary>
    public ApplyScheduleDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            var viewModel = ViewModel!;
            ViewInteractions.Register(viewModel, this).DisposeWith(d);

            Title = viewModel.Title;
            EmployeeHeader.Text = viewModel.EmployeeHeader;
            SummaryText.Text = viewModel.SelectedDaysSummary;
            MixedScheduleNote.Visibility = VisibleWhen(viewModel.ShowsMixedScheduleNote);
            TypeCombo.ItemsSource = viewModel.ScheduleTypes;
            RestDayDutyCheckBox.IsEnabled = viewModel.CanScheduleRestDayDuty;
            RestDayDutyCheckBox.ToolTip = viewModel.RestDayDutyToolTip;
            SegmentsList.ItemsSource = viewModel.Segments;
            OvertimeEligibleCombo.ItemsSource = viewModel.OvertimeEligibleOptions;
            NightDiffEligibleCombo.ItemsSource = viewModel.NightDiffEligibleOptions;
            ApplyOvertimeRatePercentageCombo.ItemsSource = viewModel.ApplyOvertimeRateOptions;

            this.Bind(ViewModel, vm => vm.SelectedType, v => v.TypeCombo.SelectedItem,
                type => type, item => item as ScheduleType? ?? viewModel.SelectedType).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.IsRestDayDuty, v => v.RestDayDutyCheckBox.IsChecked, on => on, check => check == true)
                .DisposeWith(d);
            this.Bind(ViewModel, vm => vm.WorkTimeText, v => v.WorkTimeBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.TimeIn, v => v.TimeInPicker.SelectedTime).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.IsPaidLeave, v => v.PaidLeaveRadio.IsChecked, on => on, check => check == true).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.IsPaidLeave, v => v.UnpaidLeaveRadio.IsChecked, paid => (bool?)!paid).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.RestrictedTimeIn, v => v.RestrictedTimeInPicker.SelectedTime).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.RestrictedTimeOut, v => v.RestrictedTimeOutPicker.SelectedTime).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.OvertimeEligibleIndex, v => v.OvertimeEligibleCombo.SelectedIndex).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.NightDiffEligibleIndex, v => v.NightDiffEligibleCombo.SelectedIndex).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.ApplyOvertimeRateIndex, v => v.ApplyOvertimeRatePercentageCombo.SelectedIndex).DisposeWith(d);

            DefaultTextBox.Bind(ClockInBufferBeforeBox, viewModel.ClockInBufferBefore).DisposeWith(d);
            DefaultTextBox.Bind(ClockInBufferAfterBox, viewModel.ClockInBufferAfter).DisposeWith(d);
            DefaultTextBox.Bind(ClockOutBufferBeforeBox, viewModel.ClockOutBufferBefore).DisposeWith(d);
            DefaultTextBox.Bind(ClockOutBufferAfterBox, viewModel.ClockOutBufferAfter).DisposeWith(d);
            DefaultTextBox.Bind(OvertimeRatePercentageBox, viewModel.OvertimeRatePercentage).DisposeWith(d);
            DefaultTextBox.Bind(NightDiffRatePercentageBox, viewModel.NightDiffRatePercentage).DisposeWith(d);

            this.OneWayBind(ViewModel, vm => vm.ShowsRestDayDuty, v => v.RestDayDutyCheckBox.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsWorkTime, v => v.WorkTimeLabel.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsWorkTime, v => v.WorkTimeBox.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsTimeIn, v => v.TimeInLabel.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsTimeIn, v => v.TimeInPicker.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsLeavePay, v => v.LeavePayLabel.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsLeavePay, v => v.LeavePayPanel.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsRestrictedWindow, v => v.RestrictedTimeLabel.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsRestrictedWindow, v => v.RestrictedTimeBorder.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsSegments, v => v.SegmentsLabel.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsSegments, v => v.SegmentsBorder.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsSegments, v => v.AddSegmentButton.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsNormalBuffers, v => v.NormalBufferLabel.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsNormalBuffers, v => v.NormalBufferBorder.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsOvertimeNightDiff, v => v.OvertimeNightDiffLabel.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ShowsOvertimeNightDiff, v => v.OvertimeNightDiffBorder.Visibility, VisibleWhen).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.AddSegmentCommand, v => v.AddSegmentButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.AcceptCommand, v => v.OkButton).DisposeWith(d);
            viewModel.AcceptCommand
                .Where(accepted => accepted)
                .Subscribe(_ => DialogResult = true)
                .DisposeWith(d);
        });
    }

    private static Visibility VisibleWhen(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
}
