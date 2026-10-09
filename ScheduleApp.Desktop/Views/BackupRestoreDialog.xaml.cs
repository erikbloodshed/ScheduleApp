using System.Windows;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;

namespace ScheduleApp.Desktop.Views;

/// <summary>Backup &amp; Restore -- see <see cref="ViewModels.BackupRestoreViewModel"/>.</summary>
public partial class BackupRestoreDialog
{
    public BackupRestoreDialog()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            ViewInteractions.Register(ViewModel!, this).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.BackupCommand, v => v.BackupButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.RestoreCommand, v => v.RestoreButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ProgressText, v => v.ProgressText.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.IsBusy, v => v.ProgressPanel.Visibility,
                busy => busy ? Visibility.Visible : Visibility.Collapsed).DisposeWith(d);
        });
    }
}
