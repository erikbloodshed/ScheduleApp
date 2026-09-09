using ScheduleApp.Core.Models;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>
/// Live search filter for the Department/Employee tree shared by the Schedule tab
/// (MainViewModel) and the Attendance tab's report-scope tree (ReportScopeViewModel) --
/// same tree shape, same EmployeeTreeBuilder output, so the search-matching rule lives
/// here once rather than being copy-pasted into both callers.
///
/// Matching mirrors PunchRecordsViewModel.ResolveMatchingPins: SearchText is
/// comma-separated, each term is matched against Employee ID (exact, numeric terms
/// only), first name, last name, or department name, and terms are OR'd together. A
/// department whose own name matches a term shows all of its employees, the same way
/// matching a department name surfaces every one of its employees on the Punch Records
/// tab.
/// </summary>
internal static class EmployeeTreeSearchFilter
{
    /// <summary>Recomputes IsVisible for every employee and department node against
    /// searchText, and force-expands (but never collapses) a department that still has a
    /// visible match, so a search never leaves a matching employee hidden behind a
    /// collapsed department. An empty searchText makes everything visible again but
    /// leaves IsExpanded exactly as the person left it. A blacklisted employee (see
    /// Employee.IsBlacklisted) is always excluded regardless of searchText or
    /// departmentNameMatches -- this has to be enforced here, not just once at tree-load
    /// time, since Apply recomputes every node's IsVisible from scratch on every
    /// keystroke and would otherwise silently un-hide a blacklisted employee the moment
    /// a search term (or their department's name) matched them.</summary>
    public static void Apply(IEnumerable<DepartmentGroupViewModel> departments, string searchText)
    {
        var terms = searchText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var department in departments)
        {
            bool departmentNameMatches = terms.Length > 0 &&
                terms.Any(t => department.Name.Contains(t, StringComparison.OrdinalIgnoreCase));

            bool anyEmployeeVisible = false;

            foreach (var node in department.Employees)
            {
                bool visible = !node.Employee.IsBlacklisted &&
                    (terms.Length == 0 || departmentNameMatches ||
                    terms.Any(t => EmployeeMatchesSearchTerm(node.Employee, t)));

                node.IsVisible = visible;
                if (visible) anyEmployeeVisible = true;
            }

            department.IsVisible = terms.Length == 0 || anyEmployeeVisible || departmentNameMatches;

            if (terms.Length > 0 && department.IsVisible)
                department.IsExpanded = true;
        }
    }

    /// <summary>True if term matches this employee's Employee ID (exact, numeric terms
    /// only), first name, or last name -- the same fields ResolveMatchingPins on
    /// PunchRecordsViewModel checks, minus department name, which Apply already checks
    /// once per department instead of once per employee. Internal (not private) so
    /// EmployeesPage's own "find an employee" search (see its code-behind's
    /// SearchEmployee) can reuse this exact per-employee matching rule rather than a
    /// second copy of it -- that page doesn't call Apply itself, since hiding a
    /// department there is the one thing its tree was built to avoid (see
    /// EmployeesPage.xaml's own DepartmentTree doc comment).</summary>
    internal static bool EmployeeMatchesSearchTerm(Employee employee, string term)
    {
        if (int.TryParse(term, out var id))
            return employee.Pin == id;

        return employee.FirstName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
               employee.LastName.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}
