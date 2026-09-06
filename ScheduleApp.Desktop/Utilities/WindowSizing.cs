using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ScheduleApp.Desktop.Utilities;

/// <summary>
/// Attached behavior that keeps a Window on-screen at low resolutions. Several
/// dialogs pick a hardcoded MaxHeight/Height (660-760px) sized against an
/// assumed 1366x768 laptop screen, but that assumption breaks in practice:
/// a standard taskbar plus the 125% DPI scaling Windows itself recommends
/// (and ships as the default) for a 1366x768 panel shrinks the *usable*
/// work area to well under those numbers -- Windows reports it in logical
/// pixels around 1093x614 at 125%. The dialog then opens partially
/// off-screen, and because most of these dialogs are ResizeMode="NoResize"
/// the user has no way to shrink it back into view (the OK/Save/Cancel
/// footer is usually the part that disappears).
///
/// This re-checks the *actual* work area at the moment the window opens,
/// instead of guessing a number ahead of time. Crucially, it only sets a
/// permanent MaxHeight/MaxWidth ceiling on windows that can't be resized in
/// the first place (ResizeMode="NoResize" -- these have no Maximize button,
/// so a cap can't get in the way of anything). For a resizable window it
/// only clamps the size/position it *opens* at; MaxHeight/MaxWidth are left
/// alone so a later Maximize (or manual drag-resize) can still fill the
/// whole work area. An earlier version of this behavior set MaxHeight/
/// MaxWidth unconditionally, which had the side effect of permanently
/// capping resizable windows (e.g. MainWindow) a few pixels short of full
/// screen -- Maximize looked like it wasn't filling the screen because it
/// genuinely couldn't past that ceiling.
///
/// Usage: add utilities:WindowSizing.ConstrainToWorkArea="True" to the
/// Window tag. Works alongside MinHeight/MinWidth and any internal
/// ScrollViewer exactly as already authored on each dialog.
///
/// Multi-monitor: this used to clamp against SystemParameters.WorkArea, which
/// is always the *primary* monitor's work area regardless of which screen the
/// window is actually on. WindowStartupLocation="CenterOwner" (every dialog
/// using this behavior also sets that) correctly centers the dialog over its
/// owner first -- but that clamp then ran afterward in Loaded and, on a
/// dual-monitor setup with the app on the secondary screen, forced Top/Left
/// back into the primary monitor's coordinate range, so a perfectly-centered
/// dialog visibly jumped to the other screen. GetWorkAreaForWindow below
/// fixes this by asking Win32 for the work area of whichever monitor the
/// window's own HWND is actually on (MonitorFromWindow/GetMonitorInfo), then
/// converting those physical pixels through *this window's* DPI transform --
/// not SystemParameters', which again only ever reflects the primary
/// monitor's scale -- so a mixed-DPI dual-monitor setup is handled too, not
/// just a same-DPI one.
/// </summary>
public static class WindowSizing
{
    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    /// <summary>Breathing room kept between the window and the edge of the
    /// work area so it doesn't sit flush against the taskbar or screen edge.</summary>
    private const double EdgeMargin = 24;

    public static readonly DependencyProperty ConstrainToWorkAreaProperty =
        DependencyProperty.RegisterAttached(
            "ConstrainToWorkArea",
            typeof(bool),
            typeof(WindowSizing),
            new PropertyMetadata(false, OnConstrainToWorkAreaChanged));

    public static void SetConstrainToWorkArea(Window window, bool value) =>
        window.SetValue(ConstrainToWorkAreaProperty, value);

    public static bool GetConstrainToWorkArea(Window window) =>
        (bool)window.GetValue(ConstrainToWorkAreaProperty);

    private static void OnConstrainToWorkAreaChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true)
        {
            return;
        }

        // Loaded (not SourceInitialized) so this runs *after* SizeToContent
        // has already measured the window against its own content.
        window.Loaded += OnWindowLoaded;
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        var window = (Window)sender;
        window.Loaded -= OnWindowLoaded;

        Rect workArea = GetWorkAreaForWindow(window);
        double availableHeight = Math.Max(workArea.Height - EdgeMargin, window.MinHeight);
        double availableWidth = Math.Max(workArea.Width - EdgeMargin, window.MinWidth);

        // A NoResize dialog has no Maximize button and can't be dragged
        // bigger, so a permanent ceiling here only ever helps it and can
        // never block a gesture that doesn't exist for it. A resizable
        // window (MainWindow, or a resizable dialog) skips this entirely --
        // capping MaxHeight/MaxWidth there would also cap what Maximize can
        // grow it to, short of the real screen size.
        if (window.ResizeMode == ResizeMode.NoResize)
        {
            if (window.MaxHeight > availableHeight)
            {
                window.MaxHeight = availableHeight;
            }
            if (window.MaxWidth > availableWidth)
            {
                window.MaxWidth = availableWidth;
            }
        }

        // Clamp the size the window *opens* at -- this only affects the
        // starting size, never an upper bound the user can't get past
        // afterward by resizing or maximizing.
        if (window.ActualHeight > availableHeight)
        {
            window.Height = availableHeight;
        }
        if (window.ActualWidth > availableWidth)
        {
            window.Width = availableWidth;
        }

        // Re-clamp position: shrinking the size above (or an owner window
        // near a screen edge) can still leave a corner off the work area.
        if (window.Top < workArea.Top)
        {
            window.Top = workArea.Top;
        }
        if (window.Left < workArea.Left)
        {
            window.Left = workArea.Left;
        }
        if (window.Top + window.ActualHeight > workArea.Bottom)
        {
            window.Top = Math.Max(workArea.Top, workArea.Bottom - window.ActualHeight);
        }
        if (window.Left + window.ActualWidth > workArea.Right)
        {
            window.Left = Math.Max(workArea.Left, workArea.Right - window.ActualWidth);
        }
    }

    /// <summary>Work area (in this window's own WPF device-independent units) of
    /// whichever monitor the window is actually displayed on -- not
    /// SystemParameters.WorkArea, which is always the primary monitor's, in the
    /// primary monitor's own DPI scale, regardless of where the window is. Loaded
    /// has already fired by the time this is called, so the window's HWND exists
    /// and PresentationSource.FromVisual can resolve its DPI transform.</summary>
    private static Rect GetWorkAreaForWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            // Only reached if the Win32 calls themselves fail -- falls back to
            // the old single-monitor behavior rather than leaving the window
            // completely unconstrained.
            return SystemParameters.WorkArea;
        }

        // rcWork comes back in physical pixels. TransformFromDevice is *this*
        // window's own DPI matrix (per-monitor-DPI aware), so this converts
        // correctly even when the window's monitor has a different scale
        // factor than the primary one SystemParameters assumes.
        Matrix transform = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice
            ?? Matrix.Identity;

        Point topLeft = transform.Transform(new Point(info.WorkArea.Left, info.WorkArea.Top));
        Point bottomRight = transform.Transform(new Point(info.WorkArea.Right, info.WorkArea.Bottom));

        return new Rect(topLeft, bottomRight);
    }
}
