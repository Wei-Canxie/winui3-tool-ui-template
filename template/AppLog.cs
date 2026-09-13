using System;
using System.IO;

namespace UiTemplate;

/// <summary>
/// Append-only text log.
/// The WinUI 3 XAML compiler generates the process entry point in App.g.i.cs, so
/// there is no Program.Main of our own to log from; everything interesting happens
/// in OnLaunched and in the window.  Writing to a file rather than to a debugger
/// output means the log survives a crash and can be read from a release build.
/// </summary>
internal static class AppLog
{
    /// <summary>Where the log lives: <c>%TEMP%\UiTemplate.log</c>.</summary>
    internal static string LogPath => Path.Combine(Path.GetTempPath(), "UiTemplate.log");

    /// <summary>
    /// Append one timestamped line.  Logging must never be the reason the app
    /// fails, so every failure here is swallowed.
    /// </summary>
    internal static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // A full disk or a locked profile must not take the app down.
        }
    }
}
