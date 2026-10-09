using System.ComponentModel;
using ScheduleApp.Core.Models;

namespace ScheduleApp.Desktop.ViewModels;

/// <summary>The employee selected app-wide -- one selection the Schedule, Employees, Attendance
/// and Payroll tabs share (MainViewModel's). What a tab's own ViewModels depend on when all they
/// need is to read or move that selection, rather than the whole MainViewModel.</summary>
public interface IEmployeeSelection : INotifyPropertyChanged
{
    Employee? SelectedEmployee { get; set; }
}
