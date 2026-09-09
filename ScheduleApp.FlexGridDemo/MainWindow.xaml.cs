using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using ScheduleApp.Desktop.Controls.FlexGrid;

namespace ScheduleApp.FlexGridDemo;

/// <summary>
/// Showcase for <see cref="FlexDataGrid"/> (ScheduleApp.Desktop/Controls/FlexGrid) -- this
/// project's whole purpose. Two tabs: "Bound" exercises sorting (click a header), resizing
/// (drag a header's right edge), selection (click/ctrl-click/shift-click/arrow keys) and
/// in-cell editing (double-click or F2, Enter/Tab to commit, Escape to cancel) against an
/// ItemsSource; "Unbound" exercises the same grid with no ItemsSource at all -- RowCount,
/// AddRow/InsertRow/RemoveRow, and the Grid[row, col] indexer -- FlexGrid's own unbound mode.
/// </summary>
public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly ObservableCollection<SampleEmployee> _employees;

    public MainWindow()
    {
        InitializeComponent();

        _employees = BuildSampleData();

        Grid.Columns.Add(new FlexGridColumn { Header = "ID", BindingPath = nameof(SampleEmployee.Id), Width = 60, IsReadOnly = true });
        Grid.Columns.Add(new FlexGridColumn { Header = "Name", BindingPath = nameof(SampleEmployee.Name), Width = 160 });
        Grid.Columns.Add(new FlexGridColumn { Header = "Department", BindingPath = nameof(SampleEmployee.Department), Width = 140 });
        Grid.Columns.Add(new FlexGridColumn { Header = "Salary", BindingPath = nameof(SampleEmployee.Salary), Width = 110, StringFormat = "N2" });
        Grid.Columns.Add(new FlexGridColumn { Header = "Hire Date", BindingPath = nameof(SampleEmployee.HireDate), Width = 110, StringFormat = "yyyy-MM-dd" });
        Grid.Columns.Add(new FlexGridColumn { Header = "Active", BindingPath = nameof(SampleEmployee.IsActive), Width = 70, CanResize = false });

        Grid.AlternatingRowsBackground = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        Grid.ItemsSource = _employees;

        UpdateStatus();

        UnboundGrid.Columns.Add(new FlexGridColumn { Header = "Label", BindingPath = null, Width = 140 });
        UnboundGrid.Columns.Add(new FlexGridColumn { Header = "Q1", BindingPath = null, Width = 90, DataType = typeof(double), StringFormat = "N2" });
        UnboundGrid.Columns.Add(new FlexGridColumn { Header = "Q2", BindingPath = null, Width = 90, DataType = typeof(double), StringFormat = "N2" });
        UnboundGrid.Columns.Add(new FlexGridColumn { Header = "Q3", BindingPath = null, Width = 90, DataType = typeof(double), StringFormat = "N2" });
        UnboundGrid.Columns.Add(new FlexGridColumn { Header = "Active", BindingPath = null, Width = 70, DataType = typeof(bool), CanResize = false });

        // No ItemsSource assigned at all -- UnboundGrid stays in unbound mode, and RowCount/
        // the indexer are the only way to put data into it.
        UnboundGrid.RowCount = 6;
        for (var row = 0; row < UnboundGrid.RowCount; row++)
            UnboundGrid[row, 0] = $"Row {row + 1}";

        UpdateUnboundStatus();
    }

    private static ObservableCollection<SampleEmployee> BuildSampleData()
    {
        var departments = new[] { "Engineering", "Payroll", "Human Resources", "Operations", "Finance" };
        var random = new Random(42);
        var items = new ObservableCollection<SampleEmployee>();

        for (var i = 1; i <= 500; i++)
        {
            items.Add(new SampleEmployee
            {
                Id = i,
                Name = $"Employee {i:000}",
                Department = departments[random.Next(departments.Length)],
                Salary = Math.Round(18000 + random.NextDouble() * 32000, 2),
                HireDate = DateTime.Today.AddDays(-random.Next(30, 3650)),
                IsActive = random.NextDouble() > 0.15,
            });
        }

        return items;
    }

    private void AddRowButton_Click(object sender, RoutedEventArgs e)
    {
        var nextId = _employees.Count == 0 ? 1 : _employees.Max(x => x.Id) + 1;
        _employees.Add(new SampleEmployee
        {
            Id = nextId,
            Name = "New Employee",
            Department = "Unassigned",
            Salary = 0,
            HireDate = DateTime.Today,
            IsActive = true,
        });
        UpdateStatus();
    }

    private void RemoveSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var employee in Grid.SelectedItems.Cast<SampleEmployee>().ToList())
            _employees.Remove(employee);
        UpdateStatus();
    }

    // Grid is declared after these checkboxes in the XAML tree, and a literal
    // IsChecked="True" (see AlternatingCheck) fires Checked during InitializeComponent
    // itself -- before the field assignment for Grid has run. The null guard covers that
    // one startup call; every later toggle runs after the constructor has finished.
    private void ReadOnlyCheck_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (Grid is null) return;
        Grid.IsReadOnly = ReadOnlyCheck.IsChecked == true;
    }

    private void AlternatingCheck_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (Grid is null) return;
        Grid.AlternatingRowsBackground = AlternatingCheck.IsChecked == true
            ? new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x00, 0x00))
            : null;
    }

    private void Grid_SelectionChanged(object? sender, EventArgs e) => UpdateStatus();

    private void Grid_CellEdited(object? sender, FlexGridCellEventArgs e) => UpdateStatus();

    private void UpdateStatus() =>
        StatusText.Text = $"{_employees.Count} rows, {Grid.SelectedItems.Count} selected.";

    private void UnboundAddRowButton_Click(object sender, RoutedEventArgs e)
    {
        var row = UnboundGrid.RowCount;
        UnboundGrid.AddRow();
        UnboundGrid[row, 0] = $"Row {row + 1}";
        UpdateUnboundStatus();
    }

    private void UnboundInsertRowButton_Click(object sender, RoutedEventArgs e)
    {
        var index = UnboundGrid.SelectedRowIndex >= 0 ? UnboundGrid.SelectedRowIndex : 0;
        UnboundGrid.InsertRow(index);
        UnboundGrid[index, 0] = "New Row";
        UpdateUnboundStatus();
    }

    private void UnboundRemoveRowButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var index in UnboundGrid.SelectedRowIndices.OrderDescending())
            UnboundGrid.RemoveRow(index);
        UpdateUnboundStatus();
    }

    private void UnboundFillButton_Click(object sender, RoutedEventArgs e)
    {
        var random = new Random();
        for (var row = 0; row < UnboundGrid.RowCount; row++)
        {
            for (var column = 1; column <= 3; column++)
                UnboundGrid[row, column] = Math.Round(random.NextDouble() * 100, 2);
            UnboundGrid[row, 4] = random.NextDouble() > 0.5;
        }
        UpdateUnboundStatus();
    }

    private void UnboundClearButton_Click(object sender, RoutedEventArgs e)
    {
        UnboundGrid.ClearRows();
        UpdateUnboundStatus();
    }

    private void UnboundGrid_SelectionChanged(object? sender, EventArgs e) => UpdateUnboundStatus();

    private void UnboundGrid_CellEdited(object? sender, FlexGridCellEventArgs e) => UpdateUnboundStatus();

    private void UpdateUnboundStatus() =>
        UnboundStatusText.Text = $"{UnboundGrid.RowCount} rows, {UnboundGrid.SelectedRowIndices.Count} selected. " +
                                  "Try double-clicking a Q1/Q2/Q3 or Active cell -- there's no item behind any of this, just Grid[row, col].";

    private sealed class SampleEmployee
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public double Salary { get; set; }
        public DateTime HireDate { get; set; }
        public bool IsActive { get; set; }
    }
}
