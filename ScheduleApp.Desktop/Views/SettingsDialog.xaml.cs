using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Controls;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.Utilities;
using ScheduleApp.Desktop.ViewModels;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Views;

/// <summary>Settings -- see <see cref="SettingsViewModel"/>.</summary>
public partial class SettingsDialog
{
    public SettingsDialog()
    {
        InitializeComponent();

        // The built-in logo, as the XAML loads it.
        var defaultLogo = LogoPreviewImage.Source;

        this.WhenActivated((MultipleDisposable d) =>
        {
            var viewModel = ViewModel!;
            ViewInteractions.Register(viewModel, this).DisposeWith(d);
            viewModel.ShowFieldProblem.RegisterHandler(context =>
            {
                ShowFieldProblem(context.Input);
                context.SetOutput(RxVoid.Default);
            }).DisposeWith(d);

            FilePathText.Text = viewModel.SharedConfigFileText;
            NewProfileServerBox.ItemsSource = viewModel.KnownServers;

            // Database
            ConnectionProfilesCombo.ItemsSource = viewModel.Profiles;
            this.Bind(ViewModel, vm => vm.SelectedProfile, v => v.ConnectionProfilesCombo.SelectedItem,
                profile => profile!, item => item as Services.ConnectionProfile).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.ConnectionString, v => v.ConnectionStringBox.Text).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.BeginAddProfileCommand, v => v.AddProfileButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.RemoveProfileCommand, v => v.RemoveProfileButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.CreateDatabaseCommand, v => v.CreateDatabaseButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.IsAddingProfile, v => v.AddProfilePanel.Visibility, VisibleWhen).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.NewProfileName, v => v.NewProfileNameBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.NewProfileServer, v => v.NewProfileServerBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.NewProfileDatabase, v => v.NewProfileDatabaseBox.Text).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ConfirmAddProfileCommand, v => v.ConfirmAddProfileButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.CancelAddProfileCommand, v => v.CancelAddProfileButton).DisposeWith(d);
            viewModel.WhenAnyValue(vm => vm.IsAddingProfile)
                .Where(adding => adding)
                .Subscribe(_ => NewProfileNameBox.Focus())
                .DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.AdvancedLabel, v => v.AdvancedToggleButton.Label).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ToggleAdvancedCommand, v => v.AdvancedToggleButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.IsAdvancedExpanded, v => v.AdvancedPanel.Visibility, VisibleWhen).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.CreateDedicatedLoginCommand, v => v.CreateDedicatedLoginButton).DisposeWith(d);

            // Device
            this.Bind(ViewModel, vm => vm.DeviceIp, v => v.DeviceIpBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.PortText, v => v.PortBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.CommKeyText, v => v.CommKeyBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.UseUdp, v => v.UseUdpCheckBox.IsChecked, on => on, check => check == true).DisposeWith(d);

            // Attendance
            this.Bind(ViewModel, vm => vm.DefaultWorkTimeHoursText, v => v.DefaultWorkTimeHoursBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.ClockInBufferBeforeText, v => v.ClockInBufferBeforeBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.ClockInBufferAfterText, v => v.ClockInBufferAfterBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.ClockOutBufferBeforeText, v => v.ClockOutBufferBeforeBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.ClockOutBufferAfterText, v => v.ClockOutBufferAfterBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.FlexInBufferText, v => v.FlexInBufferBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.FlexOutBufferText, v => v.FlexOutBufferBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.FlexMinBreakGapText, v => v.FlexMinBreakGapBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.GracePeriodText, v => v.GracePeriodBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.LateEarlyGraceMinutesText, v => v.LateEarlyGraceMinutesBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.NightDiffStart, v => v.NightDiffStartBox.SelectedTime).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.NightDiffEnd, v => v.NightDiffEndBox.SelectedTime).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.CapEarlyClockIn, v => v.CapEarlyClockInCheckBox.IsChecked, on => on, check => check == true)
                .DisposeWith(d);
            this.Bind(ViewModel, vm => vm.StrictOvertimeFromShiftEnd, v => v.StrictOvertimeCheckBox.IsChecked,
                on => on, check => check == true).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.UseExcelFormula, v => v.UseExcelFormulaCheckBox.IsChecked, on => on, check => check == true)
                .DisposeWith(d);

            // Payroll
            this.Bind(ViewModel, vm => vm.CompanyName, v => v.CompanyNameBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.StandardHoursPerDayText, v => v.StandardHoursPerDayBox.Text).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.OvertimeRatePercent, v => v.OvertimeRatePercentageBox.PercentValue).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.NightDiffRatePercent, v => v.NightDiffRatePercentageBox.PercentValue).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.RestDayPremiumPercent, v => v.RestDayPremiumPercentageBox.PercentValue).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.RestDayOvertimeRatePercent, v => v.RestDayOvertimeRatePercentageBox.PercentValue).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.HolidayPremiumPercent, v => v.HolidayPremiumPercentageBox.PercentValue).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.NetPayRoundingMultipleText, v => v.NetPayRoundingMultipleBox.Text).DisposeWith(d);

            // Sign-in
            this.OneWayBind(ViewModel, vm => vm.LogoPreviewPath, v => v.LogoPreviewImage.Source,
                path => path is null ? defaultLogo : AuthLogoLoader.TryLoad(path) ?? defaultLogo).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.LogoStatusText, v => v.LogoStatusText.Text).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ChooseLogoCommand, v => v.ChooseLogoButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ResetLogoCommand, v => v.ResetLogoButton).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.SaveCommand, v => v.SaveButton).DisposeWith(d);
            viewModel.SaveCommand.Where(saved => saved).Subscribe(_ => DialogResult = true).DisposeWith(d);
        });

        // The profile picker, not the connection string box: that one sits under Advanced,
        // usually collapsed, where focus would silently go nowhere.
        Loaded += (_, _) =>
        {
            RevealField(ConnectionProfilesCombo);
            ConnectionProfilesCombo.Focus();
        };
    }

    /// <summary>Points at a bad field: switches to its tab, says what's wrong, then leaves the
    /// caret in it with its text selected, so the fix is one keystroke. Focus comes after the
    /// message: dismissing it reactivates this window, which restores focus to whatever had it
    /// (the Save button), undoing an earlier Focus().</summary>
    private void ShowFieldProblem(FieldProblem problem)
    {
        var field = FieldFor(problem.Field);
        RevealField(field);
        MessageBox.Show(this, problem.Message, problem.Caption, MessageBoxButton.OK, MessageBoxImage.Warning);

        // TimeInput is a UserControl: focusing it would land on the container, not anywhere
        // to type.
        if (field is TimeInput timeInput)
            timeInput.FocusHour();
        else
            field.Focus();

        (field as TextBox)?.SelectAll();
    }

    private Control FieldFor(SettingsField field) => field switch
    {
        SettingsField.ConnectionString => ConnectionStringBox,
        SettingsField.NewProfileName => NewProfileNameBox,
        SettingsField.NewProfileServer => NewProfileServerBox,
        SettingsField.NewProfileDatabase => NewProfileDatabaseBox,
        SettingsField.Port => PortBox,
        SettingsField.CommKey => CommKeyBox,
        SettingsField.DefaultWorkTimeHours => DefaultWorkTimeHoursBox,
        SettingsField.ClockInBufferBefore => ClockInBufferBeforeBox,
        SettingsField.ClockInBufferAfter => ClockInBufferAfterBox,
        SettingsField.ClockOutBufferBefore => ClockOutBufferBeforeBox,
        SettingsField.ClockOutBufferAfter => ClockOutBufferAfterBox,
        SettingsField.FlexInBuffer => FlexInBufferBox,
        SettingsField.FlexOutBuffer => FlexOutBufferBox,
        SettingsField.FlexMinBreakGap => FlexMinBreakGapBox,
        SettingsField.GracePeriod => GracePeriodBox,
        SettingsField.LateEarlyGraceMinutes => LateEarlyGraceMinutesBox,
        SettingsField.NightDiffStart => NightDiffStartBox,
        SettingsField.NightDiffEnd => NightDiffEndBox,
        SettingsField.StandardHoursPerDay => StandardHoursPerDayBox,
        SettingsField.NetPayRoundingMultiple => NetPayRoundingMultipleBox,
        SettingsField.CompanyName => CompanyNameBox,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
    };

    /// <summary>Selects the tab <paramref name="field"/> is on and scrolls it into view. Walks
    /// the logical tree, not the visual one: an unselected tab has no visual children yet, but
    /// its content is a logical child either way -- which is also why no tab needs a
    /// name.</summary>
    private void RevealField(Control field)
    {
        for (DependencyObject? node = field; node is not null; node = LogicalTreeHelper.GetParent(node))
        {
            if (LogicalTreeHelper.GetParent(node) == SettingsTabs)
            {
                SettingsTabs.SelectedItem = node;
                break;
            }
        }

        // The tab just selected becomes a visual tree only on the next measure pass.
        SettingsTabs.UpdateLayout();
        field.BringIntoView();
    }

    private static Visibility VisibleWhen(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
}
