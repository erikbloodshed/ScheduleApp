namespace ScheduleApp.Core.Models;

/// <summary>
/// A department, e.g. "Back Office", "Production". Each department maps to
/// one worksheet tab when a schedule is exported to Excel.
/// </summary>
public class Department
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Display order of the worksheet tab on export.</summary>
    public int SortOrder { get; set; }

    public ICollection<Employee> Employees { get; set; } = new List<Employee>();
}
