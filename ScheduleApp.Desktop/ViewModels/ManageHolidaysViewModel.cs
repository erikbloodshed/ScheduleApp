using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Binding;
using ReactiveUI.SourceGenerators;
using ScheduleApp.Core.Exceptions;
using ScheduleApp.Core.Models;
using ScheduleApp.Data.Repositories;
using ScheduleApp.Desktop.ViewModels.Attendance;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Backs ManageHolidaysDialog, opened from the navigation drawer's footer -- the only place
/// holidays are added/edited/removed. Lists every Holiday and lets the signed-in person add
/// another, edit an existing one in place, or delete one.
///
/// Add, Edit and Delete all work against the list in place. Add appends a blank,
/// not-yet-saved HolidayRow (HolidayRow.NewRow / IsNew), drops it straight into edit mode
/// and, on Save, calls IHolidayRepository.AddAsync; Cancel/Escape removes it again. Edit
/// does the same for an existing row via UpdateAsync. The new row is a plain object
/// appended to the list, edited through the same explicit IsEditing swap every other row
/// uses -- nothing but this class flips that flag, so there's no framework edit pipeline to
/// auto-commit a row past SaveInlineEditAsync's validation.
/// </summary>
public partial class ManageHolidaysViewModel : ReactiveViewModel
{
    private readonly IHolidayRepository _holidayRepository;

    /// <summary>Bumped after every successful Add/Edit/Delete below, so an already-open
    /// Payroll tab recomputes Holiday Pay on its next revisit -- see
    /// AttendanceDataVersion.HolidayVersion's own doc comment. The same bump
    /// ScheduleAssignmentViewModel.ToggleHolidayForSelectionAsync makes for the calendar
    /// right-click path; this dialog is the other writer.</summary>
    private readonly AttendanceDataVersion _dataVersion;

    private readonly IObservable<bool> _canAdd;
    private readonly IObservable<bool> _canEditOrSave;
    private readonly IObservable<bool> _canDelete;

    private List<Holiday> _holidays = [];

    public ManageHolidaysViewModel(IHolidayRepository holidayRepository, AttendanceDataVersion dataVersion)
    {
        _holidayRepository = holidayRepository;
        _dataVersion = dataVersion;

        _editButtonTextHelper = this.WhenAnyValue(x => x.IsEditingRow)
            .Select(isEditing => isEditing ? "Save" : "Edit…")
            .ToProperty(this, x => x.EditButtonText);

        // Edit and Delete need a persisted row: not the one an inline Add is still filling in
        // (see SelectedHoliday).
        _canAdd = this.WhenAnyValue(x => x.IsEditingRow).Select(isEditing => !isEditing);
        _canEditOrSave = this.WhenAnyValue(x => x.IsEditingRow, x => x.SelectedRow,
            (isEditing, row) => isEditing || row is { IsNew: false });
        _canDelete = this.WhenAnyValue(x => x.IsEditingRow, x => x.SelectedRow,
            (isEditing, row) => !isEditing && row is { IsNew: false });
    }

    /// <summary>The list's items: LoadAsync rebuilds it from _holidays, Add appends one
    /// unsaved row, CancelEdit removes that row again if the add is abandoned.</summary>
    public ObservableCollection<HolidayRow> Rows { get; } = [];

    [Reactive]
    public partial HolidayRow? SelectedRow { get; set; }

    /// <summary>Null while the selected row is an unsaved new one (IsNew) -- Edit and
    /// Delete have nothing persisted to act on then, and both stay disabled anyway because
    /// IsEditingRow is true for the whole life of an inline add.</summary>
    private Holiday? SelectedHoliday => SelectedRow is { IsNew: false } row ? row.Holiday : null;

    /// <summary>True from StartInlineEdit until the edit either saves successfully
    /// (SaveInlineEditAsync's own LoadAsync call rebuilds every row fresh, dropping this
    /// back to false) or is cancelled (Escape -- see CancelEdit). Gates Add/Delete --
    /// adding or deleting a *different* row while this one's still open for edit would leave
    /// an ambiguous "which row do my pending edits belong to" situation -- and tells
    /// EditOrSaveCommand which of its two jobs to do. Mirrors the edited row's own
    /// HolidayRow.IsEditing flag; kept separately so the command logic doesn't have to
    /// reach through SelectedRow (which a stray selection change could move) to read
    /// it.</summary>
    [Reactive]
    public partial bool IsEditingRow { get; private set; }

    /// <summary>The Edit button's two jobs, one per IsEditingRow state -- see
    /// EditOrSaveAsync.</summary>
    [ObservableAsProperty]
    public partial string EditButtonText { get; }

    /// <summary>The one inline error line under the list -- a failed load/save/delete or a
    /// validation message. Null hides it.</summary>
    [Reactive]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Lists every holiday: the dialog's first load (LoadCommand), and again after
    /// each successful save or delete.</summary>
    [ReactiveCommand]
    private async Task LoadAsync()
    {
        try
        {
            _holidays = await _holidayRepository.ListAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = "Could not load holidays.\n\n" + ex.Message;
            return;
        }

        ErrorMessage = null;
        // Always reached with no row mid-edit -- either this is the first load, or it's
        // SaveInlineEditAsync's own call after a successful save. Reset the flag here
        // anyway, defensively, so a future caller doesn't have to remember that invariant
        // too; the new HolidayRow list starts every row with IsEditing = false regardless.
        IsEditingRow = false;
        Rows.Clear();
        foreach (var holiday in _holidays)
            Rows.Add(new HolidayRow(holiday));
    }

    /// <summary>Double-clicking a row: the same as Edit, when nothing is being edited.</summary>
    public void EditSelected()
    {
        if (!IsEditingRow && SelectedHoliday is not null)
            StartInlineEdit();
    }

    /// <summary>Appends a blank, unsaved row and opens it for editing straight away -- same
    /// inline editor every existing row uses, just with nothing persisted behind it yet
    /// (HolidayRow.IsNew). SaveInlineEditAsync routes it to AddAsync instead of UpdateAsync;
    /// CancelEdit drops it back off the list. Disabled while another row is mid-edit,
    /// so there's only ever one unsaved row at a time.</summary>
    [ReactiveCommand(CanExecute = nameof(_canAdd))]
    private void Add()
    {
        if (IsEditingRow)
            return;

        var row = HolidayRow.NewRow();
        EnterEditMode(row);
        Rows.Add(row);
        SelectedRow = row;
    }

    /// <summary>Starts inline editing of the selected row ("Edit…"), or validates and saves
    /// the row's pending EditDate/EditName ("Save", once edit mode has started). Also what
    /// Enter does while a row is being edited. Add opens edit mode itself, so the "start"
    /// branch only ever starts editing an existing row.</summary>
    [ReactiveCommand(CanExecute = nameof(_canEditOrSave))]
    private async Task EditOrSaveAsync()
    {
        if (!IsEditingRow)
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

        // Fresh copy of the persisted values every time editing starts -- without this, a
        // previous edit that was cancelled (Escape) would leave EditDate/EditName holding
        // that stale, never-saved attempt instead of what's actually on file.
        row.EditDate = row.Holiday.Date.ToDateTime(TimeOnly.MinValue);
        row.EditName = row.Holiday.Name;

        EnterEditMode(row);
    }

    /// <summary>Shared tail of both edit entry points (StartInlineEdit for an existing row,
    /// Add for a new one): mark the row and the dialog as editing.</summary>
    private void EnterEditMode(HolidayRow row)
    {
        row.IsEditing = true;
        IsEditingRow = true;
    }

    /// <summary>Drops the row out of edit mode without saving (Escape while editing). A new
    /// row that was never saved is removed from the list entirely; an existing row just
    /// reverts to its display cells (StartInlineEdit re-copies the persisted values next
    /// time, so nothing needs restoring here).</summary>
    [ReactiveCommand]
    private void CancelEdit()
    {
        if (SelectedRow is { } row)
        {
            row.IsEditing = false;
            if (row.IsNew)
                Rows.Remove(row);
        }

        IsEditingRow = false;
        ErrorMessage = null;
    }

    private async Task SaveInlineEditAsync()
    {
        var row = SelectedRow;
        if (row is null)
            return;

        // The same three checks the old HolidayDialog.SaveButton_Click made for Add, now
        // the single validation path for both inline Add (row.IsNew) and inline Edit.
        if (row.EditDate is not { } selectedDate)
        {
            ErrorMessage = "Pick a date.";
            return;
        }

        var name = row.EditName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorMessage = "Enter a name for the holiday.";
            return;
        }

        var date = DateOnly.FromDateTime(selectedDate);
        // For an existing row, exclude its own current date -- re-saving a holiday under
        // its own date isn't a self-conflict (mirrors HolidayRepository.EnsureDateIsFreeAsync's
        // excludingId). A new row has Id 0, which no persisted holiday matches, so it's
        // checked against every listed date.
        if (_holidays.Any(h => h.Id != row.Holiday.Id && h.Date == date))
        {
            ErrorMessage = $"{date:MMMM d, yyyy} is already listed as a holiday.";
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
            // Only reachable via the check-then-act race the local check above can't catch
            // -- e.g. Manage Holidays open twice at once. Unlikely for a single-operator
            // desktop app, cheap to handle correctly anyway.
            ErrorMessage = ex.Message;
            return;
        }
        catch (Exception ex)
        {
            ErrorMessage = (row.IsNew ? "Could not add the holiday.\n\n" : "Could not save the holiday.\n\n") + ex.Message;
            return;
        }

        _dataVersion.BumpHoliday();

        // Drop the row out of edit mode now that the save the person asked for has
        // actually succeeded, not before. LoadAsync right after rebuilds the row list
        // from the database anyway (a new row is replaced by its persisted self), but
        // clearing the flag here keeps it and the visible state in step across the await.
        row.IsEditing = false;
        await LoadAsync();
    }

    [ReactiveCommand(CanExecute = nameof(_canDelete))]
    private async Task DeleteAsync()
    {
        var selected = SelectedHoliday;
        if (selected is null)
            return;

        if (!await ConfirmAsync(
                $"Delete \"{selected.Name}\" ({selected.Date:MMMM d, yyyy})? This can't be undone.",
                "Confirm delete", isWarning: true))
            return;

        try
        {
            await _holidayRepository.DeleteAsync(selected.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = "Could not delete the holiday.\n\n" + ex.Message;
            return;
        }

        _dataVersion.BumpHoliday();
        await LoadAsync();
    }
}

/// <summary>A row of the Manage Holidays list -- wraps a Holiday with a formatted DateDisplay
/// (rather than a value converter, since only this one dialog needs it) plus a pair of
/// editable, bindable EditDate/EditName properties the cells' editor templates write into
/// live. DateDisplay/Name themselves stay read-only, reflecting only the last *saved* value,
/// so the display side of a row never shows a half-typed in-progress edit. IsEditing is the
/// swap: the cell templates show the editor while it's true, the display element while it's
/// false, and only ManageHolidaysViewModel flips it.
///
/// A row built by NewRow() is one the inline Add is still filling in: its Holiday has Id 0
/// (IsNew), and SaveInlineEditAsync sends it to AddAsync rather than UpdateAsync. Its
/// DateDisplay/Name are never seen -- it's created already in edit mode -- so the placeholder
/// Holiday behind it only exists to keep those getters and the Id-based duplicate check
/// non-null.</summary>
public sealed partial class HolidayRow(Holiday holiday) : ReactiveObject
{
    public Holiday Holiday { get; } = holiday;

    public string DateDisplay => Holiday.Date.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture);

    public string Name => Holiday.Name;

    /// <summary>Persisted holidays always have a positive identity; Id 0 marks the one row
    /// the inline Add hasn't saved yet.</summary>
    public bool IsNew => Holiday.Id == 0;

    [Reactive]
    public partial bool IsEditing { get; set; }

    [Reactive]
    public partial DateTime? EditDate { get; set; } = holiday.Date.ToDateTime(TimeOnly.MinValue);

    [Reactive]
    public partial string EditName { get; set; } = holiday.Name;

    /// <summary>A not-yet-saved row for inline Add. EditDate starts null so the person has to
    /// pick a date explicitly -- the same no-default stance the old HolidayDialog took, since
    /// a holiday being added is as likely to be past as future.</summary>
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
