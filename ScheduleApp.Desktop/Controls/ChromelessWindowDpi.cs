using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using Syncfusion.Windows.Shared;

namespace ScheduleApp.Desktop.Controls;

/// <summary>
/// Lets WPF handle a ChromelessWindow's DPI changes again. With UseNativeChrome, ChromelessWindow (Syncfusion 34.2.9 to 35.1.37)
/// adds a window hook that marks every WM_DPICHANGED handled and does nothing else. WPF calls a window's hooks before
/// its own handling, so the message never reaches it. When the window moved to a monitor with another scale, WPF kept
/// drawing at the old monitor's DPI and Windows didn't resize the window. On this machine's 100% secondary monitor,
/// a window opened on the 125% primary drew everything a quarter too large.
/// </summary>
public static class ChromelessWindowDpi
{
    /// <summary>
    /// Takes ChromelessWindow's hook back off <paramref name="window"/>. Call it after ChromelessWindow's own
    /// OnSourceInitialized, which is where the hook goes on. The hook is a private method, so this finds it by name,
    /// and the delegate made from it equals the one ChromelessWindow added, which is what RemoveHook matches on.
    /// ChromelessWindowDpiTests fails if a Syncfusion update renames it.
    /// </summary>
    public static void LetWpfHandleDpiChanges(ChromelessWindow window)
    {
        var swallowDpiChanges = typeof(ChromelessWindow).GetMethod(
            "WndProc",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(IntPtr), typeof(int), typeof(IntPtr), typeof(IntPtr), typeof(bool).MakeByRefType()]);

        if (swallowDpiChanges is not null && PresentationSource.FromVisual(window) is HwndSource source)
        {
            source.RemoveHook(swallowDpiChanges.CreateDelegate<HwndSourceHook>(window));
        }
    }
}
