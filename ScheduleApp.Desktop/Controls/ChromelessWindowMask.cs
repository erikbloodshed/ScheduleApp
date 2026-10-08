using System.Reflection;
using System.Windows.Controls;
using Syncfusion.Windows.Shared;

namespace ScheduleApp.Desktop.Controls;

/// <summary>
/// Takes ChromelessWindow's content mask off. Its Windows11 template (Syncfusion 34.2.9 to 35.1.37) masks the whole window with an
/// OpacityMask, a VisualBrush of an inner border (BorderMask), and ChromelessWindow applies it again whenever it updates
/// the window region (SetBorderMask, from its WM_SIZE hook and its StateChanged handler). The mask only rounds the
/// content's bottom corners to the window's CornerRadius. After a restore from the taskbar WPF can draw a frame before
/// the brush has anything to show, and an empty mask hides everything: the window went black for a frame, most
/// restores. A bare ChromelessWindow did it too, and never with the mask off.
/// </summary>
public static class ChromelessWindowMask
{
    /// <summary>
    /// Clears the mask on <paramref name="window"/>'s template root, and ChromelessWindow's own reference to that root,
    /// which it uses for nothing else, so it can't put the mask back. Call it after the template is applied. Only for a
    /// window with CornerRadius 0, whose mask cuts nothing. The field is private, so this finds it by name;
    /// ChromelessWindowMaskTests fails if a Syncfusion update renames it.
    /// </summary>
    public static void Remove(ChromelessWindow window)
    {
        var rootGridField = typeof(ChromelessWindow).GetField("rootGrid", BindingFlags.Instance | BindingFlags.NonPublic);
        if (rootGridField?.GetValue(window) is Grid rootGrid)
        {
            rootGrid.OpacityMask = null;
            rootGridField.SetValue(window, null);
        }
    }
}
