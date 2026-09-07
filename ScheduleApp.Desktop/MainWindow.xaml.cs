using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.Configuration;
using ScheduleApp.Core.Configuration;
using ScheduleApp.Core.Users;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Attendance;
using ScheduleApp.Desktop.Views;

// Aliased rather than `using Wpf.Ui.Controls;` -- that namespace also has a MessageBox
// type, which would make the several MessageBox.Show(...) calls below ambiguous.
using NavigatedEventArgs = Wpf.Ui.Controls.NavigatedEventArgs;
using NavigationView = Wpf.Ui.Controls.NavigationView;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

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
    private readonly NavigationDrawerStateStore _navDrawerStateStore;

    /// <summary>Whether the navigation drawer is pinned open. Loaded from
    /// <see cref="_navDrawerStateStore"/> in the constructor and applied to the pane in
    /// <see cref="OnAuthSucceeded"/> (same "wait for sign-in" timing as the first
    /// Navigate/Title); toggled by <see cref="PinPaneButton_Click"/>. When false the pane
    /// sits compact and hover-expands from the hamburger (see
    /// <see cref="PaneToggleButton_MouseEnter"/>).</summary>
    private bool _navDrawerPinned;

    /// <summary>Grace period before an unpinned, hover-expanded drawer collapses once the
    /// pointer is off the pane -- so brushing past its edge doesn't flicker it shut.
    /// Started from RootNavigationView_MouseMove (pointer moved onto the page content) or
    /// RootNavigationView_MouseLeave (pointer left the control entirely), and cancelled
    /// the moment MouseMove sees it back over the pane. Never runs while pinned. (Picking
    /// a nav item collapses on its own -- see RootNavigationView_Navigated.)</summary>
    private readonly DispatcherTimer _paneHoverCloseTimer;

    /// <summary>The pane's hamburger toggle (PART_ToggleButton), resolved from the
    /// NavigationView template on load so the drawer can hover-expand from *that button
    /// specifically* rather than anywhere on the compact rail -- see
    /// PaneToggleButton_MouseEnter. Null only if the template ever changes that part
    /// name.</summary>
    private FrameworkElement? _paneToggleButton;

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
        RememberedSignInStore rememberedSignInStore,
        NavigationDrawerStateStore navigationDrawerStateStore)
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
        _navDrawerStateStore = navigationDrawerStateStore;

        // Read now, applied to the pane in OnAuthSucceeded (see below) alongside the
        // rest of the deferred startup.
        _navDrawerPinned = navigationDrawerStateStore.LoadPinned();

        _paneHoverCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _paneHoverCloseTimer.Tick += PaneHoverCloseTimer_Tick;

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

        // Forces the pane to its intended startup state -- not just left to XAML's
        // IsPaneOpen="False": WPF-UI's NavigationView.IsPaneOpenProperty actually
        // defaults to true (open), and its own OnLoaded handler doesn't reliably re-sync
        // the visual state to a same-or-different XAML-time value either -- the pane was
        // still showing fully open on launch despite IsPaneOpen="False" sitting right
        // there in XAML. Assigning the opposite first, then the wanted value, guarantees
        // a real value *change*, so WPF-UI's OnIsPaneOpenChanged callback (the thing that
        // drives the PaneOpen/PaneCompact visual state via VisualStateManager.GoToState)
        // is guaranteed to fire and land where we want, once the window has loaded and
        // the first navigation above has run. Wanted state is compact by default, or open
        // if the drawer was left pinned last run (see PinPaneButton_Click /
        // NavigationDrawerStateStore).
        RootNavigationView.IsPaneOpen = !_navDrawerPinned;
        RootNavigationView.IsPaneOpen = _navDrawerPinned;
        ApplyNavDrawerPinnedVisual();
    }

    /// <summary>Toggles the navigation drawer's pinned-open state and remembers it for
    /// next launch. Pinned: the pane stays expanded and its own toggle (hamburger) is
    /// hidden -- the pin owns that job now, with RootNavigationView_PaneClosed guarding
    /// against anything else collapsing it. Unpinned: the pane drops straight back to its
    /// compact rail and from then on hover-expands from the hamburger (see
    /// PaneToggleButton_MouseEnter).</summary>
    private void PinPaneButton_Click(object sender, RoutedEventArgs e)
    {
        _navDrawerPinned = !_navDrawerPinned;
        _navDrawerStateStore.SavePinned(_navDrawerPinned);
        ApplyNavDrawerPinnedVisual();

        if (_navDrawerPinned)
        {
            _paneHoverCloseTimer.Stop();
            RootNavigationView.IsPaneOpen = true;
        }
        // Unpinned: the pointer is still on the pane (the pin lives in it), so don't
        // slam it shut here -- that would just hover-reopen on the next MouseMove and
        // flicker. The hover handlers collapse it the moment the pointer leaves.
    }

    /// <summary>Syncs the pin button's icon/tooltip and the pane toggle's visibility to
    /// <see cref="_navDrawerPinned"/>. Called from PinPaneButton_Click and once from
    /// OnAuthSucceeded after the stored value is applied.</summary>
    private void ApplyNavDrawerPinnedVisual()
    {
        PinPaneButton.Icon = new SymbolIcon { Symbol = SymbolRegular.Pin24, Filled = _navDrawerPinned };
        PinPaneButton.ToolTip = _navDrawerPinned
            ? "Unpin the navigation pane (let it collapse and hover-expand again)"
            : "Pin the navigation pane open";

        // While pinned the hamburger would only ever collapse a pane we immediately
        // re-open (see RootNavigationView_PaneClosed), so hide it -- the pin is the
        // control now. Restored when unpinned, where hovering it expands the pane
        // (PaneToggleButton_MouseEnter).
        RootNavigationView.IsPaneToggleVisible = !_navDrawerPinned;
    }

    /// <summary>Resolves the hamburger toggle (PART_ToggleButton) out of the applied
    /// NavigationView template and hooks its MouseEnter, so the unpinned drawer
    /// hover-expands from that button alone -- not from anywhere on the compact rail.
    /// Guarded so a second Loaded (theme change, re-parent) doesn't double-subscribe.</summary>
    private void RootNavigationView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_paneToggleButton is not null)
            return;

        _paneToggleButton = RootNavigationView.Template?.FindName("PART_ToggleButton", RootNavigationView) as FrameworkElement;
        if (_paneToggleButton is not null)
            _paneToggleButton.MouseEnter += PaneToggleButton_MouseEnter;
    }

    /// <summary>Unpinned: hovering the hamburger expands the drawer. Also dismisses the
    /// compact Attendance flyout if it's up -- the flyout and an expanded pane are two
    /// ways to show the same thing and should never be on screen together. (Pinned, the
    /// pane is already open and this button is hidden anyway.)</summary>
    private void PaneToggleButton_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_navDrawerPinned)
            return;

        _paneHoverCloseTimer.Stop();
        AttendanceFlyout.IsOpen = false;
        RootNavigationView.IsPaneOpen = true;
    }

    /// <summary>Unpinned: navigating to a page is the cue to get out of the way, so
    /// collapse the drawer back to its rail. Fires for every real page (top menu items,
    /// Attendance's three leaf pages inline or via the compact flyout), never for the
    /// Attendance parent (no navigation) or the footer dialog items (no TargetPageType --
    /// see MainWindow.xaml).
    ///
    /// The collapse is deferred to the next dispatcher turn rather than done inline: when
    /// an *inline* sub-item is clicked, WPF-UI's NavigationViewItem.OnClick raises this
    /// Navigated event *before* it finishes raising the item's own Click, which then
    /// bubbles up to AttendanceMenuItem_Click. Collapsing inline here would leave
    /// IsPaneOpen false by the time that bubble runs, and its `!IsPaneOpen` compact-flyout
    /// check would misfire -- popping the Attendance flyout onto the page just navigated
    /// to. Deferring lets the whole synchronous click finish with the pane still open.</summary>
    private void RootNavigationView_Navigated(NavigationView sender, NavigatedEventArgs e)
    {
        if (_navDrawerPinned)
            return;

        _paneHoverCloseTimer.Stop();
        Dispatcher.BeginInvoke(() =>
        {
            if (_navDrawerPinned)
                return;

            RootNavigationView.IsPaneOpen = false;
            AttendanceFlyout.IsOpen = false; // insurance: never leave it stranded on the new page
        });
    }

    /// <summary>Unpinned and hover-expanded: collapse once the pointer is off the pane.
    /// Position-based rather than a plain MouseLeave, because RootNavigationView spans the
    /// page content too -- moving from the pane into the content never leaves the control,
    /// so MouseLeave alone left the drawer stuck open. The pane occupies x in
    /// [0, OpenPaneLength] of the control, so anything past that edge is "off the pane"
    /// and starts the grace timer; coming back within it cancels the timer again.</summary>
    private void RootNavigationView_MouseMove(object sender, MouseEventArgs e)
    {
        if (_navDrawerPinned || !RootNavigationView.IsPaneOpen)
            return;

        // A few px of slack so the pane's own right edge doesn't read as "outside".
        double x = e.GetPosition(RootNavigationView).X;
        if (x <= RootNavigationView.OpenPaneLength + 8)
            _paneHoverCloseTimer.Stop();
        else if (!_paneHoverCloseTimer.IsEnabled)
            _paneHoverCloseTimer.Start();
    }

    /// <summary>Pointer left the NavigationView entirely (out of the window, onto the
    /// title bar, onto a popup) -- same grace-period collapse as moving onto the page
    /// content above. No matching MouseEnter cancel: re-entering over the *content* isn't
    /// a reason to keep the drawer open, and MouseMove above already cancels the moment
    /// the pointer is back over the pane itself.</summary>
    private void RootNavigationView_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_navDrawerPinned && RootNavigationView.IsPaneOpen && !_paneHoverCloseTimer.IsEnabled)
            _paneHoverCloseTimer.Start();
    }

    private void PaneHoverCloseTimer_Tick(object? sender, EventArgs e)
    {
        _paneHoverCloseTimer.Stop();
        if (!_navDrawerPinned)
            RootNavigationView.IsPaneOpen = false;
    }

    /// <summary>Keeps a pinned drawer from actually staying collapsed. Nothing closes the
    /// pane while pinned today (the toggle is hidden -- see ApplyNavDrawerPinnedVisual),
    /// but WPF-UI internals or future code might; re-open it on the next dispatcher turn
    /// rather than fighting the close mid-transition.</summary>
    private void RootNavigationView_PaneClosed(NavigationView sender, RoutedEventArgs e)
    {
        if (_navDrawerPinned)
            Dispatcher.BeginInvoke(() => RootNavigationView.IsPaneOpen = true);
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
