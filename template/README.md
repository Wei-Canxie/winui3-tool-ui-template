# UiTemplate — a WinUI 3 desktop-tool skeleton

A minimal, self-contained starting point for a WinUI 3 *desktop tool*: a small window
with a custom title bar, a sidebar, a few settings pages and a live appearance section.
It implements the design in [`../docs/UI-DESIGN.en.md`](../docs/UI-DESIGN.en.md)
(`../docs/UI-DESIGN.md` is the Chinese edition). Nothing here is product-specific, so you
rename it and start filling in pages.

## File map

| File | What is in it |
| --- | --- |
| `UiTemplate.csproj` | Unpackaged, self-contained WinUI 3 project (net8.0-windows10.0.19041.0). |
| `app.manifest` | PerMonitorV2 DPI awareness; `asInvoker`, so no UAC prompt. |
| `App.xaml` / `App.xaml.cs` | `XamlControlsResources`, then `OnLaunched` builds settings → tray → window inside a try/catch. |
| `AppLog.cs` | Timestamped lines to `%TEMP%\UiTemplate.log`; never throws. |
| `AppSettings.cs` | The persisted settings: `Load` / `Save` / `Clone` / `CopyFrom` over `%LOCALAPPDATA%\UiTemplate\settings.json`. |
| `AppearanceManager.cs` | Theme, window opacity (`WS_EX_LAYERED`), Mica/Acrylic, and the background image with its GDI+ gaussian blur. |
| `TrayIcon.cs` | Tray icon over raw `Shell_NotifyIcon`, with a Show / Toggle / Exit menu. |
| `ShellWindow.cs` | The window: title bar, `NavigationView`, the three pages, the numeric row and the floating Apply/Cancel card. |

## Build and run

```bash
cd template
dotnet build -c Release
```

Then launch the exe **from the bin output directory**, not from the project root:

```
bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\UiTemplate.exe
```

`WindowsAppSDKSelfContained` is on, so the Windows App SDK runtime DLLs live next to the
exe. A copy placed anywhere else (for example one dropped into the project root) finds no
runtime and exits immediately with `0x8000801A`.

A running instance locks the exe, so rebuilds fail with MSB3027/MSB3021 — kill it first:

```bash
taskkill /F /IM UiTemplate.exe
```

The app is a tray tool: closing the window hides it, so exit through the tray menu. The
settings are written only when you press **Apply**, or on exit.

## Adapting it to a new tool

1. **Rename**: change `RootNamespace`, `AssemblyName` and `Version` in `UiTemplate.csproj`,
   then rename the namespace in every `.cs` file and `x:Class="UiTemplate.App"` in `App.xaml`.
2. **Adjust `AppxMSBuildToolsPath`** in the `.csproj` if Visual Studio is not installed at
   `C:\Program Files\Microsoft Visual Studio\2022\Community\`. The dotnet CLI does not ship
   the Appx/PRI MSBuild tasks, so PRI generation needs them.
3. **Replace the three pages**: edit the `Strings` block at the top of `ShellWindow.cs` (all
   user-visible text lives there, one block per page) and the matching `Build*Page()`
   methods. Add a `NavigationViewItem` per page and route it by `Tag`.
4. **Add settings**: add a property to `AppSettings`, edit it in a control handler **through
   the draft** (`_settings`, never `_liveSettings` and never `Save()`), then call
   `MarkDirty()`. `CopyFrom` uses a JSON round-trip, so a new property needs no extra wiring.
5. **Wire what your tool actually does** from `ApplyPendingChanges`: mirror the draft onto the live instance, then update your engine.
