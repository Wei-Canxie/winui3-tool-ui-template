using System;
using System.IO;
using System.Text.Json;

namespace UiTemplate;

/// <summary>
/// Everything the tool persists, serialised to
/// <c>%LOCALAPPDATA%\UiTemplate\settings.json</c>.
///
/// The settings window never edits this instance directly: it edits a
/// <see cref="Clone"/> (the draft) and copies it back with <see cref="CopyFrom"/>
/// when the user presses Apply.  That is what keeps a slider drag cheap and makes
/// Cancel changes possible.
/// </summary>
internal sealed class AppSettings
{
    /// <summary>Theme mode: follow the OS, or force light/dark.</summary>
    public ThemeMode Theme { get; set; } = ThemeMode.FollowSystem;

    /// <summary>
    /// Overall window opacity, 0.3 to 1.0.  Applied as the layered-window alpha,
    /// which is inert while a Mica/Acrylic backdrop owns the window surface.
    /// </summary>
    public double WindowOpacity { get; set; } = 1.0;

    /// <summary>Background image file path.  Empty means "use a solid themed brush".</summary>
    public string BackgroundImagePath { get; set; } = "";

    /// <summary>Opacity of the background image, 0.0 to 1.0.</summary>
    public double BackgroundImageOpacity { get; set; } = 0.85;

    /// <summary>Background effect: plain image, Mica, or Acrylic.</summary>
    public BlurMode BackgroundBlur { get; set; } = BlurMode.Default;

    /// <summary>Gaussian blur radius applied to the background image, 0 to 255.</summary>
    public int BackgroundBlurRadius { get; set; } = 8;

    /// <summary>Demo feature switch, edited on the General page and from the tray menu.</summary>
    public bool SampleEnabled { get; set; } = true;

    /// <summary>Demo feature strength, 0 to 100, edited on the Advanced page.</summary>
    public double SampleStrength { get; set; } = 50.0;

    /// <summary>Theme selection.</summary>
    public enum ThemeMode
    {
        /// <summary>Track the Windows app theme.</summary>
        FollowSystem,

        /// <summary>Always light, whatever Windows does.</summary>
        Light,

        /// <summary>Always dark, whatever Windows does.</summary>
        Dark,
    }

    /// <summary>Background effect selection.</summary>
    public enum BlurMode
    {
        /// <summary>Background image (optionally blurred) or a solid themed brush.</summary>
        Default,

        /// <summary>Mica backdrop, falling back to the image when unsupported.</summary>
        Mica,

        /// <summary>Acrylic backdrop, falling back to the image when unsupported.</summary>
        Acrylic,
    }

    /// <summary>The settings file path: <c>%LOCALAPPDATA%\UiTemplate\settings.json</c>.</summary>
    internal static string SettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UiTemplate",
            "settings.json");

    /// <summary>
    /// Read the settings file.  Anything unreadable, malformed or out of range
    /// falls back to the default for that value rather than failing the launch.
    /// </summary>
    internal static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (settings is not null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Log($"Load settings failed, using defaults: {ex.Message}");
        }

        return new AppSettings();
    }

    /// <summary>Keep every value inside the range its control can produce.</summary>
    internal void Normalize()
    {
        WindowOpacity = Math.Clamp(WindowOpacity, 0.3, 1.0);
        BackgroundImageOpacity = Math.Clamp(BackgroundImageOpacity, 0.0, 1.0);
        BackgroundBlurRadius = Math.Clamp(BackgroundBlurRadius, 0, 255);
        SampleStrength = Math.Clamp(SampleStrength, 0, 100);
        BackgroundImagePath ??= "";
    }

    /// <summary>
    /// Deep copy through JSON.  Using the serialiser for this means a newly added
    /// property can never be forgotten in a hand-written member-by-member copy.
    /// </summary>
    internal AppSettings Clone()
    {
        try
        {
            var json = JsonSerializer.Serialize(this);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLog.Log($"Clone settings failed: {ex.Message}");
            return new AppSettings();
        }
    }

    /// <summary>Overwrite every value of this instance with another's values.</summary>
    internal void CopyFrom(AppSettings other)
    {
        var copy = other.Clone();

        Theme = copy.Theme;
        WindowOpacity = copy.WindowOpacity;
        BackgroundImagePath = copy.BackgroundImagePath;
        BackgroundImageOpacity = copy.BackgroundImageOpacity;
        BackgroundBlur = copy.BackgroundBlur;
        BackgroundBlurRadius = copy.BackgroundBlurRadius;
        SampleEnabled = copy.SampleEnabled;
        SampleStrength = copy.SampleStrength;
    }

    /// <summary>Write the settings file, creating the folder on first run.</summary>
    internal void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(
                SettingsPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            AppLog.Log($"Save settings failed: {ex.Message}");
        }
    }
}
