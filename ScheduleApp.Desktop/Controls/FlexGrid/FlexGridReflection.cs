using System.Globalization;
using System.Reflection;
using System.Text;

namespace ScheduleApp.Desktop.Controls.FlexGrid;

/// <summary>
/// Reflection-based get/set/format/parse for a column's <see cref="FlexGridColumn.BindingPath"/>.
/// The grid deliberately doesn't use WPF's Binding engine for cell values -- rows are
/// realized and recycled by <see cref="FlexGridRowsPanel"/> as plain data, not as items in
/// an ItemsControl, so there's no ItemContainerGenerator to hang a Binding's DataContext
/// off. Reflection also means row items don't need to implement INotifyPropertyChanged to
/// work with this control at all, at the cost of the grid not picking up an external
/// change to an item until that row is next realized.
/// </summary>
internal static class FlexGridReflection
{
    private static readonly Dictionary<(Type Type, string Path), PropertyInfo?> PropertyCache = new();

    public static PropertyInfo? GetProperty(object item, string path)
    {
        var type = item.GetType();
        var key = (type, path);
        if (PropertyCache.TryGetValue(key, out var cached)) return cached;

        var property = type.GetProperty(path, BindingFlags.Public | BindingFlags.Instance);
        PropertyCache[key] = property;
        return property;
    }

    public static object? GetValue(object item, string path) => GetProperty(item, path)?.GetValue(item);

    public static Type? GetPropertyType(object item, string path) => GetProperty(item, path)?.PropertyType;

    /// <summary>Parses <paramref name="text"/> to the property's declared type and writes
    /// it. Throws (FormatException/OverflowException/etc.) on unparseable input -- callers
    /// decide what "the edit failed" means for them (FlexDataGrid.TryCommitCell keeps the
    /// cell in edit mode rather than losing the typed text).</summary>
    public static void SetValue(object item, string path, string text)
    {
        var property = GetProperty(item, path);
        if (property is null || !property.CanWrite) return;

        property.SetValue(item, ParseText(text, property.PropertyType));
    }

    public static void SetValue(object item, string path, bool value)
    {
        var property = GetProperty(item, path);
        if (property is null || !property.CanWrite) return;
        property.SetValue(item, value);
    }

    /// <summary>Parses <paramref name="text"/> to <paramref name="targetType"/> (or its
    /// underlying type, if it's a Nullable&lt;T&gt;) -- shared by bound-mode property
    /// writes above and FlexGridUnboundStore's own cell edits, so both parse the same way.
    /// Throws on unparseable input, same as <see cref="SetValue(object,string,string)"/>.</summary>
    public static object? ParseText(string text, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType);
        var effectiveType = underlying ?? targetType;

        if (string.IsNullOrWhiteSpace(text) && (underlying is not null || effectiveType == typeof(string)))
            return effectiveType == typeof(string) ? string.Empty : null;

        if (effectiveType.IsEnum) return Enum.Parse(effectiveType, text, ignoreCase: true);
        if (effectiveType == typeof(DateTime)) return DateTime.Parse(text, CultureInfo.CurrentCulture);
        if (effectiveType == typeof(DateOnly)) return DateOnly.Parse(text, CultureInfo.CurrentCulture);
        if (effectiveType == typeof(TimeOnly)) return TimeOnly.Parse(text, CultureInfo.CurrentCulture);
        if (effectiveType == typeof(Guid)) return Guid.Parse(text);
        return Convert.ChangeType(text, effectiveType, CultureInfo.CurrentCulture);
    }

    public static string FormatValue(object? value, string? format)
    {
        if (value is null) return string.Empty;

        if (!string.IsNullOrEmpty(format))
            return string.Format(CultureInfo.CurrentCulture, "{0:" + format + "}", value);

        return value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.CurrentCulture)
            : value.ToString() ?? string.Empty;
    }

    /// <summary>"HireDate" -&gt; "Hire Date", for auto-generated column headers.</summary>
    public static string Humanize(string propertyName)
    {
        var builder = new StringBuilder(propertyName.Length + 4);
        for (var i = 0; i < propertyName.Length; i++)
        {
            if (i > 0 && char.IsUpper(propertyName[i]) && !char.IsUpper(propertyName[i - 1]))
                builder.Append(' ');
            builder.Append(propertyName[i]);
        }
        return builder.ToString();
    }
}
