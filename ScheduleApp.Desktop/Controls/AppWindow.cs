using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Syncfusion.Windows.Shared;

namespace ScheduleApp.Desktop.Controls;

/// <summary>
/// The app's windows: a Syncfusion ChromelessWindow, which draws the title bar itself, in the theme, with its own Minimize,
/// Maximize/Restore and Close buttons, so Windows' caption bar is gone. Every window in the app derives from it; it replaced
/// WPF-UI's FluentWindow. Ported from TinapayanRMS (Controls/AppWindow there), where the fixes below were found: it holds
/// everything a window needs to behave like one on top of ChromelessWindow.
/// <para>
/// UseNativeChrome, CornerRadius 0 and ResizeBorderThickness 0 (set here, so a window's XAML doesn't repeat them) fix two
/// bugs in the plain chromeless mode. There, every point of the window hit-tested as
/// client area, and ChromelessWindow resized the window from its own mouse handling, which only caught the outermost pixel
/// of the left and right edges. The bottom edge and all four corners couldn't be dragged at all. It also cut its 8px
/// rounded corners out of an opaque window, so the corners and a 1px ring around it drew black. With UseNativeChrome, a
/// WPF WindowChrome does the hit-testing, so Windows resizes from every edge and corner (OnSourceInitialized widens its
/// resize band to the system's), and Windows 11 draws the rounded corners, shadow and 1px border itself. So the theme's
/// own rounding (CornerRadius 8) and 1px border (ResizeBorderThickness) are switched off; they only drew black corners
/// and a dark ring when maximized.
/// </para>
/// <para>
/// ShowIcon is off by default: none of this app's windows has an icon of its own, so the title bar would otherwise show
/// the generic window glyph, which FluentWindow never did.
/// </para>
/// </summary>
public class AppWindow : ChromelessWindow
{
    public AppWindow()
    {
        UseNativeChrome = true;
        CornerRadius = new CornerRadius(0);
        ResizeBorderThickness = new Thickness(0);
        ShowIcon = false;
    }

    /// <summary>
    /// No content mask: the window's CornerRadius is 0, so ChromelessWindow's mask cuts nothing, and it could black the
    /// window out for a frame after a restore from the taskbar (ChromelessWindowMask).
    /// </summary>
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        ChromelessWindowMask.Remove(this);
    }

    /// <summary>
    /// UseNativeChrome gives the window a WindowChrome whose resize band ChromelessWindow binds to its own
    /// ResizeBorderThickness. That's 0 here, since it's also the width of the border the theme draws. A local value
    /// replaces the binding, so Windows resizes from a band as wide as a normal window's frame while no border is drawn.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        // Before base, which adds ChromelessWindow's own hook: HwndSource calls the newest hook first, so this one runs
        // after it and gets the last word on the region.
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(DropWindowRegion);
        }

        base.OnSourceInitialized(e);

        ChromelessWindowDpi.LetWpfHandleDpiChanges(this);
        RestoreRendering.UseSoftwareWhileRestoring(this);

        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome)
        {
            chrome.ResizeBorderThickness = SystemParameters.WindowResizeBorderThickness;
        }

        // Windows 11 rounds a window like this one only when asked: it stopped doing it by itself once ChromelessWindow
        // had given the window a region (below), even after the region was gone again. Windows 10 has no such attribute
        // and returns an error, which leaves its square corners as they are.
        var hwnd = new WindowInteropHelper(this).Handle;
        var round = DwmCornerRound;
        _ = DwmSetWindowAttribute(hwnd, DwmWindowCornerPreference, ref round, sizeof(int));
        _ = SetWindowRgn(hwnd, IntPtr.Zero, true);
    }

    private const int DwmWindowCornerPreference = 33;
    private const int DwmCornerRound = 2;

    private const int WmSize = 0x0005;
    private const int WmSizing = 0x0214;
    private const int WmExitSizeMove = 0x0232;

    // Set while a deferred DropWindowRegion clear is queued, so a live resize's burst of messages queues just one.
    private bool _regionClearQueued;

    /// <summary>
    /// Keeps Windows 11's rounded corners. Whenever the window is sized, finishes a move or changes state, ChromelessWindow
    /// gives it a square window region: a leftover of its own mode, which cuts its corners with one. Windows doesn't round a
    /// window that has a region, so the corners went square after any drag, such as onto the secondary monitor, and after
    /// a restore. ChromelessWindow sets it from two places: its own hook (WM_SIZE, WM_SIZING, WM_EXITSIZEMOVE), which has
    /// just run when this one does, and its StateChanged handler, which WPF raises from WM_SIZE after every hook. So the
    /// region comes off now and again once this message has been dealt with.
    /// Without a redraw: taking the region off only reshapes the window, which WPF has drawn already. The
    /// second clear, asking for one, landed just after a restore's first frame, and the window went black until the next
    /// (at normal size, black on most restores with the redraw and on none without it; maximized,
    /// RestoreRendering takes care of the rest).
    /// </summary>
    private IntPtr DropWindowRegion(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmSize or WmSizing or WmExitSizeMove)
        {
            _ = SetWindowRgn(hwnd, IntPtr.Zero, false);

            if (!_regionClearQueued)
            {
                _regionClearQueued = true;
                Dispatcher.BeginInvoke(() =>
                {
                    _regionClearQueued = false;
                    _ = SetWindowRgn(hwnd, IntPtr.Zero, false);
                });
            }
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
