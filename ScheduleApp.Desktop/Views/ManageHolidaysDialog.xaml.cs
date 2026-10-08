using System.ComponentModel;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Opened from the navigation drawer's footer (see MainWindow.OpenDialog). Everything it does
/// -- listing, inline Add/Edit/Save, Delete -- lives on ManageHolidaysViewModel; what's left
/// here is the part only a view can do: keeping the selection on the row being edited,
/// routing Enter/Escape to Save/Cancel, putting the caret in the Name editor, and scrolling
/// a newly added row into view.
///
/// The list is a plain ListView (no GridView -- a star-sized Grid in the item template plus
/// a static header, see the XAML), not a grid: each row is a HolidayRow whose IsEditing flag
/// swaps the two cells between a read-only display element and an editor. Nothing but the
/// view model flips that flag, so there's no framework edit pipeline to auto-commit a row
/// past its validation -- the two Preview handlers below only keep the selection pinned to
/// the row being edited and route Enter/Escape to the validated Save/Cancel paths.
/// </summary>
public partial class ManageHolidaysDialog : Controls.AppWindow
{
    private readonly ManageHolidaysViewModel _viewModel;

    public ManageHolidaysDialog(IHolidayRepository holidayRepository, AttendanceDataVersion dataVersion)
    {
        InitializeComponent();

        _viewModel = new ManageHolidaysViewModel(holidayRepository, dataVersion);
        DataContext = _viewModel;
        MessageBoxInteractions.Register(_viewModel, this);

        // Add selects the row it appends; bring it into view.
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += async (_, _) => await _viewModel.LoadCommand.Execute();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ManageHolidaysViewModel.SelectedRow) && _viewModel.SelectedRow is { } row)
            HolidaysList.ScrollIntoView(row);
    }

    private void HolidaysList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => _viewModel.EditSelected();

    /// <summary>Blocks moving the selection to a different row while one is mid-edit. The row
    /// showing the editor and the row Save/Cancel act on (SelectedRow) have to stay the same
    /// one -- without this, clicking another row would move SelectedRow while the first row
    /// stayed stuck visually in edit mode. Clicks inside the row already being edited (its
    /// Name box, the date editor's calendar drop-down -- which lives in a separate popup and
    /// so isn't found under any ListViewItem) pass through untouched.</summary>
    private void HolidaysList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsEditingRow) return;
        if (e.OriginalSource is not DependencyObject source) return;

        var item = ItemsControl.ContainerFromElement(HolidaysList, source) as ListViewItem;
        if (item is not null && !ReferenceEquals(item.Content, _viewModel.SelectedRow))
            e.Handled = true;
    }

    /// <summary>While a row is mid-edit, Enter saves it through the same validated path the
    /// Save button uses and Escape cancels -- both are handled here so they don't instead
    /// reach the dialog's IsDefault/IsCancel "Close" button. Nothing is intercepted when no
    /// row is being edited, so Enter/Escape close the dialog as normal then. Run through
    /// ICommand, which executes them; a ReactiveCommand's own Execute only builds an
    /// observable.</summary>
    private void HolidaysList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_viewModel.IsEditingRow) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            ((ICommand)_viewModel.EditOrSaveCommand).Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ((ICommand)_viewModel.CancelEditCommand).Execute(null);
        }
    }

    /// <summary>Puts the caret in the Name editor the moment a row enters edit mode (its box
    /// goes from Collapsed to Visible), matching the focus behaviour the old DataGrid's
    /// BeginEdit gave for free.</summary>
    private void NameEditor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } editor)
            return;

        // Deferred to Input priority -- called straight from the visibility change, Focus()
        // can land before the box is fully realised and silently no-op.
        editor.Dispatcher.BeginInvoke(new Action(() =>
        {
            editor.Focus();
            editor.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }
}
