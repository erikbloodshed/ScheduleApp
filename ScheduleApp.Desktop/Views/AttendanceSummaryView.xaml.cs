using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.Primitives.Disposables;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Enums;
using ScheduleApp.Desktop.Reactive;
using ScheduleApp.Desktop.ViewModels;
using Syncfusion.UI.Xaml.Grid;

namespace ScheduleApp.Desktop.Views;

/// <summary>The Attendance Summary page's content: the report-scope tree (ReportScopeViewModel)
/// on the left, and the period, status counts and summary grid (ReportViewModel) on the right.
/// AttendanceSummaryPage hands it the AttendanceViewModel.</summary>
public partial class AttendanceSummaryView
{
    public AttendanceSummaryView()
    {
        InitializeComponent();

        this.WhenActivated((MultipleDisposable d) =>
        {
            // Report scope
            this.Bind(ViewModel, vm => vm.ReportScope.SearchText, v => v.ScopeSearchBox.Text).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ReportScope.VisibleDepartments, v => v.ScopeTree.ItemsSource).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.ReportScope.SelectionScopeText, v => v.ScopeText.Text).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ReportScope.SelectAllTreeCommand, v => v.SelectAllButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ReportScope.ClearTreeSelectionCommand, v => v.ClearSelectionButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.ReportScope.LoadEmployeeTreeCommand, v => v.ReloadTreeButton).DisposeWith(d);

            // Period row
            this.BindCommand(ViewModel, vm => vm.Report.PreviousPeriodCommand, v => v.PreviousPeriodButton).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.Report.PeriodStart, v => v.PeriodStartPicker.DateTime).DisposeWith(d);
            this.Bind(ViewModel, vm => vm.Report.PeriodEnd, v => v.PeriodEndPicker.DateTime).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Report.NextPeriodCommand, v => v.NextPeriodButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Report.ExportSummaryCommand, v => v.ExportSummaryButton).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Report.RefreshOrCancelSummaryCommand, v => v.RefreshOrCancelButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.RefreshOrCancelGlyph, v => v.RefreshOrCancelButton.Tag).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.RefreshOrCancelToolTip, v => v.RefreshOrCancelButton.ToolTip).DisposeWith(d);

            // Counts strip -- once there's a report.
            this.OneWayBind(ViewModel, vm => vm.Report.HasResults, v => v.CountsCard.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.TotalLogs, v => v.TotalLogsText.Text, Count).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Report.ClearStatusFilterCommand, v => v.ClearFilterButton).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.SelectedStatusFilter, v => v.ClearFilterButton.Visibility,
                filter => VisibleWhen(filter is not null)).DisposeWith(d);

            // Each status tile narrows the grid to its own status (its CommandParameter), and
            // lights up while that's the active filter.
            this.OneWayBind(ViewModel, vm => vm.Report.ShowStatusDetailCommand, v => v.CompleteTile.Command).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.ShowStatusDetailCommand, v => v.PartialTile.Command).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.ShowStatusDetailCommand, v => v.AbsentTile.Command).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.ShowStatusDetailCommand, v => v.LeaveTile.Command).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.ShowStatusDetailCommand, v => v.OfficialBusinessTile.Command).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.ShowStatusDetailCommand, v => v.RestDayTile.Command).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.CompleteCount, v => v.CompleteTile.Content).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.PartialCount, v => v.PartialTile.Content).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.AbsentCount, v => v.AbsentTile.Content).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.LeaveCount, v => v.LeaveTile.Content).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.OfficialBusinessCount, v => v.OfficialBusinessTile.Content).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.RestDayCount, v => v.RestDayTile.Content).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.SelectedStatusFilter, v => v.CompleteTile.Background,
                filter => TileBackground(filter, PunchStatus.Complete)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.SelectedStatusFilter, v => v.PartialTile.Background,
                filter => TileBackground(filter, PunchStatus.Partial)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.SelectedStatusFilter, v => v.AbsentTile.Background,
                filter => TileBackground(filter, PunchStatus.Absent)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.SelectedStatusFilter, v => v.LeaveTile.Background,
                filter => TileBackground(filter, PunchStatus.Leave)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.SelectedStatusFilter, v => v.OfficialBusinessTile.Background,
                filter => TileBackground(filter, PunchStatus.OfficialBusiness)).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.SelectedStatusFilter, v => v.RestDayTile.Background,
                filter => TileBackground(filter, PunchStatus.RestDay)).DisposeWith(d);

            // Orphaned/Unscheduled aren't statuses -- raw punches with no matched shift -- so
            // they open a punch list instead of filtering.
            this.BindCommand(ViewModel, vm => vm.Report.ShowOrphanedDetailCommand, v => v.OrphanedTile).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.OrphanedCount, v => v.OrphanedCountText.Text, Count).DisposeWith(d);
            this.BindCommand(ViewModel, vm => vm.Report.ShowUnscheduledDetailCommand, v => v.UnscheduledTile).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.UnscheduledCount, v => v.UnscheduledCountText.Text, Count).DisposeWith(d);

            // The grid, or the hint until there's something in it.
            this.OneWayBind(ViewModel, vm => vm.Report.SummaryRowsView, v => v.SummaryGrid.ItemsSource).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.HasSummaryRows, v => v.SummaryGrid.Visibility, VisibleWhen).DisposeWith(d);
            this.OneWayBind(ViewModel, vm => vm.Report.HasSummaryRows, v => v.EmptyText.Visibility, hasRows => VisibleWhen(!hasRows)).DisposeWith(d);
        });
    }

    private static Visibility VisibleWhen(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;

    private static string Count(int count) => count.ToString(CultureInfo.CurrentCulture);

    private Brush TileBackground(PunchStatus? filter, PunchStatus tile) =>
        filter == tile && TryFindResource("CardBorderBrush") is Brush active ? active : Brushes.Transparent;

    /// <summary>Right-click on a Summary grid row -- the same "Add Manual Entry…" the Schedule
    /// page's calendar offers on a tile (see MonthCalendarControl.BuildDayContextMenu), for a
    /// Summary row. Built here because a XAML ContextMenu is a separate visual tree with no
    /// DataContext to inherit, so it can't reach both the clicked row and the ViewModel without
    /// a PlacementTarget/Tag tunnel. The grid's own PreviewMouseRightButtonDown, so it works for
    /// any row, selected or not; a click anywhere but a data row does nothing.
    ///
    /// "Add Manual Entry…" only for a Partial/Absent row (nothing else has a missing punch to
    /// fill in), plus a punch view/editor on every row: "Edit Punch Pairing…" for a Flexible
    /// row, "Edit Punches…" for a non-Flexible row that's Partial/Absent or has a hand-entered
    /// punch that may still need fixing, "View Punches…" (read-only) otherwise -- see
    /// DayPunchPairingEditorViewModel.IsReadOnly for how the dialog decides.</summary>
    private void SummaryGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(e.OriginalSource as DependencyObject) is not { DataContext: AttendanceSummaryRow row }) return;
        if (ViewModel?.Report is not { } report) return;

        var menu = new ContextMenu();

        if (row.Status is PunchStatus.Partial or PunchStatus.Absent)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "Add Manual Entry…",
                Command = report.AddManualEntryForRowCommand,
                CommandParameter = row,
            });
        }

        menu.Items.Add(new MenuItem
        {
            Header = row.TypeText == ScheduleType.Flexible.ToText() ? "Edit Punch Pairing…"
                : row.Status is PunchStatus.Partial or PunchStatus.Absent || row.HasManualClockPunch ? "Edit Punches…"
                : "View Punches…",
            Command = report.EditPunchPairingForRowCommand,
            CommandParameter = row,
        });

        menu.PlacementTarget = SummaryGrid;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>The grid row (VirtualizingCellsControl) the clicked element sits in, or null
    /// when the click wasn't on one.</summary>
    private static VirtualizingCellsControl? RowAt(DependencyObject? element)
    {
        while (element is not null and not VirtualizingCellsControl)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);

        return element as VirtualizingCellsControl;
    }
}
