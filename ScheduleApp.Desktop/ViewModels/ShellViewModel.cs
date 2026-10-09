using System.Reactive.Linq;
using Microsoft.Extensions.Configuration;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using ScheduleApp.Core.Configuration;
using ScheduleApp.Core.Users;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels.Accounts;
using ScheduleApp.Desktop.ViewModels.Attendance;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>Where the main window is: behind the sign-in card, behind the first run's
/// account-creation card, or signed in.</summary>
public enum ShellStage
{
    SignIn,
    SetupAdmin,
    SignedIn,
}

/// <summary>
/// The main window: the sign-in gate drawn over it (the window shows straight away, the card
/// on top), the title naming who's signed in, whether the navigation drawer stays pinned open
/// (remembered between launches), and the drawer footer's dialogs -- Manage Holidays, Manage
/// Users, Backup &amp; Restore and Settings. Page navigation itself is the window's.
/// </summary>
public partial class ShellViewModel : ReactiveViewModel
{
    private readonly IUserAccountRepository _userAccounts;
    private readonly CurrentUserContext _currentUser;
    private readonly INavigationDrawerStateStore _drawerState;
    private readonly IConfiguration _configuration;
    private readonly AttendanceSettings _attendanceSettings;
    private readonly PayrollSettings _payrollSettings;
    private readonly SignInSettings _signInSettings;
    private readonly ConnectionProfilesSettings _connectionProfilesSettings;
    private readonly ISharedConfigWriter _sharedConfigWriter;
    private readonly IDatabaseBackupService _backupService;
    private readonly IDatabaseProvisioningService _provisioningService;
    private readonly IHolidayRepository _holidayRepository;
    private readonly AttendanceDataVersion _dataVersion;

    public ShellViewModel(
        IUserAccountRepository userAccounts,
        IRememberedSignInStore rememberedSignIn,
        CurrentUserContext currentUser,
        INavigationDrawerStateStore drawerState,
        IConfiguration configuration,
        AttendanceSettings attendanceSettings,
        PayrollSettings payrollSettings,
        SignInSettings signInSettings,
        ConnectionProfilesSettings connectionProfilesSettings,
        ISharedConfigWriter sharedConfigWriter,
        IDatabaseBackupService backupService,
        IDatabaseProvisioningService provisioningService,
        IHolidayRepository holidayRepository,
        AttendanceDataVersion dataVersion)
    {
        _userAccounts = userAccounts;
        _currentUser = currentUser;
        _drawerState = drawerState;
        _configuration = configuration;
        _attendanceSettings = attendanceSettings;
        _payrollSettings = payrollSettings;
        _signInSettings = signInSettings;
        _connectionProfilesSettings = connectionProfilesSettings;
        _sharedConfigWriter = sharedConfigWriter;
        _backupService = backupService;
        _provisioningService = provisioningService;
        _holidayRepository = holidayRepository;
        _dataVersion = dataVersion;

        SignIn = new SignInViewModel(userAccounts, rememberedSignIn);
        SetupAdmin = new SetupAdminViewModel(userAccounts);
        IsDrawerPinned = drawerState.LoadPinned();

        _titleHelper = this.WhenAnyValue(x => x.SignedInAs)
            .Select(username => username is null ? "Schedule Manager" : $"Schedule Manager -- Signed in as {username}")
            .ToProperty(this, x => x.Title);
        _pinGlyphHelper = this.WhenAnyValue(x => x.IsDrawerPinned)
            .Select(pinned => pinned ? "" : "")   // PinFill : Pin
            .ToProperty(this, x => x.PinGlyph);
        _pinToolTipHelper = this.WhenAnyValue(x => x.IsDrawerPinned)
            .Select(pinned => pinned ? "Unpin the navigation pane (fold it back to icons)" : "Pin the navigation pane open")
            .ToProperty(this, x => x.PinToolTip);

        Observable.Merge(SignIn.SignInCommand, SetupAdmin.CreateCommand)
            .Where(user => user is not null)
            .Subscribe(user =>
            {
                _currentUser.Set(user!.UserId, user.Username);
                SignedInAs = _currentUser.Username;
                Stage = ShellStage.SignedIn;
            });
    }

    public SignInViewModel SignIn { get; }

    public SetupAdminViewModel SetupAdmin { get; }

    /// <summary>The sign-in logo configured, if any.</summary>
    public string? LogoPath => _signInSettings.LogoPath;

    [Reactive]
    public partial ShellStage Stage { get; private set; }

    [Reactive]
    public partial string? SignedInAs { get; private set; }

    [ObservableAsProperty(InitialValue = "Schedule Manager")]
    public partial string Title { get; }

    /// <summary>Pinned, the drawer is the full labelled menu, always open beside the page;
    /// unpinned, an icon rail that opens over the page.</summary>
    [Reactive]
    public partial bool IsDrawerPinned { get; private set; }

    [ObservableAsProperty]
    public partial string PinGlyph { get; }

    [ObservableAsProperty]
    public partial string PinToolTip { get; }

    /// <summary>Asks the window to restart the app (after a Settings change only takes effect
    /// on the next start).</summary>
    public Interaction<RxVoid, RxVoid> Restart { get; } = new();

    /// <summary>Which card greets whoever's at the keyboard: sign-in, or -- with no accounts
    /// yet -- the first account's creation. Startup decides, having already reached the
    /// database.</summary>
    public void BeginSignIn(bool hasExistingAccounts) =>
        Stage = hasExistingAccounts ? ShellStage.SignIn : ShellStage.SetupAdmin;

    [ReactiveCommand]
    private void TogglePinned()
    {
        IsDrawerPinned = !IsDrawerPinned;
        _drawerState.SavePinned(IsDrawerPinned);
    }

    [ReactiveCommand]
    private async Task OpenManageHolidaysAsync() =>
        await ShowDialogAsync(new ManageHolidaysViewModel(_holidayRepository, _dataVersion));

    [ReactiveCommand]
    private async Task OpenManageUsersAsync() =>
        await ShowDialogAsync(new ManageUsersViewModel(_userAccounts, _currentUser));

    /// <summary>Against the connection string in effect.</summary>
    [ReactiveCommand]
    private async Task OpenBackupRestoreAsync()
    {
        var connectionString = _configuration.GetConnectionString("ScheduleDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await NotifyAsync("No database connection string is configured -- check Settings.", "Not configured", NoticeKind.Warning);
            return;
        }

        await ShowDialogAsync(new BackupRestoreViewModel(_backupService, connectionString));
    }

    /// <summary>
    /// Settings, prefilled with what's in effect. What it changes goes to the shared,
    /// machine-wide file -- read by every account running the app here and by the Push
    /// Listener service -- so that's confirmed first. It takes effect at the next start, which
    /// is offered.
    /// </summary>
    [ReactiveCommand]
    private async Task OpenSettingsAsync()
    {
        var settings = new SettingsViewModel(_provisioningService, CurrentSettings(), knownServers: SqlServerDiscovery.DiscoverServers());
        if (!await ShowDialogAsync(settings) || settings.AcceptedChanges is not { Any: true } changes) return;

        if (!await ConfirmAsync(
                "This changes settings shared by every account that runs Schedule Manager on this machine, and by the Push " +
                "Listener service if one is installed here.\n\nContinue?",
                "Confirm shared change", isWarning: true))
            return;

        string? savedPath;
        try
        {
            savedPath = _sharedConfigWriter.Save(
                changes.ConnectionString, changes.Device, changes.DefaultWorkTimeHours, changes.Policy, changes.PayrollPolicy,
                changes.LogoPath, changes.CompanyName, changes.ConnectionProfiles);
        }
        catch (UnauthorizedAccessException ex)
        {
            await NotifyAsync(
                "Could not save the shared config file -- access was denied.\n\n" + ex.Message +
                "\n\nThis usually means the current Windows account doesn't have write access to " + SharedConfigFile.DefaultPath +
                ". Ask whoever set this machine up to grant your account write access to that folder, or run Schedule Manager " +
                "as an administrator.",
                "Save failed -- access denied", NoticeKind.Error);
            return;
        }
        catch (Exception ex)
        {
            await NotifyAsync("Could not save the shared config file.\n\n" + ex.Message, "Save failed", NoticeKind.Error);
            return;
        }

        if (savedPath is null) return;

        if (await ConfirmAsync(
                $"Saved to {savedPath}.\n\nThis only takes effect the next time an app reads it at startup -- Schedule Manager, " +
                "and the Push Listener service (ScheduleAppPushListener) if one is installed on this machine.\n\n" +
                "Restart Schedule Manager now?",
                "Saved"))
            await Restart.Handle(RxVoid.Default);
    }

    /// <summary>What's in effect now -- the shared file already merged over
    /// appsettings.json at startup.</summary>
    private CurrentSettings CurrentSettings() => new(
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
        SharedConfigFile.ResolvePath());
}
