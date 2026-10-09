using ReactiveUI;
using ScheduleApp.Core.Models;
using ScheduleApp.Desktop.ViewModels;

namespace ScheduleApp.Desktop.Tests;

/// <summary>The app-wide employee selection, standing in for MainViewModel's -- recording every
/// assignment, so a test can see the order a ViewModel moved it in.</summary>
internal sealed class TestSelection : ReactiveObject, IEmployeeSelection
{
    private Employee? _selectedEmployee;

    public List<Employee?> Assignments { get; } = [];

    public Employee? SelectedEmployee
    {
        get => _selectedEmployee;
        set
        {
            Assignments.Add(value);
            if (ReferenceEquals(_selectedEmployee, value)) return;
            this.RaisePropertyChanging();
            _selectedEmployee = value;
            this.RaisePropertyChanged();
        }
    }
}
