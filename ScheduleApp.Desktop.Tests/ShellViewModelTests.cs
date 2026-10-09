using System.Reactive.Linq;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Payroll;
using ScheduleApp.Core.Users;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Accounts;
using ScheduleApp.Desktop.ViewModels.Attendance;
using Xunit;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Tests;

/// <summary>The main window's ViewModel, over stubbed stores and services.</summary>
internal sealed class TestShell
{
    public const string ConnectionString = @"Data Source=.\SQLEXPRESS;Initial Catalog=ScheduleAppDb;Integrated Security=True;Trust Server Certificate=True";

    public TestShell(string? connectionString = ConnectionString)
    {
        ReactiveTestSetup.EnsureInitialized();
        var (hash, salt) = PasswordHasher.Hash("correct horse");
        Users.GetByUsernameAsync("maria", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<UserAccount?>(new UserAccount { Id = 4, Username = "maria", PasswordHash = hash, PasswordSalt = salt }));
        Users.AddAsync(Arg.Any<UserAccount>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new UserAccount { Id = 1, Username = call.Arg<UserAccount>().Username }));

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(connectionString is null ? [] : [new("ConnectionStrings:ScheduleDb", connectionString)])
            .Build();
        ViewModel = new ShellViewModel(Users, Remembered, CurrentUser, DrawerState, configuration, new AttendanceSettings(),
            new PayrollSettings(), new SignInSettings(), new ConnectionProfilesSettings(), ConfigWriter, Substitute.For<IDatabaseBackupService>(),
            Substitute.For<IDatabaseProvisioningService>(), Substitute.For<IHolidayRepository>(), new AttendanceDataVersion());
    }

    public IUserAccountRepository Users { get; } = Substitute.For<IUserAccountRepository>();
    public IRememberedSignInStore Remembered { get; } = Substitute.For<IRememberedSignInStore>();
    public INavigationDrawerStateStore DrawerState { get; } = Substitute.For<INavigationDrawerStateStore>();
    public ISharedConfigWriter ConfigWriter { get; } = Substitute.For<ISharedConfigWriter>();
    public CurrentUserContext CurrentUser { get; } = new();
    public ShellViewModel ViewModel { get; }
}

public class ShellViewModelTests
{
    private readonly TestShell _shell = new();
    private readonly List<string> _notices = [];
    private readonly List<string> _questions = [];
    private Func<ReactiveViewModel, bool> _dialog = _ => false;
    private Func<string, bool> _confirm = _ => true;
    private int _restarts;

    public ShellViewModelTests() => Answer(_shell.ViewModel);

    private ShellViewModel Vm => _shell.ViewModel;

    private void Answer(ShellViewModel vm)
    {
        vm.Notify.RegisterHandler(ctx =>
        {
            _notices.Add(ctx.Input.Message);
            ctx.SetOutput(RxVoid.Default);
        });
        vm.Confirm.RegisterHandler(ctx =>
        {
            _questions.Add(ctx.Input.Title);
            ctx.SetOutput(_confirm(ctx.Input.Title));
        });
        vm.ShowDialog.RegisterHandler(ctx => ctx.SetOutput(_dialog(ctx.Input)));
        vm.Restart.RegisterHandler(ctx =>
        {
            _restarts++;
            ctx.SetOutput(RxVoid.Default);
        });
    }

    [Fact]
    public async Task Signing_in_reveals_the_app_and_names_who_in_the_title()
    {
        Vm.BeginSignIn(hasExistingAccounts: true);
        Assert.Equal(ShellStage.SignIn, Vm.Stage);
        Assert.Equal("Schedule Manager", Vm.Title);

        Vm.SignIn.Username = "maria";
        Vm.SignIn.Password = "wrong";
        await Vm.SignIn.SignInCommand.Execute();
        Assert.Equal(ShellStage.SignIn, Vm.Stage);

        Vm.SignIn.Password = "correct horse";
        await Vm.SignIn.SignInCommand.Execute();

        Assert.Equal(ShellStage.SignedIn, Vm.Stage);
        Assert.Equal((4, "maria"), (_shell.CurrentUser.UserId, _shell.CurrentUser.Username));
        Assert.Equal("Schedule Manager -- Signed in as maria", Vm.Title);
    }

    [Fact]
    public async Task With_no_accounts_the_first_one_is_created_and_signed_in()
    {
        Vm.BeginSignIn(hasExistingAccounts: false);
        Assert.Equal(ShellStage.SetupAdmin, Vm.Stage);

        Vm.SetupAdmin.Username = "admin";
        Vm.SetupAdmin.Password = Vm.SetupAdmin.ConfirmPassword = "s3cret-pass";
        await Vm.SetupAdmin.CreateCommand.Execute();

        Assert.Equal(ShellStage.SignedIn, Vm.Stage);
        Assert.Equal("Schedule Manager -- Signed in as admin", Vm.Title);
    }

    [Fact]
    public void The_drawer_remembers_being_pinned()
    {
        Assert.False(Vm.IsDrawerPinned);
        Assert.Equal("\uE718", Vm.PinGlyph);
        Assert.Equal("Pin the navigation pane open", Vm.PinToolTip);

        Vm.TogglePinnedCommand.Execute().Subscribe();

        Assert.True(Vm.IsDrawerPinned);
        Assert.Equal("\uE841", Vm.PinGlyph);
        Assert.Equal("Unpin the navigation pane (fold it back to icons)", Vm.PinToolTip);
        _shell.DrawerState.Received(1).SavePinned(true);
    }

    [Fact]
    public async Task The_footer_opens_each_dialog()
    {
        var shown = new List<Type>();
        _dialog = dialog =>
        {
            shown.Add(dialog.GetType());
            return false;
        };

        await Vm.OpenManageHolidaysCommand.Execute();
        await Vm.OpenManageUsersCommand.Execute();
        await Vm.OpenBackupRestoreCommand.Execute();
        await Vm.OpenSettingsCommand.Execute();

        Assert.Equal([typeof(ManageHolidaysViewModel), typeof(ManageUsersViewModel), typeof(BackupRestoreViewModel), typeof(SettingsViewModel)],
            shown);
    }

    [Fact]
    public async Task Backup_needs_a_connection_string()
    {
        var shell = new TestShell(connectionString: null);
        Answer(shell.ViewModel);
        var shown = false;
        _dialog = _ => shown = true;

        await shell.ViewModel.OpenBackupRestoreCommand.Execute();

        Assert.False(shown);
        Assert.Equal(["No database connection string is configured -- check Settings."], _notices);
    }

    /// <summary>Settings opened on what's in effect, its port changed and saved.</summary>
    private bool ChangePort(ReactiveViewModel dialog)
    {
        var settings = Assert.IsType<SettingsViewModel>(dialog);
        Assert.Equal(TestShell.ConnectionString, settings.ConnectionString);
        settings.PortText = "4380";
        return settings.SaveCommand.Execute().Wait();
    }

    [Fact]
    public async Task Saved_settings_are_confirmed_written_and_a_restart_offered()
    {
        _shell.ConfigWriter.Save(default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs(@"C:\ProgramData\ScheduleApp\shared.appsettings.json");
        _dialog = ChangePort;

        await Vm.OpenSettingsCommand.Execute();

        Assert.Equal(["Confirm shared change", "Saved"], _questions);
        _shell.ConfigWriter.Received(1).Save(null, new DeviceDefaults(null, 4380, 0, "Tcp"), null, null, null, null, null, null);
        Assert.Equal(1, _restarts);
    }

    [Fact]
    public async Task Declining_the_shared_change_writes_nothing()
    {
        _dialog = ChangePort;
        _confirm = title => title != "Confirm shared change";

        await Vm.OpenSettingsCommand.Execute();

        _shell.ConfigWriter.DidNotReceiveWithAnyArgs().Save();
        Assert.Equal(0, _restarts);
    }

    [Fact]
    public async Task A_write_the_account_isnt_allowed_is_explained()
    {
        _shell.ConfigWriter.Save(default, default, default, default, default, default, default, default)
            .ThrowsForAnyArgs(new UnauthorizedAccessException("Access to the path is denied."));
        _dialog = ChangePort;

        await Vm.OpenSettingsCommand.Execute();

        Assert.StartsWith("Could not save the shared config file -- access was denied.", _notices.Single(), StringComparison.Ordinal);
        Assert.Equal(0, _restarts);
    }

    [Fact]
    public async Task A_restart_can_wait()
    {
        _shell.ConfigWriter.Save(default, default, default, default, default, default, default, default)
            .ReturnsForAnyArgs("shared.json");
        _dialog = ChangePort;
        _confirm = title => title != "Saved";

        await Vm.OpenSettingsCommand.Execute();

        _shell.ConfigWriter.ReceivedWithAnyArgs(1).Save();
        Assert.Equal(0, _restarts);
    }
}
