using System.Reactive.Linq;
using System.Windows.Input;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Users;
using ScheduleApp.Desktop.Services;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Accounts;
using Xunit;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace ScheduleApp.Desktop.Tests;

public class NewPasswordViewModelsTests
{
    public NewPasswordViewModelsTests() => ReactiveTestSetup.EnsureInitialized();

    [Fact]
    public async Task Add_user_needs_a_free_name_and_a_good_password_typed_twice()
    {
        var vm = new AddUserViewModel(["maria"]);

        Assert.False(await vm.AcceptCommand.Execute());
        Assert.Equal("Enter an account name.", vm.ErrorMessage);

        vm.Username = " MARIA ";
        Assert.False(await vm.AcceptCommand.Execute());
        Assert.Equal("The account name \"MARIA\" is already taken.", vm.ErrorMessage);

        vm.Username = "jose";
        vm.Password = "short";
        vm.ConfirmPassword = "short";
        Assert.False(await vm.AcceptCommand.Execute());
        Assert.Equal("Password must be at least 8 characters.", vm.ErrorMessage);
        Assert.Equal("short", vm.ConfirmPassword);

        vm.Password = "long enough";
        vm.ConfirmPassword = "long enuff";
        Assert.False(await vm.AcceptCommand.Execute());
        Assert.Equal("Password and confirmation don't match.", vm.ErrorMessage);
        Assert.Empty(vm.ConfirmPassword);

        vm.ConfirmPassword = "long enough";
        vm.Username = " jose ";
        Assert.True(await vm.AcceptCommand.Execute());
        Assert.Equal("jose", vm.AcceptedUsername);
    }

    [Fact]
    public async Task Reset_password_names_the_account_and_checks_the_password()
    {
        var vm = new ResetPasswordViewModel("maria");
        Assert.Equal("New password for \"maria\"", vm.Prompt);

        vm.Password = vm.ConfirmPassword = "1234567";
        Assert.False(await vm.AcceptCommand.Execute());

        vm.Password = vm.ConfirmPassword = "12345678";
        Assert.True(await vm.AcceptCommand.Execute());
    }
}

public class ManageUsersViewModelTests
{
    private readonly IUserAccountRepository _repository = Substitute.For<IUserAccountRepository>();
    private readonly CurrentUserContext _currentUser = new();
    private readonly List<UserAccount> _accounts =
    [
        new() { Id = 1, Username = "maria", IsActive = true, CreatedAtUtc = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc) },
        new() { Id = 2, Username = "jose", IsActive = true },
        new() { Id = 3, Username = "old", IsActive = false },
    ];
    private readonly ManageUsersViewModel _vm;
    private readonly List<string> _notices = [];
    private bool _confirmAnswer = true;
    private Func<ReactiveViewModel, bool> _dialog = _ => false;

    public ManageUsersViewModelTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        _currentUser.Set(1, "maria");
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(_accounts.ToList()));
        _vm = new ManageUsersViewModel(_repository, _currentUser);
        _vm.Confirm.RegisterHandler(ctx => ctx.SetOutput(_confirmAnswer));
        _vm.Notify.RegisterHandler(ctx =>
        {
            _notices.Add(ctx.Input.Message);
            ctx.SetOutput(RxVoid.Default);
        });
        _vm.ShowDialog.RegisterHandler(ctx => ctx.SetOutput(_dialog(ctx.Input)));
    }

    private static bool CanExecute(ICommand command) => command.CanExecute(null);

    private UserAccountRow Row(string username) => _vm.Rows.Single(r => r.Username == username);

    [Fact]
    public async Task Lists_every_account()
    {
        await _vm.LoadCommand.Execute();

        Assert.Equal(["maria", "jose", "old"], _vm.Rows.Select(r => r.Username));
        Assert.Equal(["Active", "Active", "Inactive"], _vm.Rows.Select(r => r.StatusDisplay));
        Assert.Null(_vm.ErrorMessage);
        Assert.False(CanExecute(_vm.ResetPasswordCommand));
        Assert.False(CanExecute(_vm.DeleteCommand));
    }

    [Fact]
    public async Task Nobody_can_lock_themselves_or_everyone_out()
    {
        await _vm.LoadCommand.Execute();

        // Yourself: reset only.
        _vm.SelectedRow = Row("maria");
        Assert.True(CanExecute(_vm.ResetPasswordCommand));
        Assert.False(CanExecute(_vm.ToggleActiveCommand));
        Assert.False(CanExecute(_vm.DeleteCommand));
        Assert.Equal("Deactivate", _vm.ToggleActiveLabel);

        _vm.SelectedRow = Row("jose");
        Assert.True(CanExecute(_vm.ToggleActiveCommand));
        Assert.True(CanExecute(_vm.DeleteCommand));

        // An inactive account can always come back, or go.
        _vm.SelectedRow = Row("old");
        Assert.Equal("Activate", _vm.ToggleActiveLabel);
        Assert.True(CanExecute(_vm.ToggleActiveCommand));
        Assert.True(CanExecute(_vm.DeleteCommand));

        // The last active account, even someone else's, stays.
        _accounts[0].IsActive = false;
        _currentUser.Set(99, "admin");
        await _vm.LoadCommand.Execute();
        _vm.SelectedRow = Row("jose");
        Assert.False(CanExecute(_vm.ToggleActiveCommand));
        Assert.False(CanExecute(_vm.DeleteCommand));
    }

    [Fact]
    public async Task Adding_a_user_hashes_their_password()
    {
        await _vm.LoadCommand.Execute();
        _dialog = dialog =>
        {
            var editor = Assert.IsType<AddUserViewModel>(dialog);
            editor.Username = "ana";
            editor.Password = editor.ConfirmPassword = "s3cret-pass";
            return editor.AcceptCommand.Execute().Wait();
        };

        await _vm.AddUserCommand.Execute();

        await _repository.Received(1).AddAsync(
            Arg.Is<UserAccount>(a => a.Username == "ana" && PasswordHasher.Verify("s3cret-pass", a.PasswordHash, a.PasswordSalt)),
            Arg.Any<CancellationToken>());
        await _repository.Received(2).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_name_taken_meanwhile_is_shown_under_the_list()
    {
        await _vm.LoadCommand.Execute();
        _repository.AddAsync(Arg.Any<UserAccount>(), Arg.Any<CancellationToken>()).ThrowsAsync(new DuplicateUsernameException("ana"));
        _dialog = dialog =>
        {
            var editor = (AddUserViewModel)dialog;
            editor.Username = "ana";
            editor.Password = editor.ConfirmPassword = "s3cret-pass";
            return editor.AcceptCommand.Execute().Wait();
        };

        await _vm.AddUserCommand.Execute();

        Assert.Contains("ana", _vm.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resetting_a_password_says_so()
    {
        await _vm.LoadCommand.Execute();
        _vm.SelectedRow = Row("jose");
        _dialog = dialog =>
        {
            var editor = Assert.IsType<ResetPasswordViewModel>(dialog);
            Assert.Equal("New password for \"jose\"", editor.Prompt);
            editor.Password = editor.ConfirmPassword = "new-password";
            return editor.AcceptCommand.Execute().Wait();
        };

        await _vm.ResetPasswordCommand.Execute();

        await _repository.Received(1).UpdatePasswordAsync(2, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(["Password reset for \"jose\"."], _notices);
    }

    [Fact]
    public async Task Deactivating_and_deleting_are_confirmed_but_reactivating_isnt()
    {
        await _vm.LoadCommand.Execute();
        _vm.SelectedRow = Row("jose");

        _confirmAnswer = false;
        await _vm.ToggleActiveCommand.Execute();
        await _vm.DeleteCommand.Execute();
        await _repository.DidNotReceiveWithAnyArgs().SetActiveAsync(default, default);
        await _repository.DidNotReceiveWithAnyArgs().DeleteAsync(default);

        _confirmAnswer = true;
        await _vm.ToggleActiveCommand.Execute();
        await _repository.Received(1).SetActiveAsync(2, false, Arg.Any<CancellationToken>());

        _confirmAnswer = false;
        _vm.SelectedRow = Row("old");
        await _vm.ToggleActiveCommand.Execute();
        await _repository.Received(1).SetActiveAsync(3, true, Arg.Any<CancellationToken>());

        _confirmAnswer = true;
        _vm.SelectedRow = Row("old");
        await _vm.DeleteCommand.Execute();
        await _repository.Received(1).DeleteAsync(3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_load_is_shown_under_the_list()
    {
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("offline"));

        await _vm.LoadCommand.Execute();

        Assert.Equal("Could not load accounts.\n\noffline", _vm.ErrorMessage);
    }
}

public class SignInViewModelsTests
{
    private readonly IUserAccountRepository _repository = Substitute.For<IUserAccountRepository>();
    private readonly IRememberedSignInStore _remembered = Substitute.For<IRememberedSignInStore>();
    private readonly UserAccount _maria;

    public SignInViewModelsTests()
    {
        ReactiveTestSetup.EnsureInitialized();
        var (hash, salt) = PasswordHasher.Hash("correct horse");
        _maria = new UserAccount { Id = 1, Username = "maria", PasswordHash = hash, PasswordSalt = salt, IsActive = true };
        _repository.GetByUsernameAsync("maria", Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<UserAccount?>(_maria));
    }

    [Fact]
    public void A_remembered_sign_in_is_prefilled()
    {
        _remembered.TryLoad().Returns(("maria", "correct horse"));

        var vm = new SignInViewModel(_repository, _remembered);

        Assert.Equal("maria", vm.Username);
        Assert.Equal("correct horse", vm.Password);
        Assert.True(vm.RememberMe);
    }

    [Fact]
    public async Task Signing_in_checks_the_password_and_remembers_only_on_success()
    {
        var vm = new SignInViewModel(_repository, _remembered) { Username = "maria", Password = "wrong", RememberMe = true };

        Assert.Null(await vm.SignInCommand.Execute());
        Assert.Equal("Invalid account name or password.", vm.ErrorMessage);
        Assert.Empty(vm.Password);
        _remembered.DidNotReceiveWithAnyArgs().Save(default!, default!);
        _remembered.DidNotReceive().Clear();

        vm.Password = "correct horse";
        Assert.Equal(new AuthenticatedUser(1, "maria"), await vm.SignInCommand.Execute());
        _remembered.Received(1).Save("maria", "correct horse");
        Assert.Null(vm.ErrorMessage);

        vm.RememberMe = false;
        await vm.SignInCommand.Execute();
        _remembered.Received(1).Clear();
    }

    [Fact]
    public async Task A_deactivated_or_unknown_account_gets_the_same_answer()
    {
        var vm = new SignInViewModel(_repository, _remembered) { Username = "nobody", Password = "whatever1" };
        Assert.Null(await vm.SignInCommand.Execute());
        var unknown = vm.ErrorMessage;

        _maria.IsActive = false;
        vm.Username = "maria";
        vm.Password = "correct horse";
        Assert.Null(await vm.SignInCommand.Execute());

        Assert.Equal(unknown, vm.ErrorMessage);
    }

    [Fact]
    public async Task Signing_in_needs_both_fields_and_reports_an_unreachable_database()
    {
        var vm = new SignInViewModel(_repository, _remembered) { Username = "maria" };
        Assert.Null(await vm.SignInCommand.Execute());
        Assert.Equal("Enter an account name and password.", vm.ErrorMessage);

        _repository.GetByUsernameAsync("maria", Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("no server"));
        vm.Password = "correct horse";
        Assert.Null(await vm.SignInCommand.Execute());
        Assert.Equal("Could not reach the database to sign in.\n\nno server", vm.ErrorMessage);
    }

    [Fact]
    public async Task The_first_account_is_created_when_the_name_is_free()
    {
        _repository.AddAsync(Arg.Any<UserAccount>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new UserAccount { Id = 7, Username = call.Arg<UserAccount>().Username }));
        var vm = new SetupAdminViewModel(_repository) { Username = "maria", Password = "s3cret-pass", ConfirmPassword = "s3cret-pass" };

        Assert.Null(await vm.CreateCommand.Execute());
        Assert.Equal("The account name \"maria\" is already taken.", vm.ErrorMessage);

        vm.Username = " admin ";
        Assert.Equal(new AuthenticatedUser(7, "admin"), await vm.CreateCommand.Execute());
        await _repository.Received(1).AddAsync(
            Arg.Is<UserAccount>(a => a.Username == "admin" && PasswordHasher.Verify("s3cret-pass", a.PasswordHash, a.PasswordSalt)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_first_account_needs_a_good_password()
    {
        var vm = new SetupAdminViewModel(_repository) { Username = "admin", Password = "s3cret-pass", ConfirmPassword = "s3cret-pas" };

        Assert.Null(await vm.CreateCommand.Execute());
        Assert.Equal("Password and confirmation don't match.", vm.ErrorMessage);
        await _repository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }
}
