using System.Windows;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;
using ScheduleApp.Desktop.Services;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Opened from the navigation drawer's footer (just above Settings) -- wraps
/// DatabaseBackupService's BACKUP DATABASE / RESTORE DATABASE calls with the file
/// pickers and confirmation this needs to be safe without SSMS or sqlcmd. See
/// DatabaseBackupService's own remarks for what each operation actually does
/// server-side and why (master connection, SINGLE_USER, MOVE targets, etc.) -- this
/// class only covers the UI: picking a .bak path, confirming Restore's one-way
/// overwrite, and keeping both buttons disabled while an operation's in flight so a
/// second click can't start a second BACKUP/RESTORE against the same database
/// concurrently.
/// </summary>
public partial class BackupRestoreDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly DatabaseBackupService _backupService;
    private readonly string _connectionString;

    public BackupRestoreDialog(DatabaseBackupService backupService, string connectionString)
    {
        InitializeComponent();
        _backupService = backupService;
        _connectionString = connectionString;
    }

    private async void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        // Best-effort pre-fill -- see TryGetDefaultBackupFolderAsync's own remarks for
        // why this folder in particular, and why a null here just means an unfilled
        // picker rather than a blocked Backup.
        var initialFolder = await _backupService.TryGetDefaultBackupFolderAsync(_connectionString);

        var dialog = new SaveFileDialog
        {
            Title = "Back Up ScheduleAppDb",
            Filter = "SQL Server backup (*.bak)|*.bak",
            FileName = $"ScheduleAppDb_{DateTime.Now:yyyyMMdd_HHmmss}.bak",
            InitialDirectory = initialFolder
        };

        if (dialog.ShowDialog(this) != true)
            return;

        await RunAsync(
            "Backing up…",
            () => _backupService.BackupAsync(_connectionString, dialog.FileName),
            "Backup complete:\n" + dialog.FileName +
            "\n\nCopy this file to the other computer's SQL Server Express instance, then use Restore there to bring it in.",
            "Backup failed");
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var initialFolder = await _backupService.TryGetDefaultBackupFolderAsync(_connectionString);

        var dialog = new OpenFileDialog
        {
            Title = "Restore ScheduleAppDb",
            Filter = "SQL Server backup (*.bak)|*.bak",
            InitialDirectory = initialFolder
        };

        if (dialog.ShowDialog(this) != true)
            return;

        // Restore is a one-way, whole-database overwrite -- worth a confirmation with
        // the actual consequence spelled out, the same way MainWindow's shared-settings
        // save asks before overwriting a file every account on the machine reads.
        var confirm = MessageBox.Show(
            "This replaces everything currently in this database with what's in:\n\n" + dialog.FileName +
            "\n\nAny employees, schedules, or attendance history already here will be gone. This can't be undone.\n\nContinue?",
            "Confirm restore", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        await RunAsync(
            "Restoring…",
            () => _backupService.RestoreAsync(_connectionString, dialog.FileName),
            "Restore complete. Restart Schedule Manager (and the Push Listener service, if one's installed on this machine) so both pick up the restored data.",
            "Restore failed");
    }

    /// <summary>Shared plumbing for both buttons: disables both while the operation
    /// runs, shows the progress row, and reports success/failure with a clear
    /// MessageBox -- same "show it, don't fail silently" treatment App.xaml.cs gives
    /// its own Migrate() call.</summary>
    private async Task RunAsync(string progressText, Func<Task> operation, string successMessage, string failureTitle)
    {
        BackupButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        ProgressText.Text = progressText;
        ProgressPanel.Visibility = Visibility.Visible;

        try
        {
            await operation();
            MessageBox.Show(successMessage, "Done", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (SqlException ex)
        {
            MessageBox.Show(
                failureTitle + ":\n\n" + ex.Message +
                "\n\nCommon causes: the SQL Server service account doesn't have access to the " +
                "chosen folder (its own default backup folder, pre-filled above, always works), " +
                "or the account this app is running as doesn't have the needed SQL Server " +
                "permission (db_backupoperator for Backup; dbcreator or sysadmin for Restore).",
                failureTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(failureTitle + ":\n\n" + ex.Message, failureTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BackupButton.IsEnabled = true;
            RestoreButton.IsEnabled = true;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
