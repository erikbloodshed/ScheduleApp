using System.Globalization;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Users;
using ScheduleApp.Desktop.Services;

namespace ScheduleApp.Desktop.ViewModels.Accounts;

/// <summary>One account in Manage Users' list.</summary>
public sealed record UserAccountRow(UserAccount Account)
{
    public string Username => Account.Username;
    public string CreatedDisplay => Account.CreatedAtUtc.ToLocalTime().ToString("MM/dd/yyyy", CultureInfo.CurrentCulture);
    public string StatusDisplay => Account.IsActive ? "Active" : "Inactive";
}

/// <summary>
/// Manage Users: every account that can sign in, and adding one, resetting a password, and
/// deactivating, reactivating or deleting one -- the only place accounts are added after the
/// first-run setup creates the first.
///
/// Two guard rails on top of what the repository guarantees: you can't deactivate or delete
/// the account you're signed in as, nor the last *active* account -- either would lock this
/// session out mid-use or leave nobody able to sign in next time, and there's no password
/// recovery. An inactive account can be deleted freely; it already can't sign in.
/// </summary>
public partial class ManageUsersViewModel : ReactiveViewModel
{
    private readonly IUserAccountRepository _repository;
    private readonly CurrentUserContext _currentUser;

    private readonly IObservable<bool> _hasSelection;
    private readonly IObservable<bool> _canToggleActive;
    private readonly IObservable<bool> _canDelete;

    public ManageUsersViewModel(IUserAccountRepository repository, CurrentUserContext currentUser)
    {
        _repository = repository;
        _currentUser = currentUser;

        var selection = this.WhenAnyValue(x => x.SelectedRow, x => x.Accounts,
            (row, accounts) => (Account: row?.Account, ActiveCount: accounts.Count(a => a.IsActive)));

        _hasSelection = selection.Select(s => s.Account is not null);

        // Deactivating an inactive account never changes how many are active.
        _canToggleActive = selection.Select(s => s.Account is { } account
            && (!account.IsActive || !IsSelf(account) && s.ActiveCount > 1));
        _canDelete = selection.Select(s => s.Account is { } account
            && !IsSelf(account) && !(account.IsActive && s.ActiveCount <= 1));

        _toggleActiveLabelHelper = this.WhenAnyValue(x => x.SelectedRow)
            .Select(row => row?.Account.IsActive == false ? "Activate" : "Deactivate")
            .ToProperty(this, x => x.ToggleActiveLabel);
    }

    /// <summary>Every account, as last loaded.</summary>
    [Reactive]
    public partial IReadOnlyList<UserAccount> Accounts { get; private set; } = [];

    public RangeObservableCollection<UserAccountRow> Rows { get; } = [];

    [Reactive]
    public partial UserAccountRow? SelectedRow { get; set; }

    /// <summary>What last failed, shown under the list; cleared by the next success.</summary>
    [Reactive]
    public partial string? ErrorMessage { get; private set; }

    [ObservableAsProperty(InitialValue = "Deactivate")]
    public partial string ToggleActiveLabel { get; }

    private bool IsSelf(UserAccount account) => account.Id == _currentUser.UserId;

    [ReactiveCommand]
    private async Task LoadAsync()
    {
        List<UserAccount> accounts;
        try
        {
            accounts = await _repository.GetAllAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = "Could not load accounts.\n\n" + ex.Message;
            return;
        }

        ErrorMessage = null;
        SelectedRow = null;
        Accounts = accounts;
        Rows.ReplaceAll(accounts.Select(a => new UserAccountRow(a)));
    }

    [ReactiveCommand]
    private async Task AddUserAsync()
    {
        var editor = new AddUserViewModel([.. Accounts.Select(a => a.Username)]);
        if (!await ShowDialogAsync(editor)) return;

        if (await TryAsync("Could not add the account.", () =>
            {
                var (hash, salt) = PasswordHasher.Hash(editor.Password);
                return _repository.AddAsync(new UserAccount { Username = editor.AcceptedUsername, PasswordHash = hash, PasswordSalt = salt });
            }))
            await LoadAsync();
    }

    [ReactiveCommand(CanExecute = nameof(_hasSelection))]
    private async Task ResetPasswordAsync()
    {
        if (SelectedRow?.Account is not { } account) return;

        var editor = new ResetPasswordViewModel(account.Username);
        if (!await ShowDialogAsync(editor)) return;

        if (!await TryAsync("Could not reset the password.", () =>
            {
                var (hash, salt) = PasswordHasher.Hash(editor.Password);
                return _repository.UpdatePasswordAsync(account.Id, hash, salt);
            }))
            return;

        ErrorMessage = null;
        await NotifyAsync($"Password reset for \"{account.Username}\".", "Done");
    }

    [ReactiveCommand(CanExecute = nameof(_canToggleActive))]
    private async Task ToggleActiveAsync()
    {
        if (SelectedRow?.Account is not { } account) return;

        var makeActive = !account.IsActive;
        if (!makeActive && !await ConfirmAsync(
                $"Deactivate \"{account.Username}\"? That account won't be able to sign in until it's reactivated here.",
                "Confirm deactivate", isWarning: true))
            return;

        if (await TryAsync("Could not update the account.", () => _repository.SetActiveAsync(account.Id, makeActive)))
            await LoadAsync();
    }

    [ReactiveCommand(CanExecute = nameof(_canDelete))]
    private async Task DeleteAsync()
    {
        if (SelectedRow?.Account is not { } account) return;

        if (!await ConfirmAsync($"Delete \"{account.Username}\"? This can't be undone.", "Confirm delete", isWarning: true))
            return;

        if (await TryAsync("Could not delete the account.", () => _repository.DeleteAsync(account.Id)))
            await LoadAsync();
    }

    /// <summary>Runs a write, showing what went wrong under the list if it fails. A duplicate
    /// name -- only reachable through the race Add User's own check can't catch, such as
    /// Manage Users open twice -- says so plainly.</summary>
    private async Task<bool> TryAsync(string failure, Func<Task> write)
    {
        try
        {
            await write();
            return true;
        }
        catch (DuplicateUsernameException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = failure + "\n\n" + ex.Message;
        }

        return false;
    }
}
