using CommunityToolkit.Mvvm.ComponentModel;

namespace ScheduleApp.Desktop.ViewModels.Schedule;

/// <summary>Single "is the tree in multi-select mode" flag shared by
/// <c>ScheduleCalendarViewModel</c> and <c>ScheduleAssignmentViewModel</c> once the
/// Schedule/Employees/Payroll pages' shared <c>MainViewModel</c> is split into child
/// ViewModels (see MainViewModel-Split-Plan.md's "Judgment call: multi-select mode
/// placement"). Same shape as <c>AttendanceBusyState</c>/<c>AttendanceDataVersion</c>
/// (see ViewModels/Attendance/AttendanceSharedState.cs) -- a small class holding exactly
/// the one piece of state two-or-more sibling child ViewModels need to share, constructed
/// once by the facade and handed to whichever children need it, rather than giving those
/// children a back-reference to the facade itself.
///
/// Unlike AttendanceBusyState, this flag isn't tree-owned even though it looks that way at
/// first glance: neither EmployeeNodeViewModel, DepartmentGroupViewModel, nor
/// EmployeeTreeBuilder actually reads it -- the checkbox column's visibility is driven
/// straight from XAML. The two real consumers are ScheduleCalendarViewModel (which reads
/// it in RefreshScheduleForSelectedEmployeeAsync's multi-select branch and
/// RefreshCalendarAttendanceStatusesAsync's early-return) and ScheduleAssignmentViewModel
/// (which both reads it -- CanSetScheduleForSelection, CanClearScheduleForSelection, the
/// Set/Leave commands' branching -- and *writes* it back to false after a successful bulk
/// Set/Leave operation, relying on the real property setter firing OnIsMultiSelectModeChanged's
/// existing cascade). That live get-*and*-set need from two siblings, with a fan-out that
/// also has to reach a third sibling (EmployeeTreeViewModel's checked-employee selection),
/// is exactly the shape AttendanceBusyState/AttendanceDataVersion/ManualEntryEditorViewModel
/// already solve for in this codebase -- see this class's own file for that precedent.
///
/// Deliberately just the one [ObservableProperty] and nothing else: unlike
/// AttendanceBusyState, there's no cancellation token, no RunAsync wrapper, no derived
/// IsVisiblyRunning -- every reaction to a flip of this flag (MultiSelectButtonText,
/// CalendarHeaderText, the three affected commands' CanExecute, clearing the tree's
/// checked employees) is cross-cutting facade behavior that stays on MainViewModel's own
/// PropertyChanged relay (see the split plan's phase 6), not something this class does
/// itself.
///
/// No App.xaml.cs registration -- unlike AttendanceBusyState/AttendanceDataVersion, this
/// isn't shared with anything outside the Schedule page, so MainViewModel just `new`s one
/// up in its own constructor alongside the four child ViewModels, the same way it
/// constructs them.</summary>
public partial class MultiSelectModeState : ObservableObject
{
    [ObservableProperty]
    private bool isMultiSelectMode;
}
