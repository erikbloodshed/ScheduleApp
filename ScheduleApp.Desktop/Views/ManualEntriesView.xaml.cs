using System.Globalization;
using System.Windows;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Desktop.Reactive;

namespace ScheduleApp.Desktop.Views;

/// <summary>The Manual Entries page's content: the range toolbar, Add/Export/Import, and the
/// grid of hand-typed entries (see ManualEntriesViewModel), whose rows' Edit/Delete go to the
/// shared ManualEntryEditorViewModel. ManualEntriesPage hands it the AttendanceViewModel.</summary>
public partial class ManualEntriesView
{
    public ManualEntriesView()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            this.BindCommand(ViewModel, vm => vm.ManualEntriesTab.RefreshOrCancelManualEntriesCommand, v => v.RefreshOrCancelButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ManualEntriesTab.RefreshOrCancelGlyph, v => v.RefreshOrCancelButton.Tag).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ManualEntriesTab.RefreshOrCancelToolTip, v => v.RefreshOrCancelButton.ToolTip).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.ManualEntriesTab.PreviousPeriodCommand, v => v.PreviousPeriodButton).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.ManualEntriesTab.ManualEntriesStart, v => v.StartPicker.DateTime).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.ManualEntriesTab.ManualEntriesEnd, v => v.EndPicker.DateTime).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ManualEntriesTab.NextPeriodCommand, v => v.NextPeriodButton).DisposeWith(d);

            this.BindCommand(ViewModel, vm => vm.ManualEntryEditor.AddManualEntryCommand, v => v.AddButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ManualEntriesTab.ExportManualEntriesCommand, v => v.ExportButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ManualEntriesTab.ImportManualEntriesCommand, v => v.ImportButton).DisposeWith(d);

            // The count line once something has loaded; the hint until then.
            this.OneWayBind(ViewModel, vm => vm.ManualEntriesTab.ManualEntriesCount, v => v.CountRun.Text, count => count.ToString(CultureInfo.CurrentCulture)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ManualEntriesTab.HasLoadedManualEntries, v => v.CountLine.Visibility,
                loaded => loaded ? Visibility.Visible : Visibility.Collapsed).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ManualEntriesTab.HasLoadedManualEntries, v => v.EmptyText.Visibility,
                loaded => loaded ? Visibility.Collapsed : Visibility.Visible).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ManualEntriesTab.ManualEntries, v => v.EntriesGrid.ItemsSource).DisposeWith(d);
        });
    }
}
