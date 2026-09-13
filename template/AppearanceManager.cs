using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT;
using WinRT.Interop;
using XamlBrush = Microsoft.UI.Xaml.Media.Brush;
using XamlColor = Windows.UI.Color;

namespace UiTemplate;

/// <summary>
/// Applies the appearance settings to a window: theme, window opacity and the
/// background (Mica / Acrylic, or a blurred background image).
///
/// They are three separate steps because they have three separate lifetimes: the theme
/// and the background only change occasionally, while opacity is re-applied on every
/// slider tick.
/// </summary>
internal static class AppearanceManager
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

    /// <summary>
    /// Longest edge the background image is downscaled to before blurring.  A separable
    /// gaussian costs O(width * height * radius), so blurring a 4K photo with a 255px
    /// radius on the UI thread would freeze the window for minutes; 512px is
    /// indistinguishable once it is blurred and stretched back over the window.
    /// </summary>
    private const int MaxBlurEdge = 512;

    /// <summary>Hard cap on the kernel radius actually used (see <see cref="MaxBlurEdge"/>).</summary>
    private const int MaxKernelRadius = 24;

    private static MicaController? _mica;
    private static DesktopAcrylicController? _acrylic;
    private static SystemBackdropConfiguration? _backdropConfig;
    private static ICompositionSupportsSystemBackdrop? _backdropTarget;

    /// <summary>Apply theme, background and opacity, in that order.</summary>
    internal static void ApplyAll(Window window, AppSettings settings)
    {
        ApplyTheme(window, settings);
        ApplyBackground(window, settings);
        ApplyOpacity(window, settings);
    }

    /// <summary>
    /// Set <c>RequestedTheme</c> on the window content.  Doing it on the root element
    /// rather than on Application leaves a second window free to use another theme.
    /// </summary>
    internal static void ApplyTheme(Window window, AppSettings settings)
    {
        if (window.Content is FrameworkElement root)
        {
            root.RequestedTheme = settings.Theme switch
            {
                AppSettings.ThemeMode.Light => ElementTheme.Light,
                AppSettings.ThemeMode.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }

    /// <summary>
    /// Apply the window opacity.
    ///
    /// WinUI 3 windows have no <c>Opacity</c> property, so the alpha goes on the Win32
    /// window: <c>WS_EX_LAYERED</c> plus <c>SetLayeredWindowAttributes</c>.  The layered
    /// bit is set in every mode, not only below 100%, because the composition backdrops
    /// need a layered window to composite into.
    ///
    /// While Mica or Acrylic is active the backdrop owns the window surface, so the
    /// slider is inert there and the alpha is pinned to opaque; otherwise the window
    /// would fade the backdrop out and the material would be pointless.
    /// </summary>
    internal static void ApplyOpacity(Window window, AppSettings settings)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(window);
            if (hwnd == IntPtr.Zero) return;

            var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
                // A style change only takes effect after a frame change.
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }

            byte alpha = settings.BackgroundBlur == AppSettings.BlurMode.Default
                ? (byte)Math.Clamp(settings.WindowOpacity * 255.0, 25, 255)
                : (byte)255;

            SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
        }
        catch (Exception ex) { AppLog.Log($"ApplyOpacity failed: {ex.Message}"); }
    }

    /// <summary>
    /// Apply the background: a Mica/Acrylic backdrop when one is selected and supported,
    /// otherwise the background image (blurred through GDI+), otherwise a solid themed
    /// brush when no image is configured.
    /// </summary>
    internal static void ApplyBackground(Window window, AppSettings settings)
    {
        // The shell is expected to have a Grid as its root element.
        if (window.Content is not Grid root) return;

        if (settings.BackgroundBlur != AppSettings.BlurMode.Default)
        {
            // The backdrop must show through, so the root must not paint over it.
            root.Background = new SolidColorBrush(Colors.Transparent);

            if (TryApplyMaterial(window, settings)) return;

            // Unsupported (old build, VM, transparency off): fall through and paint the
            // image rather than leaving a black window behind.
            AppLog.Log($"{settings.BackgroundBlur} backdrop unavailable, falling back to the image");
        }

        ClearBackdrop();

        try { root.Background = BuildBackgroundBrush(settings); }
        catch (Exception ex) { AppLog.Log($"ApplyBackground failed: {ex.Message}"); }
    }

    /// <summary>Release the backdrop controllers.  Call once, on exit.</summary>
    internal static void Cleanup() => ClearBackdrop();

    /// <summary>True when the effective theme is dark.</summary>
    internal static bool IsDark(AppSettings settings) => settings.Theme switch
    {
        AppSettings.ThemeMode.Dark => true,
        AppSettings.ThemeMode.Light => false,
        _ => IsSystemDark(),
    };

    /// <summary>Read the Windows app (light/dark) theme from the registry.</summary>
    internal static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- background

    /// <summary>Build the brush behind the whole window: the configured image, or a solid themed brush.</summary>
    private static XamlBrush BuildBackgroundBrush(AppSettings settings)
    {
        var path = settings.BackgroundImagePath;

        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                using var source = new Bitmap(path);
                using var working = Downscale(source, MaxBlurEdge, out var scale);

                // The radius is a pixel count in the original image; shrink it with the
                // image so the blur looks the same after it is stretched back.
                var radius = settings.BackgroundBlurRadius <= 0
                    ? 0
                    : (int)Math.Round(settings.BackgroundBlurRadius * scale);
                using var blurred = Blur(working, radius);

                return new ImageBrush
                {
                    ImageSource = ToWriteableBitmap(blurred),
                    Stretch = Stretch.UniformToFill,
                    Opacity = Math.Clamp(settings.BackgroundImageOpacity, 0.0, 1.0),
                };
            }
            catch (Exception ex) { AppLog.Log($"Background image {path} failed: {ex.Message}"); }
        }

        return new SolidColorBrush(IsDark(settings)
            ? XamlColor.FromArgb(0xFF, 0x1E, 0x1E, 0x1E)
            : XamlColor.FromArgb(0xFF, 0xF0, 0xF0, 0xF0));
    }

    /// <summary>Scale a bitmap down so its longest edge is at most <paramref name="maxEdge"/>.</summary>
    private static Bitmap Downscale(Bitmap source, int maxEdge, out double scale)
    {
        scale = Math.Min(1.0, maxEdge / (double)Math.Max(source.Width, source.Height));

        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, width, height);
        return result;
    }

    /// <summary>
    /// Separable gaussian blur: one 1D convolution pass across, one down.  Two O(n * r)
    /// passes instead of a single O(n * r^2) 2D pass is the difference between instant
    /// and unusable at these kernel sizes.
    /// </summary>
    private static Bitmap Blur(Bitmap source, int radius)
    {
        if (radius <= 0) return new Bitmap(source);
        radius = Math.Min(radius, MaxKernelRadius);

        int width = source.Width, height = source.Height;
        var kernel = BuildGaussianKernel(radius);
        var output = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        var sourceData = source.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var targetData = output.LockBits(
            new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        try
        {
            int stride = Math.Abs(sourceData.Stride);
            var pixels = new byte[stride * height];
            Marshal.Copy(sourceData.Scan0, pixels, 0, pixels.Length);

            var horizontal = new byte[pixels.Length];
            var result = new byte[pixels.Length];
            BlurAxis(pixels, horizontal, width, height, stride, kernel, vertical: false);
            BlurAxis(horizontal, result, width, height, stride, kernel, vertical: true);

            Marshal.Copy(result, 0, targetData.Scan0, result.Length);
        }
        finally
        {
            source.UnlockBits(sourceData);
            output.UnlockBits(targetData);
        }

        return output;
    }

    /// <summary>
    /// One convolution pass over a single axis of a 32bppArgb buffer.  Edge samples are
    /// clamped rather than skipped, so pixels near the border stay as bright as the ones
    /// next to them instead of fading to black.
    /// </summary>
    private static void BlurAxis(
        byte[] source, byte[] target, int width, int height, int stride, double[] kernel, bool vertical)
    {
        int radius = (kernel.Length - 1) / 2;
        int outer = vertical ? width : height;   // fixed coordinate, one row or one column
        int inner = vertical ? height : width;   // walking coordinate
        int step = vertical ? stride : 4;

        for (int o = 0; o < outer; o++)
        {
            int outerOffset = vertical ? o * 4 : o * stride;

            for (int i = 0; i < inner; i++)
            {
                double b = 0, g = 0, r = 0, a = 0;

                for (int k = -radius; k <= radius; k++)
                {
                    int index = outerOffset + Math.Clamp(i + k, 0, inner - 1) * step;
                    double weight = kernel[k + radius];
                    b += source[index] * weight;
                    g += source[index + 1] * weight;
                    r += source[index + 2] * weight;
                    a += source[index + 3] * weight;
                }

                int destination = outerOffset + i * step;
                target[destination] = ToByte(b);
                target[destination + 1] = ToByte(g);
                target[destination + 2] = ToByte(r);
                target[destination + 3] = ToByte(a);
            }
        }
    }

    private static double[] BuildGaussianKernel(int radius)
    {
        int size = radius * 2 + 1;
        var kernel = new double[size];
        double sum = 0;

        for (int i = 0; i < size; i++)
        {
            int x = i - radius;
            kernel[i] = Math.Exp(-(x * (double)x) / (2.0 * radius * radius));
            sum += kernel[i];
        }

        for (int i = 0; i < size; i++) kernel[i] /= sum;
        return kernel;
    }

    private static byte ToByte(double value) => (byte)Math.Clamp(value, 0, 255);

    /// <summary>
    /// Copy a GDI+ bitmap into a WinUI <see cref="WriteableBitmap"/>.  Must run on the UI
    /// thread, because a WriteableBitmap belongs to the thread that created it.
    /// </summary>
    private static WriteableBitmap ToWriteableBitmap(Bitmap bitmap)
    {
        var target = new WriteableBitmap(bitmap.Width, bitmap.Height);
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];

        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { Marshal.Copy(data.Scan0, pixels, 0, pixels.Length); }
        finally { bitmap.UnlockBits(data); }

        using var stream = target.PixelBuffer.AsStream();
        stream.Write(pixels, 0, pixels.Length);
        return target;
    }

    // ------------------------------------------------------------------- backdrop

    /// <summary>
    /// Attach a Mica or Acrylic controller to the window.
    ///
    /// <c>Window.SystemBackdrop = new MicaBackdrop()</c> and the DWM
    /// <c>DWMWA_SYSTEMBACKDROP_TYPE</c> attribute are both unreliable on unpackaged
    /// WinUI 3 windows, so the controller is created by hand and pointed at the
    /// window's <c>ICompositionSupportsSystemBackdrop</c> target.
    /// </summary>
    private static bool TryApplyMaterial(Window window, AppSettings settings)
    {
        try
        {
            var target = window.As<ICompositionSupportsSystemBackdrop>();
            if (target is null)
            {
                AppLog.Log("Window does not support ICompositionSupportsSystemBackdrop");
                return false;
            }

            ClearBackdrop();

            switch (settings.BackgroundBlur)
            {
                case AppSettings.BlurMode.Mica when MicaController.IsSupported():
                    _mica = new MicaController { Kind = MicaKind.Base };
                    _backdropConfig = NewBackdropConfiguration();
                    _mica.AddSystemBackdropTarget(target);
                    _mica.SetSystemBackdropConfiguration(_backdropConfig);
                    break;

                case AppSettings.BlurMode.Acrylic when DesktopAcrylicController.IsSupported():
                    _acrylic = new DesktopAcrylicController();
                    _backdropConfig = NewBackdropConfiguration();
                    _acrylic.AddSystemBackdropTarget(target);
                    _acrylic.SetSystemBackdropConfiguration(_backdropConfig);
                    break;

                default:
                    return false;
            }

            _backdropTarget = target;
            AppLog.Log($"{settings.BackgroundBlur} backdrop applied");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Log($"TryApplyMaterial failed: {ex.Message}");
            return false;
        }
    }

    private static SystemBackdropConfiguration NewBackdropConfiguration() => new()
    {
        IsInputActive = true,
        Theme = SystemBackdropTheme.Default,
    };

    /// <summary>Detach and dispose both backdrop controllers.</summary>
    private static void ClearBackdrop()
    {
        if (_mica is not null)
        {
            try { _mica.RemoveSystemBackdropTarget(_backdropTarget!); } catch { }
            try { _mica.Dispose(); } catch { }
            _mica = null;
        }

        if (_acrylic is not null)
        {
            try { _acrylic.RemoveSystemBackdropTarget(_backdropTarget!); } catch { }
            try { _acrylic.Dispose(); } catch { }
            _acrylic = null;
        }

        _backdropConfig = null;
        _backdropTarget = null;
    }

    // --------------------------------------------------------------------- win32

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
