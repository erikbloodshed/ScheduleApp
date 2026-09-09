using System.ComponentModel;
using System.Windows;

namespace ScheduleApp.Desktop.Controls.FlexGrid;

/// <summary>
/// One column definition for <see cref="FlexDataGrid"/> -- the equivalent of a FlexGrid
/// GridColumn / DataGridColumn. Plain INotifyPropertyChanged rather than a
/// DependencyObject: columns never sit in the visual tree themselves, so there's nothing
/// here that needs styling, binding or animation as a WPF element in its own right --
/// only the grid's header/row panels, which read these properties directly.
/// </summary>
public class FlexGridColumn : INotifyPropertyChanged
{
    private string _header = string.Empty;
    private double _width = 120;
    private double _minWidth = 40;
    private ListSortDirection? _sortDirection;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Header
    {
        get => _header;
        set => SetField(ref _header, value);
    }

    /// <summary>Public property name read off each row item via reflection, both to
    /// display the cell and to write an edited value back. Required when the grid is
    /// bound (<see cref="FlexDataGrid.ItemsSource"/> set); ignored in unbound mode, where
    /// cells are addressed by (row, column) instead -- see <see cref="FlexDataGrid"/>'s
    /// own "unbound mode" section.</summary>
    public string? BindingPath { get; init; }

    /// <summary>The CLR type edited text is parsed into. In bound mode this is inferred
    /// from the item's property automatically; in unbound mode there's no property to
    /// infer it from, so this decides both the editor (a checkbox for
    /// <see cref="bool"/>, a text box otherwise) and how a commit parses what was typed.
    /// Defaults to <see cref="string"/>.</summary>
    public Type DataType { get; set; } = typeof(string);

    public double Width
    {
        get => _width;
        set
        {
            var clamped = Math.Max(MinWidth, value);
            SetField(ref _width, clamped);
        }
    }

    public double MinWidth
    {
        get => _minWidth;
        set
        {
            if (SetField(ref _minWidth, value) && _width < value)
                Width = value;
        }
    }

    public bool IsReadOnly { get; set; }

    public bool CanSort { get; set; } = true;

    public bool CanResize { get; set; } = true;

    /// <summary>Standard .NET composite format applied to the cell's value, e.g. "N2" or
    /// "yyyy-MM-dd". Null shows the value's own ToString().</summary>
    public string? StringFormat { get; set; }

    /// <summary>Optional read-only cell appearance override. When set, this template
    /// renders the cell instead of the plain formatted TextBlock; editing still uses the
    /// grid's built-in text/checkbox editor.</summary>
    public DataTemplate? CellTemplate { get; set; }

    /// <summary>Set by <see cref="FlexDataGrid"/> when this column is the active sort
    /// column; drives the header's sort glyph.</summary>
    public ListSortDirection? SortDirection
    {
        get => _sortDirection;
        internal set => SetField(ref _sortDirection, value);
    }

    private bool SetField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
