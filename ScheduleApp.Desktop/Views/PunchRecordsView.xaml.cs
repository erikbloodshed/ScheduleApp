using System.Windows;
using System.Windows.Controls;
using ScheduleApp.Desktop.ViewModels;
using ScheduleApp.Desktop.ViewModels.Attendance;

namespace ScheduleApp.Desktop.Views;

public partial class PunchRecordsView : UserControl
{
    public PunchRecordsView()
    {
        InitializeComponent();
    }

    /// <summary>Runs when a suggestion in the Punch Records search dropdown is
    /// clicked. Handled in code-behind rather than a Command on the ListBoxItem
    /// since ListBox has no built-in "item clicked" command hook -- this just
    /// finds which PunchSearchSuggestion was clicked and hands it to the
    /// ViewModel, which owns all the actual text-editing logic (see
    /// SelectLogViewSuggestion).</summary>
    private void PunchSearchSuggestionList_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not ListBox { DataContext: AttendanceViewModel viewModel } listBox)
            return;

        if ((e.OriginalSource as DependencyObject) is not { } source)
            return;

        var item = ItemsControl.ContainerFromElement(listBox, source) as ListBoxItem;
        if (item?.Content is not PunchSearchSuggestion suggestion)
            return;

        viewModel.SelectLogViewSuggestionCommand.Execute(suggestion);
        e.Handled = true;
    }
}
