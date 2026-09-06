using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.Configuration;
using ScheduleApp.Core.Configuration;
using ScheduleApp.Core.Users;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;

namespace ScheduleApp.Desktop;

/// <summary>
/// The sign-in gate used to be a separate modal LoginWindow/SetupAdminWindow shown by
/// App.OnStartup *before* MainWindow ever existed. It's now AuthOverlay -- a Grid drawn
/// on top of MainWindow's own content (see MainWindow.xaml) hosting SignInPanel or
/// SetupAdminPanel -- so the window appears immediately and the sign-in card sits over
/// it instead of in a window of its own. That moves a few things that used to happen in
/// App.OnStartup (or in MainWindow's constructor, back when the constructor only ever
/// ran after a successful sign-in) into this class instead:
///   - ShowSignInOverlay(hasAccounts), called by App.OnStartup right after resolving
///     this window and before Show(), decides which panel to display.
///   - Title and the first RootNavigationView.Navigate both used to happen unconditionally
///     (construction always meant "already signed in"); now they wait for
///     OnAuthSucceeded, since construction no longer implies that.
///   - CurrentUserContext.Set(...) used to be called by App.OnStartup right after
///     LoginWindow/SetupAdminWindow's ShowDialog() returned true; it's called from
///     OnAuthSucceeded here instead, for the same reason as Title/Navigate above.
/// </summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly IConfiguration _configuration;
    private readonly AttendanceSettings _attendanceSettings;
    private readonly PayrollSettings _payrollSettings;
    private readonly SignInSettings _signInSettings;
    private readonly SharedConfigWriter _sharedConfigWriter;
    private readonly DatabaseBackupService _backupService;
    private readonly DatabaseProvisioningService _provisioningService;
    private readonly IUserAccountRepository _userAccountRepository;
    private readonly IHolidayRepository _holidayRepository;
    private readonly AttendanceDataVersion _dataVersion;
    private readonly CurrentUserContext _currentUser;

    public MainWindow(
        IServiceProvider serviceProvider,
        IStatusBarService statusBarService,
        IConfiguration configuration,
        AttendanceSettings attendanceSettings,
        PayrollSettings payrollSettings,
        SignInSettings signInSettings,
        SharedConfigWriter sharedConfigWriter,
        DatabaseBackupService backupService,
        DatabaseProvisioningService provisioningService,
        IUserAccountRepository userAccountRepository,
        IHolidayRepository holidayRepository,
        AttendanceDataVersion dataVersion,
        CurrentUserContext currentUser,
        RememberedSignInStore rememberedSignInStore)
    {
        InitializeComponent();

        _configuration = configuration;
        _attendanceSettings = attendanceSettings;
        _payrollSettings = payrollSettings;
        _signInSettings = signInSettings;
        _sharedConfigWriter = sharedConfigWriter;
        _backupService = backupService;
        _provisioningService = provisioningService;
        _userAccountRepository = userAccountRepository;
        _holidayRepository = holidayRepository;
        _dataVersion = dataVersion;
        _currentUser = currentUser;

        // Generic until OnAuthSucceeded fills in who's signed in -- see this class's own
        // doc comment for why that can no longer happen right here in the constructor.
        Title = "Schedule Manager";

        // Lets the NavigationView resolve SchedulePage/AttendancePage (and the
        // ViewModels their constructors ask for) through DI instead of calling
        // Activator.CreateInstance on a bare parameterless constructor.
        RootNavigationView.SetServiceProvider(serviceProvider);

        statusBarService.SetStatusBarPresenter(RootStatusBarPresenter);

        SignInPanelControl.Initialize(_userAccountRepository, rememberedSignInStore);
        SignInPanelControl.SignedIn += OnAuthSucceeded;
        SignInPanelControl.ExitRequested += (_, _) => Close();
        SignInPanelControl.ApplyLogo(_signInSettings.LogoPath);

        SetupAdminPanelControl.Initialize(_userAccountRepository);
        SetupAdminPanelControl.AccountCreated += OnAuthSucceeded;
        SetupAdminPanelControl.ExitRequested += (_, _) => Close();
        SetupAdminPanelControl.ApplyLogo(_signInSettings.LogoPath);

        // Whichever panel ShowSignInOverlay made Visible before Show() was called --
        // Focus() needs the window actually loaded/rendered first, which is why this
        // waits for Loaded rather than happening inside ShowSignInOverlay itself.
        Loaded += (_, _) =>
        {
            if (SignInPanelControl.Visibility == Visibility.Visible)
                SignInPanelControl.FocusUsername();
            else if (SetupAdminPanelControl.Visibility == Visibility.Visible)
                SetupAdminPanelControl.FocusUsername();
        };
    }

    /// <summary>Called by App.OnStartup right after resolving this window and before
    /// Show() -- decides which of the two AuthOverlay panels greets whoever's at the
    /// keyboard. hasExistingAccounts is App.OnStartup's own IUserAccountRepository.AnyAsync()
    /// result, computed there rather than re-checked here since App.OnStartup already
    /// has the try/catch + Startup-error MessageBox for that call in place (see its own
    /// comments) and there's no reason to duplicate it.</summary>
    public void ShowSignInOverlay(bool hasExistingAccounts)
    {
        AuthOverlay.Visibility = Visibility.Visible;

        if (hasExistingAccounts)
        {
            SignInPanelControl.Visibility = Visibility.Visible;
            SetupAdminPanelControl.Visibility = Visibility.Collapsed;
        }
        else
        {
            SetupAdminPanelControl.Visibility = Visibility.Visible;
            SignInPanelControl.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Shared handler for SignInPanel.SignedIn and SetupAdminPanel.AccountCreated
    /// -- either one means the same thing here: an account is now authenticated, so
    /// finish the startup this window's constructor deferred (see its own doc comment)
    /// and reveal the app underneath the overlay.</summary>
    private void OnAuthSucceeded(object? sender, AuthenticatedEventArgs e)
    {
        _currentUser.Set(e.UserId, e.Username);
        Title = $"Schedule Manager -- Signed in as {_currentUser.Username}";
        AuthOverlay.Visibility = Visibility.Collapsed;
        RootNavigationView.Navigate(typeof(SchedulePage));

        // Forces the pane closed to its compact, icon-only rail -- the intended default
        // (see MainWindow.xaml's own IsPaneOpen="False"), not just left to that XAML
        // value: WPF-UI's NavigationView.IsPaneOpenProperty actually defaults to true
        // (open), and its own OnLoaded handler doesn't reliably re-sync the compact visual
        // state to a same-or-different XAML-time value either -- the pane was still
        // showing fully open on launch despite IsPaneOpen="False" sitting right there in
        // XAML. Toggling true-then-false here guarantees a real value change each way, so
        // WPF-UI's OnIsPaneOpenChanged callback (the thing that actually drives the
        // PaneOpen/PaneCompact visual state via VisualStateManager.GoToState) is
        // guaranteed to fire and land on PaneCompact, once the window has actually loaded
        // and the first navigation above has run.
        RootNavigationView.IsPaneOpen = true;
        RootNavigationView.IsPaneOpen = false;
    }

    /// <summary>Opens AttendanceFlyout when the pane is compact -- see AttendanceMenuItem's
    /// own doc comment in MainWindow.xaml for why a plain click on this item is otherwise
    /// a dead end while compact (WPF-UI's own OnClick only expands MenuItems inline while
    /// the pane is open, and this item has no TargetPageType of its own to navigate to
    /// either way). Left alone while the pane IS open -- that inline-expand already covers
    /// it, and re-opening AttendanceFlyout on top of an already-expanding pane would just
    /// be a redundant second way to reach the same three pages.</summary>
    private void AttendanceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!RootNavigationView.IsPaneOpen)
            AttendanceFlyout.IsOpen = true;
    }

    /// <summary>The three AttendanceFlyout rows (see MainWindow.xaml) -- each navigates
    /// the same way clicking the equivalent expanded-pane MenuItem would (Navigate is the
    /// same method WPF-UI's own NavigationViewItem.OnClick calls internally), then closes
    /// the flyout, since picking one is the end of this interaction, not the start of
    /// another.</summary>
    private void AttendanceFlyoutSummary_Click(object sender, RoutedEventArgs e)
    {
        AttendanceFlyout.IsOpen = false;
        RootNavigationView.Navigate(typeof(AttendanceSummaryPage));
    }

    private void AttendanceFlyoutPunchRecords_Click(object sender, RoutedEventArgs e)
    {
        AttendanceFlyout.IsOpen = false;
        RootNavigationView.Navigate(typeof(PunchRecordsPage));
    }

    private void AttendanceFlyoutManualEntries_Click(object sender, RoutedEventArgs e)
    {
        AttendanceFlyout.IsOpen = false;
        RootNavigationView.Navigate(typeof(ManualEntriesPage));
    }

    /// <summary>Opens SettingsDialog pre-filled with whatever's currently effective --
    /// connection string, attendance device defaults, attendance policy, the Net Pay
    /// rounding multiple, the sign-in logo, and the payslip company name -- whether
    /// that's coming from the shared file already overriding, or from appsettings.json
    /// (both are already merged into _configuration/_attendanceSettings/_payrollSettings/
    /// _signInSettings by the time MainWindow exists, see App.OnStartup), then hands
    /// anything the user Saved to SharedConfigWriter. AttendanceSettings/PayrollSettings
    /// rather than _configuration for the device fields/policies since
    /// AttendanceSettings.DeviceTransport/Policy and PayrollSettings.Policy/CompanyName
    /// are already the shapes SettingsDialog/SharedConfigWriter expect.</summary>
    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(
            _provisioningService,
            _configuration.GetConnectionString("ScheduleDb") ?? string.Empty,
            _attendanceSettings.DeviceIp,
            _attendanceSettings.DevicePort,
            _attendanceSettings.DeviceCommKey,
            _attendanceSettings.DeviceTransport,
            _attendanceSettings.DefaultWorkTimeHours,
            _attendanceSettings.Policy,
            _payrollSettings.Policy,
            _signInSettings.LogoPath,
            _payrollSettings.CompanyName,
            SharedConfigFile.ResolvePath())
        { Owner = this };

        // SettingsDialog itself refuses to close with DialogResult = true unless at
        // least one group actually changed (see its own SaveButton_Click), so HasChanges
        // is always true here -- this check just guards against that contract changing
        // out from under this method without a compile error to catch it.
        if (dialog.ShowDialog() != true || !dialog.HasChanges)
            return;

        // This file is machine-wide -- it's read by every account that runs Schedule
        // Manager on this machine, and by the Push Listener Windows Service if one's
        // installed, regardless of which of those started this particular change. Worth
        // a confirmation rather than a silent write, the same way a shared/kiosk machine
        // would want a heads-up before one user's edit affects everyone else's.
        var confirm = MessageBox.Show(
            "This changes settings shared by every account that runs Schedule Manager on " +
            "this machine, and by the Push Listener service if one is installed here.\n\n" +
            "Continue?",
            "Confirm shared change", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        string? savedPath;
        try
        {
            savedPath = _sharedConfigWriter.Save(
                dialog.ChangedConnectionString,
                dialog.ChangedDevice,
                dialog.ChangedDefaultWorkTimeHours,
                dialog.ChangedPolicy,
                dialog.ChangedPayrollPolicy,
                dialog.ChangedLogoPath,
                dialog.ChangedCompanyName);
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(
                "Could not save the shared config file -- access was denied.\n\n" + ex.Message +
                "\n\nThis usually means the current Windows account doesn't have write access " +
                "to " + SharedConfigFile.DefaultPath + ". Ask whoever set this machine up to " +
                "grant your account write access to that folder, or run Schedule Manager as " +
                "an administrator.",
                "Save failed -- access denied", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not save the shared config file.\n\n" + ex.Message,
                "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (savedPath is null)
            return;

        var restart = MessageBox.Show(
            $"Saved to {savedPath}.\n\n" +
            "This only takes effect the next time an app reads it at startup -- Schedule " +
            "Manager, and the Push Listener service (ScheduleAppPushListener) if one is " +
            "installed on this machine.\n\n" +
            "Restart Schedule Manager now?",
            "Saved", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (restart == MessageBoxResult.Yes && Environment.ProcessPath is { } exePath)
        {
            Process.Start(exePath);
            Application.Current.Shutdown();
        }
    }

    /// <summary>Opens BackupRestoreDialog against whatever connection string is
    /// currently effective -- same source SettingsButton_Click reads (_configuration,
    /// already merged from appsettings.json and the shared config file by the time
    /// MainWindow exists -- see App.OnStartup).</summary>
    private void BackupRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var connectionString = _configuration.GetConnectionString("ScheduleDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            MessageBox.Show(
                "No database connection string is configured -- check Settings.",
                "Not configured", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new BackupRestoreDialog(_backupService, connectionString) { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>Opens ManageUsersDialog -- see its own doc comment for what it covers
    /// and the two guard rails it enforces so this can't lock the app out of
    /// itself.</summary>
    private void ManageUsersButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ManageUsersDialog(_userAccountRepository, _currentUser) { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>Opens ManageHolidaysDialog -- see its own doc comment for what it
    /// covers.</summary>
    private void ManageHolidaysButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ManageHolidaysDialog(_holidayRepository, _dataVersion) { Owner = this };
        dialog.ShowDialog();
    }
}
