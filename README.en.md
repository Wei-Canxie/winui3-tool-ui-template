# WinUI 3 Desktop Tool UI Template

English | [中文](README.md)

A **ready-to-follow UI design spec for WinUI 3 desktop tools**: custom-drawn title bar + sidebar
navigation shell + multi-page settings panel + adjustable appearance (theme / background image /
Mica / Acrylic / window opacity) — all taken from a tray-resident tool that actually ships.

Its purpose is concrete: **let another agent (or developer) build a UI in the same style from
scratch without re-discovering the same pitfalls.**

- 📘 **Leader handbook (start here)**: [README_leader.en.md](README_leader.en.md) ([中文](README_leader.md)) — design rationale, features, usage, troubleshooting and an agent acceptance checklist
- 📐 Design spec and code recipes: [docs/UI-DESIGN.en.md](docs/UI-DESIGN.en.md)
  ([中文](docs/UI-DESIGN.md))
- 🧪 Every conclusion is measured (raw numbers and reasoning included), not "this is probably how it works"

> This repository ships **the documentation plus a ready-to-build template project** (`template/`, verified
> to build with 0 warnings / 0 errors and to start successfully). The spec and template are derived from the
> MIT-licensed [OsuCursorWin](https://github.com/xyc-233/OsuCursirWin) project.

---

## Features in detail

### 1. Shell: custom title bar + sidebar navigation

- `ExtendsContentIntoTitleBar`, with a root layout of exactly two rows: **a 32px title bar** plus the
  content area.
- The title bar follows the **in-app theme** (dark `#2D2D2D` / light `#F3F3F3`) with alpha adjusted
  for window opacity.
- Minimize / maximize / close button colours are **synced to the in-app theme by hand** (Windows
  colours them from the system theme, so light mode otherwise gets white glyphs on a light bar),
  including hover and pressed states.
- `NavigationView` + `LeftCompact`: a 48px icon strip collapsed, 200px expanded; pages are routed by
  `Tag` and built in C# (`StackPanel` + `ScrollViewer`), with scroll offsets cached and restored.
- Closing hides the window and the app lives in the tray:
  `AppWindow.Closing → e.Cancel = true; AppWindow.Hide()`.

### 2. The sidebar (the most expensive part of this design)

- **One full-height background with 12px rounding on the content side**, square against the window
  edge; the rounding uses a Composition `CreateRectangleClip` (XAML's `RectangleGeometry` has no
  `RadiusX/RadiusY`).
- The NavigationView template's own insets are flattened (the pane carries a 3px margin plus a 1px
  host border), leaving a 1px inset so the sidebar meets the title bar and the window bottom exactly.
- **Collapse animation**: the template's own close lasts 120ms *and* shrinks the pane to 48px at the
  same time, so its slide is invisible. This design refuses to fight the template and animates only
  the pane's own width (same 120ms, same KeySpline), so both curves shrink together — measured at
  118ms end to end, with no flicker and no jump.
- The rounding clip has a zero-size guard: a non-positive layout box is never clipped, which prevents
  "the whole sidebar vanishes for a few frames".

### 3. Settings interaction: draft + Apply / Cancel

- Every control change **only writes the draft** and marks it dirty; a floating
  **Apply / Cancel changes** card appears bottom-right (8px radius, translucent themed background,
  `MinWidth = 96`, Apply uses the system `AccentButtonStyle`).
- The card floats over the content instead of owning a layout row — otherwise the window gains a band
  at the bottom that neither the sidebar nor the backdrop covers.
- **Apply**: mirror the draft onto the live instance → engine side effects → appearance and title bar
  → persist → snapshot.
- **Cancel**: roll the draft back from the snapshot and rebuild the current page.
- Hard rule: control handlers must **never** call `Save()`, or pending values reach disk and any
  runtime watcher loads them, defeating the draft model.

### 4. Control conventions

- Numeric settings use one row shape: `label | slider | text box | − / +`, with columns of
  110 / * / 70 / auto.
- Buttons are exactly 32×32, font size 16, with centred `TextBlock` content; the minus sign is
  U+2212 `−`, not a hyphen.
- The text box may exceed the slider range; the slider is for fast dragging and keeps
  `SmallChange/LargeChange/StepFrequency` in sync with the step.
- Enums use horizontal `RadioButton`s, booleans use `ToggleSwitch`, section headers use
  `FontSize = 20 / SemiBold`.
- When a setting is meaningless in the current mode (e.g. window opacity is fixed at 100% under
  Mica), **disable the whole row and rewrite its label** instead of leaving a draggable no-op.

### 5. Appearance system

| Mode | Implementation notes |
| --- | --- |
| Theme | `RequestedTheme`; "follow system" reads `AppsUseLightTheme` from the registry |
| Default background | image + GDI+ gaussian blur (radius 0–255) → `WriteableBitmap` → `ImageBrush`, blurred off the UI thread |
| Mica | `MicaController` + `SystemBackdropConfiguration` (`MicaBackdrop` and the DWM attribute both proved unreliable) |
| Acrylic | `DesktopAcrylicController` |
| Window opacity | `WS_EX_LAYERED` + `SetLayeredWindowAttributes`; a WinUI 3 window has no `Opacity` property |

- `WS_EX_LAYERED` stays set at all times (MicaController needs it).
- Under Mica/Acrylic the opacity is **pinned to 255** and the slider is locked (the backdrop owns the
  window surface); the title bar and sidebar become fully opaque with it.

### 6. Persistence and diagnostics

- Settings live in one place: `%LOCALAPPDATA%\<App>\settings.json` (a shared path prevents two
  components from overwriting each other's file).
- External changes are picked up with a `FileSystemWatcher` plus a 300ms debounce; the settings window
  itself never writes while editing.
- Logs go to `%TEMP%\<App>.log`; geometry and animation problems are solved by sampling real
  coordinates with a `DispatcherQueueTimer` + `TransformToVisual`, then removing the instrumentation.

### 7. Project and runtime

- Unpackaged (`WindowsPackageType = None`) and self-contained
  (`WindowsAppSDKSelfContained = true`), so no runtime install is required.
- `AppxMSBuildToolsPath` must point at a local VS2022 install, or the CLI's `dotnet build` fails in
  `PriGen`.
- A self-contained build **must be launched from `bin\...\<RID>\`** (the lone exe in the project root
  exits instantly with `0x8000801A`). Kill the old instance with an elevated `taskkill` before
  rebuilding, or the build fails with MSB3027/MSB3021.

---

## The template project: `template/`

Besides the documentation, the repository contains a **compilable, runnable WinUI 3 skeleton** that turns this
design into code. All business logic of the original tool (cursor engine, sounds, services) has been stripped;
only the generic shell remains:

| File | Purpose |
| --- | --- |
| `UiTemplate.csproj` | Unpackaged + self-contained WASDK project config (including the `AppxMSBuildToolsPath` you must point at your own Visual Studio) |
| `app.manifest` | PerMonitorV2 DPI; deliberately `asInvoker` (no forced UAC prompt) |
| `App.xaml` / `App.xaml.cs` | Startup order: tray icon → main window → activate, all wrapped in try/catch with logging |
| `ShellWindow.cs` | The shell: custom title bar with theme-synced caption buttons, NavigationView left-compact pane, sidebar rounding and collapse animation, the draft + floating Apply/Cancel card, the numeric row control |
| `AppearanceManager.cs` | Theme / window opacity (`WS_EX_LAYERED`) / Mica / Acrylic / background image with gaussian blur |
| `AppSettings.cs` | Settings model: `Load` / `Save` / `Clone` / `CopyFrom`, persisted to `%LOCALAPPDATA%\UiTemplate\settings.json` |
| `TrayIcon.cs` | Win32 tray: show window / toggle the demo feature / exit |
| `AppLog.cs` | Logs to `%TEMP%\UiTemplate.log` |

Verification status: after deleting `bin/obj`, `dotnet build -c Release` reports **0 warnings / 0 errors**, and the
output `bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\UiTemplate.exe` was launched successfully (tray
registration, window creation and activation all logged).

```bash
cd template
dotnet build -c Release
# launch from the output directory; never copy the exe elsewhere on its own
bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/UiTemplate.exe
# kill the old instance before rebuilding, or the locked exe fails the build
taskkill /F /IM UiTemplate.exe
```

Adapting it to your own tool: change `AssemblyName` / `RootNamespace` and the window title → replace the three
sample pages in `ShellWindow.cs` (General / Appearance / Advanced) → add or remove fields in `AppSettings` → only
switch `app.manifest` to `requireAdministrator` if the tool genuinely needs elevation.

## Using it as a template

0. Want a code starting point straight away? Copy the whole `template/` directory and rename it (see the
   section above).
1. Prefer building it yourself? Read Section 2 of [docs/UI-DESIGN.en.md](docs/UI-DESIGN.en.md) and create
   the project from the `.csproj` / `app.manifest` shown there.
2. Follow the ten-step recipe in Section 10: shell → navigation → sidebar → settings model → controls
   → appearance → self-test.
3. Section 11 is a symptom-to-cause table — check it first when something behaves oddly.
4. For ground truth, read the WinUI templates straight out of the NuGet package:
   `~/.nuget/packages/microsoft.windowsappsdk.winui/<ver>/lib/net6.0-windows10.*/Microsoft.WinUI/Themes/generic.xaml`

## Known limitations

- The spec is measured on **Win11 / WASDK 2.4 (WinUI package resolves to 2.3.6) / .NET 8**. Older
  releases (e.g. WASDK 1.5) lack APIs such as `MicaController.SystemBackdropConfiguration` and
  `GaussianBlurEffect`.
- The sidebar/title-bar alignment and animation conclusions depend on the current NavigationView
  template. If Microsoft changes those insets or timings, re-measure per Section 9 and adjust the
  constants.
- For high-DPI / multi-monitor scaling, verify pixels the same measured way (this spec contains no
  scaling conversion logic).

## License and credits

MIT. Code snippets come from the MIT-licensed
[OsuCursorWin](https://github.com/xyc-233/OsuCursirWin) (Copyright (c) 2022 solstice23).
