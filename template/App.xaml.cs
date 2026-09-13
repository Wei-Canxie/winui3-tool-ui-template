using System;
using Microsoft.UI.Xaml;

namespace UiTemplate;

/// <summary>
/// WinUI 3 application.  <c>App.xaml</c> declares only <c>XamlControlsResources</c>.
/// <see cref="OnLaunched"/> creates the settings object, the tray icon and the
/// shell window; the order matters because the tray menu needs the window to exist
/// before the user can click anything.
/// </summary>
public sealed partial class App : Application
{
    /// <summary>The live settings instance the whole app reads and writes.</summary>
    private AppSettings? _settings;

    private ShellWindow? _window;
    private TrayIcon? _tray;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            AppLog.Log("OnLaunched: starting");

            _settings = AppSettings.Load();

            // The tray icon is best effort: if Shell_NotifyIcon fails (for example
            // because the shell is busy during logon) TrayIcon simply reports itself
            // as unavailable and the window keeps working.
            _tray = new TrayIcon();
            _tray.ShowWindowRequested += ShowWindow;
            _tray.ToggleSampleRequested += () => _window?.ToggleSampleFeature();
            _tray.ExitRequested += ExitApp;

            _window = new ShellWindow(_settings);
            _window.Closed += (_, _) => _window = null;
            _window.Activate();

            AppLog.Log("OnLaunched: shell window activated");
        }
        catch (Exception ex)
        {
            // Nothing else can report this: the window either does not exist yet or
            // just failed to initialise.
            AppLog.Log($"OnLaunched failed: {ex}");
            throw;
        }
    }

    /// <summary>Bring the (possibly hidden) shell window back from the tray.</summary>
    private void ShowWindow()
    {
        try
        {
            _window?.ShowFromTray();
        }
        catch (Exception ex)
        {
            AppLog.Log($"ShowWindow failed: {ex.Message}");
        }
    }

    /// <summary>Shut everything down and leave the process.</summary>
    private void ExitApp()
    {
        try
        {
            _tray?.Dispose();
            AppearanceManager.Cleanup();
            _settings?.Save();
        }
        catch (Exception ex)
        {
            AppLog.Log($"ExitApp cleanup failed: {ex.Message}");
        }

        Environment.Exit(0);
    }
}
