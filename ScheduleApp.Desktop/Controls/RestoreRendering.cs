using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ScheduleApp.Desktop.Controls;

/// <summary>
/// Draws a window in software from the moment it's minimized until shortly after it's back, and with the GPU the rest of
/// the time. Restored from the taskbar while maximized, MainWindow showed its content, then went black for a frame or two
/// about a tenth of a second later, then showed it again. The black was WPF's hardware
/// surface: in software the same restores never went black, and neither did switching back to the GPU once the window
/// had settled. A plain WPF window didn't do it; a ChromelessWindow with the app's content did, whatever of its own
/// handling was taken off, so this works around it rather than fixing its cause.
/// </summary>
public static class RestoreRendering
{
    /// <summary>
    /// How long after a restore the window stays in software. The black came up to about 0.27 s after the restore;
    /// this is comfortably past it.
    /// </summary>
    public static readonly TimeSpan SoftwareAfterRestore = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Switches <paramref name="window"/> to software rendering whenever it's minimized, and back to the default (the GPU)
    /// <see cref="SoftwareAfterRestore"/> after it's restored or maximized again. Call it once its HwndSource exists
    /// (OnSourceInitialized or later).
    /// </summary>
    public static void UseSoftwareWhileRestoring(Window window)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source)
        {
            return;
        }

        var backToGpu = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = SoftwareAfterRestore };
        backToGpu.Tick += (_, _) =>
        {
            backToGpu.Stop();
            if (!source.IsDisposed)
            {
                source.CompositionTarget.RenderMode = RenderMode.Default;
            }
        };

        window.StateChanged += (_, _) =>
        {
            if (source.IsDisposed)
            {
                return;
            }

            if (window.WindowState == WindowState.Minimized)
            {
                backToGpu.Stop();
                source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
            }
            else if (source.CompositionTarget.RenderMode == RenderMode.SoftwareOnly)
            {
                // Restarted, so a second restore soon after the first still gets its full time in software.
                backToGpu.Stop();
                backToGpu.Start();
            }
        };
        window.Closed += (_, _) => backToGpu.Stop();
    }
}
