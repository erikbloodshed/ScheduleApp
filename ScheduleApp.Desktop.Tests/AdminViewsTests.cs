using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Core.Users;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Accounts;
using ScheduleApp.Desktop.Views;
using Xunit;

namespace ScheduleApp.Desktop.Tests;

/// <summary>The account, backup, database-setup and settings windows, and the main window's
/// sign-in gate, shown.</summary>
public class AdminViewsTests
{
    private static async Task WithShownAsync<TWindow>(TWindow window, Func<TWindow, Task> test) where TWindow : Window
    {
        await UiThread.ShowAsync(window);
        try
        {
            await test(window);
        }
        finally
        {
            window.Close();
        }
    }

    [Fact]
    public Task Add_user_pushes_passwords_and_shows_whats_wrong() => UiThread.RunAsync(() =>
        WithShownAsync(new AddUserDialog { ViewModel = new AddUserViewModel(["maria"]) }, async dialog =>
        {
            var vm = dialog.ViewModel!;
            Assert.Equal(Visibility.Collapsed, dialog.ErrorText.Visibility);

            dialog.UsernameBox.Text = "jose";
            dialog.PasswordBoxControl.Password = "long enough";
            dialog.ConfirmPasswordBoxControl.Password = "long enuff";
            await UiThread.IdleAsync();
            Assert.Equal(("jose", "long enough", "long enuff"), (vm.Username, vm.Password, vm.ConfirmPassword));

            dialog.OkButton.Command.Execute(null);
            await UiThread.IdleAsync();
            Assert.Equal(Visibility.Visible, dialog.ErrorText.Visibility);
            Assert.Equal("Password and confirmation don't match.", dialog.ErrorText.Text);
            Assert.Empty(dialog.ConfirmPasswordBoxControl.Password);
            Assert.True(dialog.IsVisible);
        }));

    [Fact]
    public Task Reset_password_closes_once_accepted() => UiThread.RunAsync(async () =>
    {
        var dialog = new ResetPasswordDialog { ViewModel = new ResetPasswordViewModel("maria") };

        var result = await UiThread.ShowDialogAsync(dialog, async d =>
        {
            Assert.Equal("New password for \"maria\"", d.TargetUsernameText.Text);
            d.PasswordBoxControl.Password = "brand new pass";
            d.ConfirmPasswordBoxControl.Password = "brand new pass";
            await UiThread.IdleAsync();
            d.OkButton.Command.Execute(null);
        });

        Assert.True(result);
        Assert.Equal("brand new pass", dialog.ViewModel!.Password);
    });

    [Fact]
    public Task Manage_users_lists_accounts_and_follows_the_selection() => UiThread.RunAsync(() =>
    {
        var repository = Substitute.For<IUserAccountRepository>();
        repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new List<UserAccount>
        {
            new() { Id = 1, Username = "maria", IsActive = true },
            new() { Id = 2, Username = "old", IsActive = false },
        }));
        var currentUser = new CurrentUserContext();
        currentUser.Set(1, "maria");

        return WithShownAsync(new ManageUsersDialog { ViewModel = new ManageUsersViewModel(repository, currentUser) }, async dialog =>
        {
            await Until.TrueAsync(() => dialog.ViewModel!.Rows.Count == 2, "the accounts");
            await UiThread.IdleAsync();
            Assert.Same(dialog.ViewModel!.Rows, dialog.UsersGrid.ItemsSource);
            Assert.False(dialog.DeleteButton.Command.CanExecute(null));

            dialog.UsersGrid.SelectedItem = dialog.ViewModel.Rows[1];
            await UiThread.IdleAsync();
            Assert.Same(dialog.ViewModel.Rows[1], dialog.ViewModel.SelectedRow);
            Assert.Equal("Activate", dialog.ToggleActiveButton.Label);
            Assert.True(dialog.DeleteButton.Command.CanExecute(null));
        });
    });

    [Fact]
    public Task Backup_restore_shows_progress_while_one_runs() => UiThread.RunAsync(() =>
    {
        var backup = Substitute.For<IDatabaseBackupService>();
        var gate = new TaskCompletionSource();
        backup.BackupAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(gate.Task);
        var vm = new BackupRestoreViewModel(backup, "Server=.;Database=db");

        return WithShownAsync(new BackupRestoreDialog { ViewModel = vm }, async dialog =>
        {
            // After the dialog's own handlers, so these answer first.
            vm.PickFileToSave.RegisterHandler(ctx => ctx.SetOutput(@"D:\db.bak"));
            vm.Notify.RegisterHandler(ctx => ctx.SetOutput(ReactiveUI.Primitives.RxVoid.Default));
            Assert.Equal(Visibility.Collapsed, dialog.ProgressPanel.Visibility);

            dialog.BackupButton.Command.Execute(null);
            await UiThread.IdleAsync();
            Assert.Equal(Visibility.Visible, dialog.ProgressPanel.Visibility);
            Assert.Equal("Backing up…", dialog.ProgressText.Text);
            Assert.False(dialog.RestoreButton.Command.CanExecute(null));

            gate.SetResult();
            await Until.TrueAsync(() => !vm.IsBusy, "the backup");
            await UiThread.IdleAsync();
            Assert.Equal(Visibility.Collapsed, dialog.ProgressPanel.Visibility);
        });
    });

    [Fact]
    public Task Database_setup_shows_the_sections_its_mode_needs() => UiThread.RunAsync(async () =>
    {
        var provisioning = Substitute.For<IDatabaseProvisioningService>();

        await WithShownAsync(new DatabaseSetupDialog
        {
            ViewModel = new DatabaseSetupViewModel(provisioning, "Server=.;Database=ScheduleAppDb", knownServers: [@".\SQLEXPRESS"]),
        }, async dialog =>
        {
            var vm = dialog.ViewModel!;
            Assert.Equal("Database Setup", dialog.Title);
            Assert.Equal(".", dialog.ServerBox.Text);
            Assert.Equal("ScheduleAppDb", dialog.DatabaseNameBox.Text);
            Assert.Equal(Visibility.Visible, dialog.AdminConnectionPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.AdminCredentialsPanel.Visibility);

            dialog.AdminSqlAuthRadio.IsChecked = true;
            await UiThread.IdleAsync();
            Assert.False(vm.UseWindowsAuthForAdmin);
            Assert.Equal(Visibility.Visible, dialog.AdminCredentialsPanel.Visibility);

            dialog.AdminPasswordBox.Password = "admin-pass";
            dialog.ReadWriteRadio.IsChecked = true;
            await UiThread.IdleAsync();
            Assert.Equal("admin-pass", vm.AdminPassword);
            Assert.False(vm.GrantsFullAccess);

            dialog.CreateButton.Command.Execute(null);
            await UiThread.IdleAsync();
            Assert.Equal(Visibility.Visible, dialog.ErrorText.Visibility);
            Assert.Equal("Enter the admin username and password, or switch to Windows (integrated).", dialog.ErrorText.Text);
        });

        var firstRun = new DatabaseSetupDialog
        {
            ViewModel = new DatabaseSetupViewModel(provisioning, null, DatabaseSetupMode.WindowsAuthOnly, isRequiredFirstRun: true),
        };
        var result = await UiThread.ShowDialogAsync(firstRun, async d =>
        {
            Assert.Equal("Database Setup (Required)", d.Title);
            Assert.Equal("Exit", d.CancelButton.Label);
            Assert.Equal(Visibility.Collapsed, d.AdminConnectionPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, d.LoginProvisioningPanel.Visibility);
            d.ServerBox.Text = "box";
            d.DatabaseNameBox.Text = "db";
            await UiThread.IdleAsync();
            d.CreateButton.Command.Execute(null);
        });
        Assert.True(result);
        Assert.Contains("Initial Catalog=db", firstRun.ViewModel!.ConnectionString, StringComparison.Ordinal);
    });

    private static SettingsViewModel NewSettings() => new(
        Substitute.For<IDatabaseProvisioningService>(),
        new CurrentSettings(TestShell.ConnectionString, [], "192.168.1.201", 4370, 0, "Tcp", 10, new AttendancePolicy(), new PayrollPolicy(),
            null, "Panaderia", @"C:\ProgramData\ScheduleApp\shared.appsettings.json"),
        _ => false);

    [Fact]
    public Task Settings_binds_its_fields() => UiThread.RunAsync(() =>
        WithShownAsync(new SettingsDialog { ViewModel = NewSettings() }, async dialog =>
        {
            var vm = dialog.ViewModel!;
            Assert.Equal(@"File: C:\ProgramData\ScheduleApp\shared.appsettings.json", dialog.FilePathText.Text);
            Assert.Same(vm.SelectedProfile, dialog.ConnectionProfilesCombo.SelectedItem);
            Assert.Equal(Visibility.Collapsed, dialog.AdvancedPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, dialog.AddProfilePanel.Visibility);
            Assert.Equal("4370", dialog.PortBox.Text);
            Assert.Equal("Panaderia", dialog.CompanyNameBox.Text);
            Assert.Equal(30d, dialog.RestDayPremiumPercentageBox.PercentValue!.Value, 6);
            Assert.Equal("Using the default logo.", dialog.LogoStatusText.Text);

            dialog.AdvancedToggleButton.Command.Execute(null);
            dialog.AddProfileButton.Command.Execute(null);
            dialog.PortBox.Text = "4371";
            dialog.UseUdpCheckBox.IsChecked = true;
            dialog.NightDiffStartBox.SelectedTime = new TimeOnly(21, 0);
            await UiThread.IdleAsync();

            Assert.Equal(Visibility.Visible, dialog.AdvancedPanel.Visibility);
            Assert.Equal("Advanced ▴", dialog.AdvancedToggleButton.Label);
            Assert.Equal(Visibility.Visible, dialog.AddProfilePanel.Visibility);
            Assert.Equal("4371", vm.PortText);
            Assert.True(vm.UseUdp);
            Assert.Equal(new TimeOnly(21, 0), vm.NightDiffStart);
        }));

    [Fact]
    public Task Settings_points_at_a_bad_field_on_its_own_tab() => UiThread.RunAsync(async () =>
    {
        var dialog = new SettingsDialog { ViewModel = NewSettings() };

        // The field's tab is selected before the message shows; the message box then waits
        // for the person, so answer it from here by closing the window with it.
        TabItem? selectedWhenShown = null;
        await UiThread.ShowDialogAsync(dialog, async d =>
        {
            d.StandardHoursPerDayBox.Text = "0";
            await UiThread.IdleAsync();
            d.ViewModel!.ShowFieldProblem.RegisterHandler(ctx =>
            {
                selectedWhenShown = d.SettingsTabs.SelectedItem as TabItem;
                ctx.SetOutput(ReactiveUI.Primitives.RxVoid.Default);
            });
            d.SaveButton.Command.Execute(null);
            await UiThread.IdleAsync();
        });

        Assert.NotNull(selectedWhenShown);
    });

    [Fact]
    public Task The_main_window_hides_the_sign_in_card_once_someone_signs_in() => UiThread.RunAsync(async () =>
    {
        using var schedule = new TestSchedule();
        var services = new ServiceCollection()
            .AddSingleton(_ => new SchedulePage(schedule.ViewModel))
            .BuildServiceProvider();
        var shell = new TestShell();

        var window = new MainWindow(services, Substitute.For<IStatusBarService>(), shell.ViewModel);
        window.ShowSignInOverlay(hasExistingAccounts: true);
        await WithShownAsync(window, async w =>
        {
            Assert.Equal("Schedule Manager", w.Title);
            Assert.Equal(Visibility.Visible, w.AuthOverlay.Visibility);
            Assert.Equal(Visibility.Visible, w.SignInPanelControl.Visibility);
            Assert.Equal(Visibility.Collapsed, w.SetupAdminPanelControl.Visibility);
            Assert.Equal(Visibility.Collapsed, w.PinPaneButton.Visibility);

            w.SignInPanelControl.UsernameBox.Text = "maria";
            w.SignInPanelControl.PasswordBoxControl.Password = "correct horse";
            await UiThread.IdleAsync();
            w.SignInPanelControl.SignInButton.Command.Execute(null);
            await Until.TrueAsync(() => shell.ViewModel.Stage == ShellStage.SignedIn, "the sign-in");
            await UiThread.IdleAsync();

            Assert.Equal("Schedule Manager -- Signed in as maria", w.Title);
            Assert.Equal(Visibility.Collapsed, w.AuthOverlay.Visibility);
            Assert.Equal(Visibility.Visible, w.PinPaneButton.Visibility);
            Assert.IsType<SchedulePage>(w.ContentFrame.Content);

            w.PinPaneButton.Command.Execute(null);
            await UiThread.IdleAsync();
            Assert.Equal("\uE841", w.PinPaneGlyph.Text);
            Assert.True(w.NavigationDrawer.IsOpen);
            shell.DrawerState.Received(1).SavePinned(true);
        });
    });
}
