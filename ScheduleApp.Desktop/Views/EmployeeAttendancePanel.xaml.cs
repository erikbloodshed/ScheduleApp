using System.Windows.Controls;

namespace ScheduleApp.Desktop.Views;

/// <summary>Slim, read-only attendance grid (PayrollViewModel.AttendanceRows) for whichever
/// employee/period is selected on the Payroll tab -- deliberately not the full
/// AttendanceView (which would nest a second tree/toolbar/tabs redundantly), just the
/// basis-of-the-numbers view sitting below PayrollSummaryView, scoped to one employee via
/// IAttendanceRunner's TargetPins rather than a whole-company run. DataContext is set
/// explicitly to a PayrollViewModel by PayrollPage's code-behind, not inherited or set here
/// -- see PayrollPage's own doc comment for why.</summary>
public partial class EmployeeAttendancePanel : UserControl
{
    public EmployeeAttendancePanel()
    {
        InitializeComponent();
    }
}
