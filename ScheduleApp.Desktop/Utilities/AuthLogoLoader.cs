using System.IO;
using System.Windows.Media.Imaging;

namespace ScheduleApp.Desktop.Utilities;

/// <summary>
/// Shared by SignInPanel and SetupAdminPanel -- both panels inside MainWindow's
/// AuthOverlay show the same "Company logo" image (see each panel's own XAML comment),
/// so swapping in a custom one (SignInSettings.LogoPath, set from the Settings dialog's
/// "Sign-in page" section) is one shared helper instead of two copies of the same
/// BitmapImage-loading/try-catch. SettingsDialog also uses this for its own logo preview.
/// </summary>
public static class AuthLogoLoader
{
    /// <summary>
    /// Loads logoPath as a frozen (cross-thread-safe, and not left holding the file
    /// open) BitmapImage, or null if the path is blank, the file doesn't exist, or it
    /// fails to load as an image. Callers should leave their XAML-declared default
    /// /Assets logo in place when this returns null -- a bad or missing custom logo
    /// should never block sign-in with a broken image.
    /// </summary>
    public static BitmapImage? TryLoad(string? logoPath)
    {
        if (string.IsNullOrWhiteSpace(logoPath) || !File.Exists(logoPath))
            return null;

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(logoPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
