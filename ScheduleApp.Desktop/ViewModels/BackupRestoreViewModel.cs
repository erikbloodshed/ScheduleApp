using System.Globalization;
using System.Reactive.Linq;
using Microsoft.Data.SqlClient;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using ScheduleApp.Desktop.Services;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Backup &amp; Restore: SQL Server's BACKUP/RESTORE DATABASE for this database (see
/// DatabaseBackupService for what each does server-side), with the file pickers and the
/// confirmation that make it safe without SSMS. Both start in SQL Server's own default backup
/// folder when it can be found -- the one folder its service account can always reach. Only
/// one runs at a time: a second BACKUP or RESTORE against the same database mustn't start.
/// </summary>
public partial class BackupRestoreViewModel : ReactiveViewModel
{
    private const string BackupFilter = "SQL Server backup (*.bak)|*.bak";

    private readonly IDatabaseBackupService _backupService;
    private readonly string _connectionString;
    private readonly Func<DateTime> _now;
    private readonly IObservable<bool> _idle;

    public BackupRestoreViewModel(IDatabaseBackupService backupService, string connectionString, Func<DateTime>? now = null)
    {
        _backupService = backupService;
        _connectionString = connectionString;
        _now = now ?? (() => DateTime.Now);
        _idle = this.WhenAnyValue(x => x.ProgressText).Select(text => text is null);
        _isBusyHelper = this.WhenAnyValue(x => x.ProgressText).Select(text => text is not null).ToProperty(this, x => x.IsBusy);
    }

    /// <summary>What's running ("Backing up…"), or null when nothing is.</summary>
    [Reactive]
    public partial string? ProgressText { get; private set; }

    [ObservableAsProperty]
    public partial bool IsBusy { get; }

    [ReactiveCommand(CanExecute = nameof(_idle))]
    private async Task BackupAsync()
    {
        var path = await PickFileToSaveAsync(BackupFilter,
            string.Create(CultureInfo.InvariantCulture, $"ScheduleAppDb_{_now():yyyyMMdd_HHmmss}.bak"), "Back Up ScheduleAppDb",
            await DefaultFolderAsync());
        if (path is null) return;

        await RunAsync("Backing up…", () => _backupService.BackupAsync(_connectionString, path),
            "Backup complete:\n" + path +
            "\n\nCopy this file to the other computer's SQL Server Express instance, then use Restore there to bring it in.",
            "Backup failed");
    }

    /// <summary>A one-way overwrite of the whole database, so it's confirmed with the
    /// consequence spelled out.</summary>
    [ReactiveCommand(CanExecute = nameof(_idle))]
    private async Task RestoreAsync()
    {
        var path = await PickFileToOpenAsync(BackupFilter, title: "Restore ScheduleAppDb", initialDirectory: await DefaultFolderAsync());
        if (path is null) return;

        if (!await ConfirmAsync(
                "This replaces everything currently in this database with what's in:\n\n" + path +
                "\n\nAny employees, schedules, or attendance history already here will be gone. This can't be undone.\n\nContinue?",
                "Confirm restore", isWarning: true))
            return;

        await RunAsync("Restoring…", () => _backupService.RestoreAsync(_connectionString, path),
            "Restore complete. Restart Schedule Manager (and the Push Listener service, if one's installed on this machine) " +
            "so both pick up the restored data.",
            "Restore failed");
    }

    /// <summary>SQL Server's own backup folder, or null to leave the picker where it
    /// is.</summary>
    private Task<string?> DefaultFolderAsync() => _backupService.TryGetDefaultBackupFolderAsync(_connectionString);

    private async Task RunAsync(string progress, Func<Task> operation, string success, string failureTitle)
    {
        ProgressText = progress;
        try
        {
            await operation();
            ProgressText = null;
            await NotifyAsync(success, "Done");
        }
        catch (SqlException ex)
        {
            ProgressText = null;
            await NotifyAsync(
                failureTitle + ":\n\n" + ex.Message +
                "\n\nCommon causes: the SQL Server service account doesn't have access to the chosen folder (its own " +
                "default backup folder, pre-filled above, always works), or the account this app is running as doesn't " +
                "have the needed SQL Server permission (db_backupoperator for Backup; dbcreator or sysadmin for Restore).",
                failureTitle, NoticeKind.Error);
        }
        catch (Exception ex)
        {
            ProgressText = null;
            await NotifyAsync(failureTitle + ":\n\n" + ex.Message, failureTitle, NoticeKind.Error);
        }
    }
}
