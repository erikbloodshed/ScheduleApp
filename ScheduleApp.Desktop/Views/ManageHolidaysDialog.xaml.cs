using System.Globalization;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.ViewModels.Attendance;

namespace ScheduleApp.Desktop.Views;

/// <summary>
/// Opened from MainWindow's toolbar (see MainWindow.ManageHolidaysButton_Click) --
/// the only place holidays are added/edited/removed. Lists every Holiday and lets
/// the signed-in person add another, edit an existing one in place, or delete one.
///
/// Add, Edit and Delete all work against the list in place. Add appends a blank,
/// not-yet-saved HolidayRow (HolidayRow.NewRow / IsNew), drops it straight into
/// edit mode and, on Save, calls IHolidayRepository.AddAsync; Cancel/Escape removes
/// it again. Edit does the same for an existing row via UpdateAsync. An earlier
/// "blank new row" attempt that was reverted leaned on DataGrid's own
/// CanUserAddRows placeholder machinery; this one doesn't -- the new row is a plain
/// object appended to the list, edited through the same explicit IsEditing swap
/// every other row uses (see EditButton_Click/EnterEditMode/SaveInlineEditAsync
/// below and the cell DataTemplates in XAML).
///
/// The list is a plain ListView (no GridView -- a star-sized Grid in the item
/// template plus a static header, see the XAML), not a DataGrid: each row is a
/// HolidayRow whose IsEditing flag swaps the two cells between a read-only display
/// element and an editor. Nothing but this class's own EnterEditMode/CancelInlineEdit/
/// SaveInlineEditAsync flips that flag, so there's no framework edit pipeline to
/// auto-commit a row past SaveInlineEditAsync's validation -- the two Preview
/// handlers below only keep the selection pinned to the row being edited and route
/// Enter/Escape to the validated Save/Cancel paths.
/// </summary>
public partial class ManageHolidaysDialog : Wpf.Ui.Controls.FluentWindow
{
    private readonly IHolidayRepository _holidayRepository;

    /// <summary>Bumped after every successful Add/Edit/Delete below, so an already-open
    /// Payroll tab recomputes Holiday Pay on its next revisit -- see
    /// AttendanceDataVersion.HolidayVersion's own doc comment. The same bump
    /// ScheduleAssignmentViewModel.ToggleHolidayForSelectionAsync makes for the calendar
    /// right-click path; this dialog is the other writer.</summary>
    private readonly AttendanceDataVersion _dataVersion;

    private List<Holiday> _holidays = [];

    /// <summary>The ListView's ItemsSource, set once in the constructor and then only
    /// ever mutated in place: ReloadAsync rebuilds it from _holidays, AddButton_Click
    /// appends one unsaved row, CancelInlineEdit removes that row again if the add is
    /// abandoned.</summary>
    private readonly ObservableCollection<HolidayRow> _rows = [];

    /// <summary>True from StartInlineEdit until the edit either saves successfully
    /// (SaveInlineEditAsync's own ReloadAsync call rebuilds every row fresh, dropping
    /// this back to false) or is cancelled (Escape -- see CancelInlineEdit). Gates
    /// AddButton/DeleteButton -- adding or deleting a *different* row while this one's
    /// still open for edit would leave an ambiguous "which row do my pending edits
    /// belong to" situation -- and tells EditButton_Click which of its two jobs to
    /// do. Mirrors the edited row's own HolidayRow.IsEditing flag; kept as a separate
    /// dialog-level field so the button/keyboard logic doesn't have to reach through
    /// SelectedRow (which a stray selection change could move) to read it.</summary>
    private bool _isEditingRow;

    public ManageHolidaysDialog(IHolidayRepository holidayRepository, AttendanceDataVersion dataVersion)
    {
        InitializeComponent();
        _holidayRepository = holidayRepository;
        _dataVersion = dataVersion;

        HolidaysList.ItemsSource = _rows;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private HolidayRow? SelectedRow => HolidaysList.SelectedItem as HolidayRow;

    /// <summary>Null while the selected row is an unsaved new one (IsNew) -- Edit and
    /// Delete have nothing persisted to act on then, and both stay disabled anyway
    /// because _isEditingRow is true for the whole life of an inline add.</summary>
    private Holiday? SelectedHoliday => SelectedRow is { IsNew: false } row ? row.Holiday : null;

    private async Task ReloadAsync()
    {
        try
        {
            _holidays = await _holidayRepository.ListAsync();
        }
        catch (Exception ex)
        {
            ShowError("Could not load holidays.\n\n" + ex.Message);
            return;
        }

        HideError();
        // Always reached with no row mid-edit -- either this is the first load, or
        // it's SaveInlineEditAsync's own call after a successful save. Reset the flag
        // here anyway, defensively, so a future caller of ReloadAsync doesn't have to
        // remember that invariant too; the new HolidayRow list starts every row with
        // IsEditing = false regardless.
        _isEditingRow = false;
        _rows.Clear();
        foreach (var holiday in _holidays)
            _rows.Add(new HolidayRow(holiday));
        UpdateButtonStates();
    }

    private void HolidaysList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtonStates();

    private void UpdateButtonStates()
    {
        if (_isEditingRow)
        {
            AddButton.IsEnabled = false;
            DeleteButton.IsEnabled = false;
            EditButton.IsEnabled = true;
            return;
        }

        EditButton.Content = "Edit…";

        var hasSelection = SelectedHoliday is not null;
        AddButton.IsEnabled = true;
        EditButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
    }

    private void HolidaysList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_isEditingRow && SelectedHoliday is not null)
            StartInlineEdit();
    }

    /// <summary>Blocks moving the selection to a different row while one is mid-edit.
    /// The row showing the editor and the row SaveInlineEditAsync/CancelInlineEdit
    /// act on (SelectedRow) have to stay the same one -- without this, clicking
    /// another row would move SelectedRow while the first row stayed stuck visually
    /// in edit mode. Clicks inside the row already being edited (its Name TextBox,
    /// the DatePicker's calendar drop-down -- which lives in a separate popup and so
    /// isn't found under any ListViewItem) pass through untouched.</summary>
    private void HolidaysList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isEditingRow) return;
        if (e.OriginalSource is not DependencyObject source) return;

        var item = ItemsControl.ContainerFromElement(HolidaysList, source) as ListViewItem;
        if (item is not null && !ReferenceEquals(item.Content, SelectedRow))
            e.Handled = true;
    }

    /// <summary>While a row is mid-edit, Enter saves it through the same validated
    /// SaveInlineEditAsync path the Save button uses and Escape cancels -- both are
    /// handled here so they don't instead reach the dialog's IsDefault/IsCancel
    /// "Close" button. Nothing is intercepted when no row is being edited, so Enter/
    /// Escape close the dialog as normal then.</summary>
    private void HolidaysList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isEditingRow) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            EditButton_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelInlineEdit();
        }
    }

    /// <summary>Appends a blank, unsaved row and opens it for editing straight away --
    /// same inline editor every existing row uses, just with nothing persisted behind
    /// it yet (HolidayRow.IsNew). SaveInlineEditAsync routes it to AddAsync instead of
    /// UpdateAsync; CancelInlineEdit drops it back off the list. Disabled while
    /// another row is mid-edit (UpdateButtonStates), so there's only ever one unsaved
    /// row at a time.</summary>
    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isEditingRow)
            return;

        var row = HolidayRow.NewRow();
        EnterEditMode(row);
        _rows.Add(row);
        HolidaysList.SelectedItem = row;
        HolidaysList.ScrollIntoView(row);
    }

    /// <summary>Repurposed for two jobs, one per _isEditingRow state: starts inline
    /// editing of the selected row (Content "Edit…"), or validates and saves the
    /// row's pending EditDate/EditName (Content "Save", set when edit mode starts) --
    /// same single-button-does-both-jobs shape ManageUsersDialog's own
    /// Reset-or-nothing button doesn't use, but PayrollPage's Payroll Group
    /// load/reload button does. Also invoked directly by
    /// HolidaysList_PreviewKeyDown for Enter, not just a real button click. The Add
    /// case never lands here in the "start" branch -- AddButton_Click opens edit mode
    /// itself -- so this only ever starts editing an existing row.</summary>
    private async void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isEditingRow)
        {
            StartInlineEdit();
            return;
        }

        await SaveInlineEditAsync();
    }

    private void StartInlineEdit()
    {
        if (SelectedRow is not { IsNew: false } row)
            return;

        // Fresh copy of the persisted values every time editing starts -- without
        // this, a previous edit that was cancelled (Escape) would leave EditDate/
        // EditName holding that stale, never-saved attempt instead of what's actually
        // on file.
        row.EditDate = row.Holiday.Date.ToDateTime(TimeOnly.MinValue);
        row.EditName = row.Holiday.Name;

        EnterEditMode(row);
    }

    /// <summary>Shared tail of both edit entry points (StartInlineEdit for an existing
    /// row, AddButton_Click for a new one): mark the row and the dialog as editing
    /// and flip the button to Save.</summary>
    private void EnterEditMode(HolidayRow row)
    {
        _isEditingRow = true;
        row.IsEditing = true;
        EditButton.Content = "Save";
        UpdateButtonStates();
    }

    /// <summary>Escape while editing -- drops the row out of edit mode without saving.
    /// A new row that was never saved is removed from the list entirely; an existing
    /// row just reverts to its display cells (StartInlineEdit re-copies the persisted
    /// values next time, so nothing needs restoring here).</summary>
    private void CancelInlineEdit()
    {
        if (SelectedRow is { } row)
        {
            row.IsEditing = false;
            if (row.IsNew)
                _rows.Remove(row);
        }

        _isEditingRow = false;
        HideError();
        UpdateButtonStates();
    }

    private async Task SaveInlineEditAsync()
    {
        var row = SelectedRow;
        if (row is null)
            return;

        // The same three checks the old HolidayDialog.SaveButton_Click made for Add,
        // now the single validation path for both inline Add (row.IsNew) and inline
        // Edit.
        if (row.EditDate is not { } selectedDate)
        {
            ShowError("Pick a date.");
            return;
        }

        var name = row.EditName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Enter a name for the holiday.");
            return;
        }

        var date = DateOnly.FromDateTime(selectedDate);
        // For an existing row, exclude its own current date -- re-saving a holiday
        // under its own date isn't a self-conflict (mirrors
        // HolidayRepository.EnsureDateIsFreeAsync's excludingId). A new row has Id 0,
        // which no persisted holiday matches, so it's checked against every listed
        // date.
        if (_holidays.Any(h => h.Id != row.Holiday.Id && h.Date == date))
        {
            ShowError($"{date:MMMM d, yyyy} is already listed as a holiday.");
            return;
        }

        try
        {
            if (row.IsNew)
                await _holidayRepository.AddAsync(new Holiday { Date = date, Name = name });
            else
                await _holidayRepository.UpdateAsync(new Holiday { Id = row.Holiday.Id, Date = date, Name = name });
        }
        catch (DuplicateHolidayDateException ex)
        {
            // Only reachable via the check-then-act race the local check above can't
            // catch -- e.g. Manage Holidays open twice at once. Unlikely for a
            // single-operator desktop app, cheap to handle correctly anyway.
            ShowError(ex.Message);
            return;
        }
        catch (Exception ex)
        {
            ShowError((row.IsNew ? "Could not add the holiday.\n\n" : "Could not save the holiday.\n\n") + ex.Message);
            return;
        }

        _dataVersion.BumpHoliday();

        // Drop the row out of edit mode now that the save the person asked for has
        // actually succeeded, not before. ReloadAsync right after rebuilds the row
        // list from the database anyway (a new row is replaced by its persisted
        // self), but clearing the flag here keeps it and the visible state in step
        // across the await.
        row.IsEditing = false;
        await ReloadAsync();
    }

    private async void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedHoliday;
        if (selected is null)
            return;

        var confirm = MessageBox.Show(
            $"Delete \"{selected.Name}\" ({selected.Date:MMMM d, yyyy})? This can't be undone.",
            "Confirm delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            await _holidayRepository.DeleteAsync(selected.Id);
        }
        catch (Exception ex)
        {
            ShowError("Could not delete the holiday.\n\n" + ex.Message);
            return;
        }

        _dataVersion.BumpHoliday();
        await ReloadAsync();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;

    /// <summary>Puts the caret in the Name editor the moment a row enters edit mode
    /// (its TextBox goes from Collapsed to Visible), matching the focus behaviour the
    /// old DataGrid's BeginEdit gave for free.</summary>
    private void NameEditor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox { IsVisible: true } editor)
            return;

        // Deferred to Input priority -- called straight from the visibility change,
        // Focus() can land before the TextBox is fully realised and silently no-op.
        editor.Dispatcher.BeginInvoke(new Action(() =>
        {
            editor.Focus();
            editor.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Row for HolidaysList -- wraps a Holiday with a formatted DateDisplay
    /// (rather than a value converter, since only this one dialog needs it) plus a
    /// pair of editable, bindable EditDate/EditName properties the cells' editor
    /// templates write into live (UpdateSourceTrigger=PropertyChanged). DateDisplay/
    /// Name themselves stay read-only, reflecting only the last *saved* value, so the
    /// display side of a row never shows a half-typed in-progress edit. IsEditing is
    /// the swap: the cell templates show the editor while it's true, the display
    /// element while it's false, and only ManageHolidaysDialog flips it.
    ///
    /// A row built by NewRow() is one the inline Add is still filling in: its Holiday
    /// has Id 0 (IsNew), and SaveInlineEditAsync sends it to AddAsync rather than
    /// UpdateAsync. Its DateDisplay/Name are never seen -- it's created already in
    /// edit mode -- so the placeholder Holiday behind it only exists to keep those
    /// getters and the Id-based duplicate check non-null.</summary>
    private sealed partial class HolidayRow : ObservableObject
    {
        public Holiday Holiday { get; }
        public string DateDisplay => Holiday.Date.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture);
        public string Name => Holiday.Name;

        /// <summary>Persisted holidays always have a positive identity; Id 0 marks the
        /// one row the inline Add hasn't saved yet.</summary>
        public bool IsNew => Holiday.Id == 0;

        [ObservableProperty]
        private bool isEditing;

        [ObservableProperty]
        private DateTime? editDate;

        [ObservableProperty]
        private string editName;

        public HolidayRow(Holiday holiday)
        {
            Holiday = holiday;
            editDate = holiday.Date.ToDateTime(TimeOnly.MinValue);
            editName = holiday.Name;
        }

        /// <summary>A not-yet-saved row for inline Add. EditDate starts null so the
        /// person has to pick a date explicitly -- the same no-default stance the old
        /// HolidayDialog took, since a holiday being added is as likely to be past as
        /// future.</summary>
        public static HolidayRow NewRow() => new(new Holiday
        {
            Date = DateOnly.FromDateTime(DateTime.Today),
            Name = string.Empty,
        })
        {
            EditDate = null,
            EditName = string.Empty,
        };
    }
}
