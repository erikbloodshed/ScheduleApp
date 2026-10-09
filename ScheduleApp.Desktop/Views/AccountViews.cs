using System.Windows;

namespace ScheduleApp.Desktop.Views;

/// <summary>Shared by the account views.</summary>
internal static class AccountViews
{
    /// <summary>An inline error shows only while there's one to show.</summary>
    public static Visibility VisibleWhenSet(string? message) =>
        string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
}
