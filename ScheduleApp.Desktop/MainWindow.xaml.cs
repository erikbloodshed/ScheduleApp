using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ScheduleApp.Core.Configuration;
using ScheduleApp.Core.Users;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;
using Syncfusion.SfSkinManager;
using Syncfusion.UI.Xaml.NavigationDrawer;

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
///   - Title and the first NavigateTo both used to happen unconditionally
///     (construction always meant "already signed in"); now they wait for
///     OnAuthSucceeded, since construction no longer implies that.
///   - CurrentUserContext.Set(...) used to be called by App.OnStartup right after
///     LoginWindow/SetupAdminWindow's ShowDialog() returned true; it's called from
///     OnAuthSucceeded here instead, for the same reason as Title/Navigate above.
/// </summary>
public partial class MainWindow : Controls.AppWindow
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly AttendanceSettings _attendanceSettings;
    private readonly PayrollSettings _payrollSettings;
    private readonly SignInSettings _signInSettings;
    private readonly ConnectionProfilesSettings _connectionProfilesSettings;
    private readonly SharedConfigWriter _sharedConfigWriter;
    private readonly DatabaseBackupService _backupService;
    private readonly DatabaseProvisioningService _provisioningService;
    private readonly IUserAccountRepository _userAccountRepository;
    private readonly IHolidayRepository _holidayRepository;
    private readonly AttendanceDataVersion _dataVersion;
    private readonly CurrentUserContext _currentUser;
    private readonly NavigationDrawerStateStore _navDrawerStateStore;

    /// <summary>Whether the navigation drawer is pinned open. Loaded from
    /// <see cref="_navDrawerStateStore"/> in the constructor and applied to the drawer in
    /// <see cref="OnAuthSucceeded"/> (same "wait for sign-in" timing as the first
    /// NavigateTo/Title); toggled by <see cref="PinPaneButton_Click"/>. See
    /// <see cref="ApplyNavDrawerPinned"/> for what pinned and unpinned look like.</summary>
    private bool _navDrawerPinned;

    /// <summary>The page on screen, told when it's left -- see <see cref="NavigateTo"/>.</summary>
    private INavigationAware? _currentPage;

    public MainWindow(
        IServiceProvider serviceProvider,
        IStatusBarService statusBarService,
        IConfiguration configuration,
        AttendanceSettings attendanceSettings,
        PayrollSettings payrollSettings,
        SignInSettings signInSettings,
        ConnectionProfilesSettings connectionProfilesSettings,
        SharedConfigWriter sharedConfigWriter,
        DatabaseBackupService backupService,
        DatabaseProvisioningService provisioningService,
        IUserAccountRepository userAccountRepository,
        IHolidayRepository holidayRepository,
        AttendanceDataVersion dataVersion,
        CurrentUserContext currentUser,
        RememberedSignInStore rememberedSignInStore,
        NavigationDrawerStateStore navigationDrawerStateStore)
    {
        InitializeComponent();

        _configuration = configuration;
        _attendanceSettings = attendanceSettings;
        _payrollSettings = payrollSettings;
        _signInSettings = signInSettings;
        _connectionProfilesSettings = connectionProfilesSettings;
        _sharedConfigWriter = sharedConfigWriter;
        _backupService = backupService;
        _provisioningService = provisioningService;
        _userAccountRepository = userAccountRepository;
        _holidayRepository = holidayRepository;
        _dataVersion = dataVersion;
        _currentUser = currentUser;
        _navDrawerStateStore = navigationDrawerStateStore;

        // Read now, applied to the pane in OnAuthSucceeded (see below) alongside the
        // rest of the deferred startup.
        _navDrawerPinned = navigationDrawerStateStore.LoadPinned();

        // Generic until OnAuthSucceeded fills in who's signed in -- see this class's own
        // doc comment for why that can no longer happen right here in the constructor.
        Title = "Schedule Manager";

        // NavigateTo resolves SchedulePage/AttendanceSummaryPage/... (and the ViewModels
        // their constructors ask for) through DI rather than constructing them itself.
        _serviceProvider = serviceProvider;

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

        // The overlay covers the window's content but not its title bar, where the pin sits;
        // nothing is there to pin until someone has signed in.
        PinPaneButton.Visibility = Visibility.Collapsed;

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
        PinPaneButton.Visibility = Visibility.Visible;
        ApplyNavDrawerPinned();
        NavigateTo(ScheduleNavItem);
    }

    /// <summary>Toggles the navigation drawer's pinned-open state and remembers it for
    /// next launch.</summary>
    private void PinPaneButton_Click(object sender, RoutedEventArgs e)
    {
        _navDrawerPinned = !_navDrawerPinned;
        _navDrawerStateStore.SavePinned(_navDrawerPinned);
        ApplyNavDrawerPinned();
    }

    /// <summary>Pinned: the drawer is Expanded, the full labelled menu always open beside the
    /// page, and its own toggle is hidden since the pin owns that job. Unpinned: it's Compact,
    /// an icon rail whose toggle expands the menu over the page until a page is picked (see
    /// <see cref="NavigationDrawer_ItemClicked"/>), with Attendance's three pages in a popup
    /// off the rail. The pin glyph is filled while pinned, and its tooltip says what a click
    /// will do.</summary>
    private void ApplyNavDrawerPinned()
    {
        NavigationDrawer.DisplayMode = _navDrawerPinned ? DisplayMode.Expanded : DisplayMode.Compact;
        NavigationDrawer.IsToggleButtonVisible = !_navDrawerPinned;
        NavigationDrawer.IsOpen = _navDrawerPinned;

        PinPaneGlyph.Text = _navDrawerPinned ? "" : ""; // PinFill : Pin
        PinPaneButton.ToolTip = _navDrawerPinned
            ? "Unpin the navigation pane (fold it back to icons)"
            : "Pin the navigation pane open";
    }

    /// <summary>A page item opens its page (its Tag is the page's type); a footer item opens
    /// its dialog (its Tag names it). Attendance itself has no Tag -- the drawer expands its
    /// sub-items, or pops them up off the compact rail, on its own. Unpinned, picking a page
    /// folds the expanded menu back to the rail, so it's out of the way of the page.</summary>
    private void NavigationDrawer_ItemClicked(object? sender, NavigationItemClickedEventArgs e)
    {
        switch (e.Item?.Tag)
        {
            case Type:
                NavigateTo(e.Item);
                if (!_navDrawerPinned)
                    NavigationDrawer.IsOpen = false;
                break;
            case string dialog:
                OpenDialog(dialog);
                break;
        }
    }

    /// <summary>Shows <paramref name="item"/>'s page (its Tag) and marks the item selected,
    /// which the drawer doesn't do on its own for a navigation that didn't come from a
    /// click (the first page after sign-in). The page comes from DI each time -- see
    /// App.xaml.cs's page registrations for why that hands back the same instance -- and the
    /// page being left, then the new one, are told (<see cref="INavigationAware"/>) once the
    /// Frame has switched (<see cref="ContentFrame_Navigated"/>).</summary>
    private void NavigateTo(NavigationItem item)
    {
        if (item.Tag is not Type pageType)
            return;

        var page = (Page)_serviceProvider.GetRequiredService(pageType);
        NavigationDrawer.SelectedItem = item;

        // A Frame doesn't pass inherited properties on to its Page, and the theme reaches a
        // control through one (SfSkinManager.Theme; see App.xaml.cs), so the page is themed
        // directly or everything on it keeps WPF's own look.
        if (SfSkinManager.GetTheme(page) is null)
            SfSkinManager.SetTheme(page, SfSkinManager.ApplicationTheme);

        if (ReferenceEquals(ContentFrame.Content, page))
            return;

        ContentFrame.Navigate(page);
    }

    /// <summary>Clears the journal the Frame just added to -- the app has no back/forward --
    /// so it doesn't hold on to the pages it showed, then runs the page lifecycle calls
    /// <see cref="NavigateTo"/> deferred to here. async void, so an exception from a page's
    /// OnNavigatedToAsync reaches App's DispatcherUnhandledException handler, as it did when
    /// WPF-UI's NavigationView made these calls.</summary>
    private async void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        while (ContentFrame.CanGoBack)
            ContentFrame.RemoveBackEntry();

        var previous = _currentPage;
        _currentPage = e.Content as INavigationAware;

        if (previous is not null && !ReferenceEquals(previous, _currentPage))
            await previous.OnNavigatedFromAsync();
        if (_currentPage is not null)
            await _currentPage.OnNavigatedToAsync();
    }

    /// <summary>The footer items' dialogs, by the name in each one's Tag
    /// (MainWindow.xaml).</summary>
    private void OpenDialog(string dialog)
    {
        switch (dialog)
        {
            case "Holidays":
                OpenManageHolidays();
                break;
            case "Users":
                OpenManageUsers();
                break;
            case "Backup":
                OpenBackupRestore();
                break;
            case "Settings":
                OpenSettings();
                break;
        }
    }

    /// <summary>Opens SettingsDialog pre-filled with whatever's currently effective --
    /// connection string, saved connection profiles, attendance device defaults,
    /// attendance policy, the Net Pay rounding multiple, the sign-in logo, and the
    /// payslip company name -- whether that's coming from the shared file already
    /// overriding, or from appsettings.json (all already merged into
    /// _configuration/_attendanceSettings/_payrollSettings/_signInSettings/
    /// _connectionProfilesSettings by the time MainWindow exists, see App.OnStartup),
    /// then hands anything the user Saved to SharedConfigWriter. AttendanceSettings/
    /// PayrollSettings/ConnectionProfilesSettings rather than _configuration for the
    /// device fields/policies/profiles since AttendanceSettings.DeviceTransport/
    /// Policy, PayrollSettings.Policy/CompanyName, and
    /// ConnectionProfilesSettings.Profiles are already the shapes SettingsDialog/
    /// SharedConfigWriter expect.</summary>
    private void OpenSettings()
    {
        var dialog = new SettingsDialog(
            _provisioningService,
            _configuration.GetConnectionString("ScheduleDb") ?? string.Empty,
            _connectionProfilesSettings.Profiles,
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
                dialog.ChangedCompanyName,
                dialog.ChangedConnectionProfiles);
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
    /// currently effective -- same source OpenSettings reads (_configuration,
    /// already merged from appsettings.json and the shared config file by the time
    /// MainWindow exists -- see App.OnStartup).</summary>
    private void OpenBackupRestore()
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
    private void OpenManageUsers()
    {
        var dialog = new ManageUsersDialog(_userAccountRepository, _currentUser) { Owner = this };
        dialog.ShowDialog();
    }

    /// <summary>Opens ManageHolidaysDialog -- see its own doc comment for what it
    /// covers.</summary>
    private void OpenManageHolidays()
    {
        var dialog = new ManageHolidaysDialog(_holidayRepository, _dataVersion) { Owner = this };
        dialog.ShowDialog();
    }
}
