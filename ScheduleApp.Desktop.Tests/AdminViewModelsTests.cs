using System.Reactive.Linq;
using System.Windows.Input;
using Microsoft.Data.SqlClient;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using Xunit;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Tests;

public class BackupRestoreViewModelTests
{
    private const string ConnectionString = "Server=.;Database=ScheduleAppDb";

    private readonly IDatabaseBackupService _backup = Substitute.For<IDatabaseBackupService>();
    private readonly BackupRestoreViewModel _vm;
    private readonly List<Notice> _notices = [];
    private FileRequest? _request;
    private string? _pickedPath = @"D:\backups\db.bak";
    private bool _confirmAnswer = true;

    public BackupRestoreViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _backup.TryGetDefaultBackupFolderAsync(ConnectionString, Arg.Any<CancellationToken>()).Returns(@"C:\SQL\Backup");
        _vm = new BackupRestoreViewModel(_backup, ConnectionString, () => new DateTime(2026, 10, 10, 14, 30, 5));
        _vm.PickFileToSave.RegisterHandler(ctx =>
        {
            _request = ctx.Input;
            ctx.SetOutput(_pickedPath);
        });
        _vm.PickFileToOpen.RegisterHandler(ctx =>
        {
            _request = ctx.Input;
            ctx.SetOutput(_pickedPath);
        });
        _vm.Confirm.RegisterHandler(ctx => ctx.SetOutput(_confirmAnswer));
        _vm.Notify.RegisterHandler(ctx =>
        {
            _notices.Add(ctx.Input);
            ctx.SetOutput(RxVoid.Default);
        });
    }

    [Fact]
    public async Task Backing_up_starts_in_sql_servers_folder_with_a_dated_name()
    {
        await _vm.BackupCommand.Execute();

        Assert.Equal("ScheduleAppDb_20261010_143005.bak", _request!.FileName);
        Assert.Equal(@"C:\SQL\Backup", _request.InitialDirectory);
        await _backup.Received(1).BackupAsync(ConnectionString, @"D:\backups\db.bak", Arg.Any<CancellationToken>());
        Assert.StartsWith("Backup complete:\nD:\\backups\\db.bak", _notices.Single().Message, StringComparison.Ordinal);
        Assert.False(_vm.IsBusy);
    }

    [Fact]
    public async Task Restoring_is_confirmed_first()
    {
        _confirmAnswer = false;
        await _vm.RestoreCommand.Execute();
        await _backup.DidNotReceiveWithAnyArgs().RestoreAsync(default!, default!);

        _confirmAnswer = true;
        await _vm.RestoreCommand.Execute();
        await _backup.Received(1).RestoreAsync(ConnectionString, @"D:\backups\db.bak", Arg.Any<CancellationToken>());
        Assert.StartsWith("Restore complete.", _notices.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancelled_picker_does_nothing()
    {
        _pickedPath = null;
        await _vm.BackupCommand.Execute();
        await _vm.RestoreCommand.Execute();

        await _backup.DidNotReceiveWithAnyArgs().BackupAsync(default!, default!);
        await _backup.DidNotReceiveWithAnyArgs().RestoreAsync(default!, default!);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task One_runs_at_a_time_and_a_failure_is_explained()
    {
        var gate = new TaskCompletionSource();
        _backup.BackupAsync(ConnectionString, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(gate.Task);

        var running = BackUpAsync();
        Assert.True(_vm.IsBusy);
        Assert.Equal("Backing up…", _vm.ProgressText);
        Assert.False(((ICommand)_vm.RestoreCommand).CanExecute(null));

        gate.SetException(new InvalidOperationException("disk full"));
        await running;

        Assert.False(_vm.IsBusy);
        Assert.True(((ICommand)_vm.RestoreCommand).CanExecute(null));
        var notice = _notices.Single();
        Assert.Equal(NoticeKind.Error, notice.Kind);
        Assert.Equal("Backup failed:\n\ndisk full", notice.Message);

        async Task BackUpAsync() => await _vm.BackupCommand.Execute();
    }
}

public class DatabaseSetupViewModelTests
{
    private readonly IDatabaseProvisioningService _provisioning = Substitute.For<IDatabaseProvisioningService>();

    public DatabaseSetupViewModelTests() => ReactiveTestSetup.EnsureInitialized();

    [Fact]
    public void Prefills_the_server_and_database_from_the_connection_in_effect()
    {
        var vm = new DatabaseSetupViewModel(_provisioning, @"Server=.\SQLEXPRESS;Database=ScheduleAppDb;Trusted_Connection=True");

        Assert.Equal(@".\SQLEXPRESS", vm.Server);
        Assert.Equal("ScheduleAppDb", vm.DatabaseName);
        Assert.True(vm.CreatesDedicatedLogin);
        Assert.Equal("Database Setup", vm.Title);
        Assert.Equal("Cancel", vm.CancelLabel);

        var junk = new DatabaseSetupViewModel(_provisioning, "not a connection string");
        Assert.Empty(junk.Server);
    }

    [Fact]
    public async Task The_required_first_run_builds_a_windows_authenticated_connection()
    {
        var vm = new DatabaseSetupViewModel(_provisioning, null, DatabaseSetupMode.WindowsAuthOnly, isRequiredFirstRun: true);
        Assert.Equal("Database Setup (Required)", vm.Title);
        Assert.Equal("Exit", vm.CancelLabel);
        Assert.StartsWith("Schedule Manager needs a database", vm.IntroText, StringComparison.Ordinal);
        Assert.False(vm.CreatesDedicatedLogin);

        Assert.False(await vm.CreateCommand.Execute());
        Assert.Equal("Enter the SQL Server instance name.", vm.ErrorMessage);

        vm.Server = @" .\SQLEXPRESS ";
        vm.DatabaseName = "ScheduleAppDb";
        Assert.True(await vm.CreateCommand.Execute());

        var built = new SqlConnectionStringBuilder(vm.ConnectionString);
        Assert.Equal(@".\SQLEXPRESS", built.DataSource);
        Assert.Equal("ScheduleAppDb", built.InitialCatalog);
        Assert.True(built.IntegratedSecurity);
        await _provisioning.DidNotReceiveWithAnyArgs().ProvisionAsync(default!);
    }

    [Fact]
    public async Task A_dedicated_login_is_provisioned_with_whats_asked_for()
    {
        _provisioning.ProvisionAsync(Arg.Any<DatabaseProvisioningRequest>(), Arg.Any<CancellationToken>()).Returns("Server=.;User Id=app");
        var vm = new DatabaseSetupViewModel(_provisioning, null)
        {
            Server = ".",
            DatabaseName = "ScheduleAppDb",
            UseWindowsAuthForAdmin = false,
        };

        Assert.False(await vm.CreateCommand.Execute());
        Assert.Equal("Enter the admin username and password, or switch to Windows (integrated).", vm.ErrorMessage);

        vm.AdminUsername = "sa";
        vm.AdminPassword = "admin-pass";
        Assert.False(await vm.CreateCommand.Execute());
        Assert.Equal("Enter a username for the new login.", vm.ErrorMessage);

        vm.NewLoginUsername = "app";
        vm.NewLoginPassword = "app-password";
        vm.ConfirmPassword = "app-passw0rd";
        Assert.False(await vm.CreateCommand.Execute());
        Assert.Equal("Password and confirmation don't match.", vm.ErrorMessage);
        Assert.Empty(vm.ConfirmPassword);

        vm.ConfirmPassword = "app-password";
        vm.GrantsFullAccess = false;
        Assert.True(await vm.CreateCommand.Execute());

        Assert.Equal("Server=.;User Id=app", vm.ConnectionString);
        await _provisioning.Received(1).ProvisionAsync(
            new DatabaseProvisioningRequest(".", "ScheduleAppDb", false, "sa", "admin-pass", "app", "app-password", DatabaseAccessLevel.ReadWrite),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_provisioning_failure_is_shown()
    {
        _provisioning.ProvisionAsync(Arg.Any<DatabaseProvisioningRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ArgumentException("Database names may only contain letters, digits and underscores."));
        var vm = new DatabaseSetupViewModel(_provisioning, null)
        {
            Server = ".", DatabaseName = "bad name", NewLoginUsername = "app", NewLoginPassword = "app-password", ConfirmPassword = "app-password",
        };

        Assert.False(await vm.CreateCommand.Execute());
        Assert.Equal("Database names may only contain letters, digits and underscores.", vm.ErrorMessage);
        Assert.Null(vm.ConnectionString);
        Assert.False(vm.IsBusy);
    }
}

public class SettingsViewModelTests
{
    private const string Effective = @"Data Source=.\SQLEXPRESS;Initial Catalog=ScheduleAppDb;Integrated Security=True;Trust Server Certificate=True";

    private readonly IDatabaseProvisioningService _provisioning = Substitute.For<IDatabaseProvisioningService>();
    private readonly List<FieldProblem> _problems = [];
    private readonly List<string> _notices = [];
    private readonly HashSet<string> _loadableImages = [@"C:\logos\current.png", @"C:\logos\new.png"];
    private string? _pickedImage;
    private Func<ReactiveViewModel, bool> _dialog = _ => false;

    public SettingsViewModelTests() => ReactiveTestSetup.EnsureInitialized();

    private static CurrentSettings Current(
        string connectionString = Effective, IReadOnlyList<ConnectionProfile>? profiles = null, string? logoPath = @"C:\logos\current.png") =>
        new(connectionString, profiles ?? [], "192.168.1.201", 4370, 0, "Tcp", 10, new AttendancePolicy(), new PayrollPolicy(),
            logoPath, null, @"C:\ProgramData\ScheduleApp\shared.appsettings.json");

    private SettingsViewModel NewSettings(CurrentSettings? current = null)
    {
        var vm = new SettingsViewModel(_provisioning, current ?? Current(), path => path is not null && _loadableImages.Contains(path));
        vm.ShowFieldProblem.RegisterHandler(ctx =>
        {
            _problems.Add(ctx.Input);
            ctx.SetOutput(RxVoid.Default);
        });
        vm.Notify.RegisterHandler(ctx =>
        {
            _notices.Add(ctx.Input.Message);
            ctx.SetOutput(RxVoid.Default);
        });
        vm.PickFileToOpen.RegisterHandler(ctx => ctx.SetOutput(_pickedImage));
        vm.ShowDialog.RegisterHandler(ctx => ctx.SetOutput(_dialog(ctx.Input)));
        return vm;
    }

    private static async Task<SettingsChanges?> SaveAsync(SettingsViewModel vm) =>
        await vm.SaveCommand.Execute() ? vm.AcceptedChanges : null;

    [Fact]
    public void Opens_on_whats_in_effect()
    {
        var vm = NewSettings();

        Assert.Equal(@"File: C:\ProgramData\ScheduleApp\shared.appsettings.json", vm.SharedConfigFileText);
        var current = Assert.Single(vm.Profiles);
        Assert.Equal(new ConnectionProfile("Current", @".\SQLEXPRESS", "ScheduleAppDb"), current);
        Assert.Same(current, vm.SelectedProfile);
        Assert.False(vm.IsAdvancedExpanded);
        Assert.Equal("Advanced ▾", vm.AdvancedLabel);
        Assert.Equal("4370", vm.PortText);
        Assert.Equal(30d, vm.RestDayPremiumPercent!.Value, 6);
        Assert.Equal(ScheduleApp.Payroll.Pdf.PayslipLineBuilder.DefaultCompanyName, vm.CompanyName);
        Assert.Equal(@"C:\logos\current.png", vm.LogoPreviewPath);
        Assert.Equal("Custom logo: current.png", vm.LogoStatusText);
    }

    [Fact]
    public void A_connection_no_profile_produces_opens_under_advanced()
    {
        var vm = NewSettings(Current("Server=db;User Id=app;Password=x", [new ConnectionProfile("Prod", "db", "ScheduleAppDb")]));

        Assert.Null(vm.SelectedProfile);
        Assert.True(vm.IsAdvancedExpanded);
        Assert.Equal("Advanced ▴", vm.AdvancedLabel);
        Assert.Equal("Server=db;User Id=app;Password=x", vm.ConnectionString);
    }

    [Fact]
    public async Task Saving_nothing_changed_says_so()
    {
        var vm = NewSettings();

        Assert.Null(await SaveAsync(vm));
        Assert.Equal(["Nothing was changed."], _notices);
    }

    [Fact]
    public async Task Only_the_groups_that_changed_are_saved()
    {
        var vm = NewSettings();
        vm.PortText = "4371";
        vm.LateEarlyGraceMinutesText = "7";
        vm.HolidayPremiumPercent = 150;

        var changes = await SaveAsync(vm);

        Assert.NotNull(changes);
        Assert.Null(changes.ConnectionString);
        Assert.Null(changes.ConnectionProfiles);
        Assert.Equal(new DeviceDefaults("192.168.1.201", 4371, 0, "Tcp"), changes.Device);
        Assert.Null(changes.DefaultWorkTimeHours);
        Assert.Equal(7, changes.Policy!.LateInEarlyOutGraceMinutes);
        Assert.Equal(new AttendancePolicy().ClockInBufferBefore, changes.Policy.ClockInBufferBefore);
        Assert.Equal(1.5m, changes.PayrollPolicy!.HolidayPremiumPercentage);
        Assert.Equal(new PayrollPolicy().RestDayPremiumPercentage, changes.PayrollPolicy.RestDayPremiumPercentage);
        Assert.Null(changes.LogoPath);
        Assert.Null(changes.CompanyName);
    }

    [Fact]
    public async Task A_cleared_premium_keeps_the_policys_value()
    {
        var vm = NewSettings();
        vm.OvertimeRatePercent = null;
        vm.CompanyName = "Panaderia";

        var changes = await SaveAsync(vm);

        Assert.Null(changes!.PayrollPolicy);
        Assert.Equal("Panaderia", changes.CompanyName);
    }

    [Fact]
    public async Task The_first_bad_field_is_pointed_at()
    {
        var vm = NewSettings();
        vm.PortText = "70000";
        vm.FlexMinBreakGapText = "-1";

        Assert.Null(await SaveAsync(vm));
        vm.PortText = "4370";
        Assert.Null(await SaveAsync(vm));
        vm.FlexMinBreakGapText = "0.5";
        vm.NightDiffEnd = null;
        Assert.Null(await SaveAsync(vm));

        Assert.Equal(
            [
                new FieldProblem(SettingsField.Port, "Port must be a number between 1 and 65535.", "Invalid port"),
                new FieldProblem(SettingsField.FlexMinBreakGap, "Flexible minimum break gap must be a number of hours, 0 or greater.", "Invalid value"),
                new FieldProblem(SettingsField.NightDiffEnd, "Night differential end must be a valid time.", "Invalid value"),
            ],
            _problems);
    }

    [Fact]
    public async Task A_blank_connection_string_opens_advanced_to_point_at_it()
    {
        var vm = NewSettings();
        vm.SelectedProfile = null;
        vm.ConnectionString = " ";

        Assert.Null(await SaveAsync(vm));

        Assert.True(vm.IsAdvancedExpanded);
        Assert.Equal(SettingsField.ConnectionString, _problems.Single().Field);
    }

    [Fact]
    public async Task Profiles_can_be_added_picked_and_removed()
    {
        var vm = NewSettings();

        vm.BeginAddProfileCommand.Execute().Subscribe();
        Assert.True(vm.IsAddingProfile);
        vm.NewProfileName = "Testing";
        await vm.ConfirmAddProfileCommand.Execute();
        Assert.Equal(SettingsField.NewProfileServer, _problems.Single().Field);

        vm.NewProfileServer = "testbox";
        vm.NewProfileDatabase = "ScheduleTest";
        await vm.ConfirmAddProfileCommand.Execute();
        Assert.False(vm.IsAddingProfile);
        Assert.Equal("Testing", vm.SelectedProfile!.Name);
        Assert.Equal(vm.SelectedProfile.ToConnectionString(), vm.ConnectionString);

        var changes = await SaveAsync(vm);
        Assert.Equal(["Current", "Testing"], changes!.ConnectionProfiles!.Select(p => p.Name));
        Assert.Equal(vm.SelectedProfile.ToConnectionString(), changes.ConnectionString);

        vm.RemoveProfileCommand.Execute().Subscribe();
        Assert.Null(vm.SelectedProfile);
        Assert.Equal(["Current"], vm.Profiles.Select(p => p.Name));
    }

    [Fact]
    public async Task Database_setup_fills_in_the_connection_string()
    {
        var vm = NewSettings();
        DatabaseSetupViewModel? setup = null;
        _dialog = dialog =>
        {
            setup = Assert.IsType<DatabaseSetupViewModel>(dialog);
            setup.Server = "newbox";
            setup.DatabaseName = "Fresh";
            return setup.CreateCommand.Execute().Wait();
        };

        await vm.CreateDatabaseCommand.Execute();

        Assert.False(setup!.CreatesDedicatedLogin);
        Assert.Equal("Fresh", new SqlConnectionStringBuilder(vm.ConnectionString).InitialCatalog);
        Assert.Equal(vm.ConnectionString, (await SaveAsync(vm))!.ConnectionString);

        _dialog = dialog =>
        {
            setup = (DatabaseSetupViewModel)dialog;
            return false;
        };
        await vm.CreateDedicatedLoginCommand.Execute();
        Assert.True(setup.CreatesDedicatedLogin);
    }

    [Fact]
    public async Task A_logo_is_picked_or_reset()
    {
        var vm = NewSettings();

        _pickedImage = @"C:\logos\broken.png";
        await vm.ChooseLogoCommand.Execute();
        Assert.Equal(["That file couldn't be loaded as an image."], _notices);
        Assert.Equal(@"C:\logos\current.png", vm.LogoPreviewPath);

        _pickedImage = @"C:\logos\new.png";
        await vm.ChooseLogoCommand.Execute();
        Assert.Equal(@"C:\logos\new.png", vm.LogoPreviewPath);
        Assert.Equal(@"C:\logos\new.png", (await SaveAsync(vm))!.LogoPath);

        vm.ResetLogoCommand.Execute().Subscribe();
        Assert.Null(vm.LogoPreviewPath);
        Assert.Equal("Will reset to the default logo when saved.", vm.LogoStatusText);
        Assert.Equal(string.Empty, (await SaveAsync(vm))!.LogoPath);
    }

    [Fact]
    public async Task Resetting_a_default_logo_changes_nothing()
    {
        var vm = NewSettings(Current(logoPath: null));
        Assert.Equal("Using the default logo.", vm.LogoStatusText);

        vm.ResetLogoCommand.Execute().Subscribe();

        Assert.Null(await SaveAsync(vm));
    }
}
