# A UI Design Spec for WinUI 3 Desktop Tools

> This spec is distilled from a shipped WinUI 3 desktop tool (an osu! cursor replacer). Its goal is
> to let you build the UI of "a tray-resident Windows desktop tool that needs a multi-page settings
> panel and a custom look" from scratch.
>
> It is written so that another AI agent can follow Section 10 step by step and end up with a UI
> that looks and behaves the same.
>
> All snippets are minimal and directly usable; names and structure follow the reference
> implementation.

---

## 1. Goals and design stance

| Stance | What it looks like |
| --- | --- |
| Custom-drawn title bar | `ExtendsContentIntoTitleBar`; the app draws its own 32px title bar row, coloured by the **in-app** theme rather than the system theme |
| Sidebar shell | `NavigationView` + `LeftCompact`: a 48px icon strip collapsed, 200px expanded, rounded, with expand/collapse animation |
| Pages as code | Settings pages are `StackPanel` + `ScrollViewer` built in C#, not XAML files, so controls can be generated conditionally |
| Controllable edits | Every change goes into a draft first; a floating **Apply / Cancel changes** card appears bottom-right, and only Apply touches runtime state and disk |
| Adjustable appearance | Theme (follow system / light / dark), background image + gaussian blur, Mica, Acrylic, window opacity |
| Quiet operation | Closing the window only hides it; the tray icon stays. Editing settings never interrupts a drag |
| Diagnosable | Everything is logged to `%TEMP%\<App>.log`; geometry and animation problems are solved from measured data |

Three rules that were learned the hard way:

1. **Never do heavy work for live preview.** An early version rebuilt the page on every slider tick;
   drags broke and everything stuttered. Sliders now only write to memory and mark the draft dirty;
   the real work happens on Apply.
2. **Never fight the system template for the same property.** Section 4 explains how the template's
   120ms collapse also shrinks the pane, and how holding its clip property frame by frame makes the
   whole sidebar flicker. Animate one property you own instead.
3. **Measure every geometry problem.** WinUI template insets (e.g. NavigationView's pane has a 3px
   margin plus a 1px host border) are invisible in source; only `TransformToVisual` coordinates
   logged to a file will find them.

---

## 2. Project setup (unpackaged WinUI 3)

Key `.csproj` properties:

```xml
<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
<TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
<UseWinUI>true</UseWinUI>
<UseWPF>false</UseWPF>
<WindowsPackageType>None</WindowsPackageType>          <!-- unpackaged -->
<EnableMsixTooling>false</EnableMsixTooling>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>  <!-- no runtime install needed -->
<Nullable>enable</Nullable>
<ApplicationManifest>app.manifest</ApplicationManifest>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<!-- the dotnet CLI does not ship the PRI/Appx tasks; point them at an installed VS2022,
     otherwise PriGen fails -->
<AppxMSBuildToolsPath>C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\</AppxMSBuildToolsPath>
```

Dependencies: `Microsoft.WindowsAppSDK` (2.4.0 in this implementation) + `Microsoft.Windows.SDK.BuildTools`.
If you need WinForms/GDI, add `<FrameworkReference Include="Microsoft.WindowsDesktop.App.WindowsForms" />`.

`app.manifest`: PerMonitorV2 DPI, plus `requireAdministrator` only if you truly need elevation.

> **Launch trap you must know**: with a self-contained build the windowing runtime DLLs live in
> `bin\<Platform>\<Config>\<TFM>\<RID>\`, so **the lone exe copied to the project root will not run**
> (it exits immediately with `0x8000801A`). Always launch from the output directory. Before testing a
> new build, kill the old instance with an elevated `taskkill /F /IM <App>.exe`, or `dotnet build`
> fails with MSB3027/MSB3021 (file locked).

---

## 3. Window shell: custom title bar + NavigationView

### 3.1 The root layout has exactly two rows

```csharp
ExtendsContentIntoTitleBar = true;

var root = new Grid();
root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // 32px title bar
root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // content

_titleBarRoot = new Border { Height = 32, Background = GetTitleBarBrush() };
_titleBarText = new TextBlock { Text = Title, VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(12, 0, 0, 0), FontWeight = FontWeights.SemiBold };
_titleBarRoot.Child = _titleBarText;
Grid.SetRow(_titleBarRoot, 0);
```

- The title bar is a fixed-height 32px `Border` holding the app name, 12px from the left.
- **Do not** add extra rows or spacing for the title bar: every "sidebar doesn't line up" bug in the
  reference implementation came from extra pixels here.

### 3.2 Caption buttons must follow the in-app theme

Windows colours minimize/maximize/close from the **system** theme, so a light in-app theme gives you
white glyphs on a light title bar. Sync them whenever appearance is applied:

```csharp
var titleBar = AppWindow.TitleBar;
var isDark = IsDarkTheme();

titleBar.ButtonBackgroundColor = Colors.Transparent;              // blend into the custom title bar
titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
titleBar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
titleBar.ButtonHoverBackgroundColor = isDark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
                                             : Color.FromArgb(0x18, 0x00, 0x00, 0x00);
titleBar.ButtonPressedBackgroundColor = isDark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
                                               : Color.FromArgb(0x10, 0x00, 0x00, 0x00);
```

The title bar background is app-drawn: `#2D2D2D` dark, `#F3F3F3` light, alpha adjusted for window
opacity.

### 3.3 Tray tools: closing hides

```csharp
AppWindow.Closing += (_, e) => { e.Cancel = true; AppWindow.Hide(); };
```

### 3.4 The navigation shell

```csharp
var nav = new NavigationView
{
    IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
    IsSettingsVisible = false,
    PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact,
    OpenPaneLength = 200,
    CompactPaneLength = 48,
    IsPaneOpen = false,
};
nav.MenuItems.Add(new NavigationViewItem { Content = "Appearance", Icon = new SymbolIcon(Symbol.View), Tag = "appearance" });
// …same shape for the other pages; Tag is the route
```

On navigation, **rebuild** the page (rather than caching control instances) and cache/restore the
scroll offset:

```csharp
nav.SelectionChanged += (s, e) =>
{
    if (nav.SelectedItem is NavigationViewItem item && item.Tag is string tag)
    {
        CacheScrollPosition();                                // remember the old page's VerticalOffset
        if (_currentPage is IDisposable old) old.Dispose();   // unsubscribe, avoid leaks
        _currentTag = tag;
        _currentPage = BuildPage(tag);
        nav.Content = _currentPage;
        RestoreScrollPosition(tag, _currentPage);
    }
};
```

`DisposablePage` is a tiny `StackPanel` subclass that collects the page's unsubscribe actions and runs
them when the page is dropped:

```csharp
internal sealed class DisposablePage : StackPanel, IDisposable
{
    private readonly List<Action> _unsubscribeActions = new();
    public void RegisterUnsubscribe(Action action) => _unsubscribeActions.Add(action);
    public void Dispose() { foreach (var a in _unsubscribeActions) { try { a(); } catch { } } _unsubscribeActions.Clear(); }
}
```

> After a theme change, rebuild the current page, or the brushes already created inside the controls
> keep the old colours.

---

## 4. Sidebar visuals and animation (with measurements)

### 4.1 Visuals: a full-height column with 12px rounding on the content side

```csharp
var splitView = FindSplitViewPane(_nav);            // walk the visual tree for the SplitView
if (splitView?.Pane is FrameworkElement pane)
{
    pane.Background = new SolidColorBrush(isDark ? Color.FromArgb(255, 0x2D, 0x2D, 0x2D) : Colors.White);

    // Rounding via a Composition clip: XAML's RectangleGeometry has no RadiusX/RadiusY
    var clip = compositor.CreateRectangleClip();
    clip.TopLeftRadius = clip.BottomLeftRadius = new Vector2(0, 0);      // flush with the window edge
    clip.TopRightRadius = clip.BottomRightRadius = new Vector2(12, 12);  // rounded toward the content
    SyncClipBounds(clip, pane);
    ElementCompositionPreview.GetElementVisual(pane).Clip = clip;
    pane.SizeChanged += (_, _) => SyncClipBounds(clip, pane);
}

static void SyncClipBounds(RectangleClip clip, FrameworkElement pane)
{
    // Critical: do not update for a non-positive size — clipping to an empty box makes the whole
    // sidebar invisible for several frames.
    if (!(pane.ActualWidth > 0) || !(pane.ActualHeight > 0)) return;
    clip.Left = 0f; clip.Top = 0f;
    clip.Right = (float)pane.ActualWidth; clip.Bottom = (float)pane.ActualHeight;
}
```

### 4.2 Remove the template's extra insets, keep 1px

NavigationView's `PaneContentGrid` (i.e. `SplitView.Pane`) carries a top/bottom margin, and an outer
host `Border` sits 1px inside the pane column: measured as "4px of empty space above and below the
sidebar". Zero the pane's own margin and flatten the ancestors between it and the SplitView:

```csharp
pane.Margin = new Thickness(0);                       // the template's 3px margin
FlattenPaneAncestors(pane, splitView);                // clear Margin/Padding/Border/Background on ancestors
// Result: only the template host Border's 1px inset remains (kept on purpose; it reads as a cleaner edge)
```

### 4.3 Expand/collapse animation: animate exactly one property

Conclusions from measured logs, not guesses:

- Expand: the template slides `PaneClipRectangleTransform.TranslateX` from
  `-(OpenPaneLength-CompactPaneLength)` to 0 over 350ms with KeySpline `0.1,0.9 0.2,1.0`
  (quick off the mark, easing out).
- Collapse: the same property in reverse, but only **120ms**; worse, the closed state snaps the pane
  width to `CompactPaneLength` at the same time, so a 48px-wide pane has nothing left to clip and the
  slide is invisible — which is why the collapse looks like a snap. `PaneClosing`'s `Cancel` does
  **not** stop it, and writing `IsPaneOpen` back to true in the property-changed callback leaves the
  NavigationView half-closed.
- Holding the template's clip window open (re-creating a hold animation every frame) fights the
  template's own animation and makes the sidebar flicker.

**What the reference implementation does**: leave the template's clip animation completely alone and
animate only the pane's own width, with the same duration and curve, so both shrink together and
nothing fights:

```csharp
private const int SidebarCloseMs = 120;                 // matches the template's collapse
private static readonly Point Spline1 = new(0.1, 0.9);  // the template's curve
private static readonly Point Spline2 = new(0.2, 1.0);

nav.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) =>
{
    if (nav.IsPaneOpen) ResetSidebarWidth();            // opening: drop the width pinned while closing
    else AnimateSidebarCollapse();
});

void AnimateSidebarCollapse()
{
    var splitView = FindSplitViewPane(_nav);
    var pane = splitView.Pane as FrameworkElement;
    var startWidth = pane.ActualWidth > splitView.CompactPaneLength ? pane.ActualWidth : splitView.OpenPaneLength;

    pane.Width = startWidth;                            // pin it, or the SplitView arranges it to the compact width
    var sb = new Storyboard();
    var anim = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };  // required for layout props
    anim.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = startWidth });
    anim.KeyFrames.Add(new SplineDoubleKeyFrame
    {
        KeyTime = TimeSpan.FromMilliseconds(SidebarCloseMs),
        KeySpline = new KeySpline { ControlPoint1 = Spline1, ControlPoint2 = Spline2 },
        Value = splitView.CompactPaneLength
    });
    Storyboard.SetTarget(anim, pane);
    Storyboard.SetTargetProperty(anim, "Width");
    sb.Children.Add(anim);
    sb.Begin();
}
```

Key points:

- `EnableDependentAnimation = true` is **mandatory**; without it, animations on layout properties like
  `Width` are silently ignored.
- On expand you must `pane.ClearValue(FrameworkElement.WidthProperty)`, or the pane stays 48px wide.
- The collapse duration must match the template's (same time window), otherwise the two curves cut
  each other off mid-motion.

---

## 5. Settings interaction model: a draft + Apply / Cancel

### 5.1 Why no live apply

Applying appearance on every control change also rebuilds the whole page (to re-colour controls), and
the rebuild destroys the slider being dragged: drags break and every tick costs a full page build.
Therefore:

- Control callbacks do only two things: write the value into the draft, and `MarkDirty()`.
- The real work happens when the user presses Apply (or during window initialisation).

### 5.2 The draft object

The settings window holds two `AppSettings` instances: `_liveSettings` (the one the runtime/engine
uses) and `_settings` (the draft the UI edits, produced by `Clone()` in the constructor), plus an
`_applied` snapshot used by "Cancel changes".

```csharp
internal AppSettings Clone()            // deep copy through JSON, no field-by-field copying
internal void CopyFrom(AppSettings o)   // overwrite every field (mirror the draft onto the live instance)
```

### 5.3 The floating card

Apply / Cancel is not a layout row (that leaves a band at the bottom of the window that neither the
sidebar nor the backdrop covers); it floats in the bottom-right corner of the content area:

```csharp
_applyBar = new Border
{
    HorizontalAlignment = HorizontalAlignment.Right,
    VerticalAlignment = VerticalAlignment.Bottom,
    Margin = new Thickness(0, 0, 24, 16),
    Padding = new Thickness(10, 8, 10, 8),
    CornerRadius = new CornerRadius(8),
    Background = GetFloatingBarBrush(),      // dark #E6 2D2D2D / light #F0 FFFFFF
    Child = buttons,                         // [Cancel changes] [Apply (AccentButtonStyle)]
    Visibility = Visibility.Collapsed,       // only shown while there are pending edits
};
Grid.SetRow(_applyBar, 1);                   // same row as the content, so the sidebar keeps full height
```

Button rules: `MinWidth = 96`; Apply uses the system `AccentButtonStyle` (from
`Application.Current.Resources`); Cancel comes first, Apply second.

### 5.4 The full apply/cancel flow

```csharp
private void ApplyPendingChanges()
{
    _liveSettings?.CopyFrom(_settings);                    // 1. mirror onto the live instance
    _engine?.ApplyCursorWidth(_settings.CursorWidth);      // 2. engine-side side effects
    ApplyAppearanceCore();                                 // 3. theme/backdrop/title bar/sidebar + rebuild page
    _settings.Save();                                      // 4. persist (JSON)
    _applied = _settings.Clone();                          // 5. new snapshot
    HideApplyBar();
}

private void CancelPendingChanges()
{
    _settings.CopyFrom(_applied);   // roll the draft back
    HideApplyBar();
    RebuildCurrentPage();           // rebuild so every control shows the rolled-back value
}
```

**Hard rule**: never call `Save()` from a control handler. Pending draft values would reach disk, and
a runtime with a `FileSystemWatcher` on the settings file would load them immediately, defeating the
whole model.

---

## 6. Control conventions

### 6.1 The numeric row: label | slider | text box | − / +

```csharp
private Grid BuildSliderWithTextBox(string label, double value, double sliderMin, double sliderMax,
                                    Action<double> apply, double step = 1.0, string format = "0.##",
                                    double? textMin = null, double? textMax = null)
{
    var grid = new Grid();                        // 4 columns: 110px label | * slider | 70px box | auto buttons
    var slider = new Slider { Minimum = sliderMin, Maximum = sliderMax, Value = value,
                              SmallChange = step, LargeChange = step * 10, StepFrequency = step };
    var valueBox = new TextBox { Text = value.ToString(format) };
    var minus = new Button { Content = new TextBlock { Text = "−", FontSize = 16,
                              HorizontalAlignment = HorizontalAlignment.Center },
                             Width = 32, Height = 32, Padding = new Thickness(0) };
    var plus  = new Button { Content = new TextBlock { Text = "+", FontSize = 16, … }, Width = 32, Height = 32 };
    // slider.ValueChanged / text box lost focus / ± clicks → apply(v) → the caller calls MarkDirty()
}
```

Conventions:

- The minus sign is U+2212 `−` (the MINUS SIGN, not a hyphen): it sits visually centred; `+` is the plain plus.
- Buttons are exactly 32×32, font size 16, `Padding = 0`, content is a centred `TextBlock`.
- The text box may exceed the slider range (`textMin/textMax`); the slider is only for fast dragging.
- A header label shows "value + unit" and updates live (e.g. `Background image opacity: 80%`).

### 6.2 Everything else

| Case | Control | Note |
| --- | --- | --- |
| Enums (theme, backdrop) | horizontal `RadioButton`s | put the descriptive part in brackets, e.g. `Mica (云母)` |
| Booleans | `ToggleSwitch` | `OnContent/OffContent = "On"/"Off"` |
| Standalone toggles | `CheckBox` | |
| Section header | `TextBlock { FontSize = 20, FontWeight = SemiBold }` | |
| Sliders that used to be live | only `MarkDirty()`, applied later | see Section 5 |

When a setting is meaningless in the current mode (e.g. window opacity is pinned to 100% under
Mica/Acrylic), **disable the whole row and rewrite its label** instead of leaving a draggable control
that does nothing:

```csharp
foreach (var child in row.Children) if (child is Control c) c.IsEnabled = !locked;
label.Text = locked ? "Window opacity: 100% (fixed under Mica/Acrylic)" : $"Window opacity: {v:P0}";
```

> `IsEnabled` lives on `Control`; `UIElement` does not have it (so `Grid`/`StackPanel` cannot be
> disabled directly) — disable the child controls one by one.

---

## 7. Appearance system

| Mode | Implementation | Notes |
| --- | --- | --- |
| Theme | `root.RequestedTheme = Light/Dark/Default` | "follow system" reads `AppsUseLightTheme` from the registry |
| Default background | image + GDI+ gaussian blur (radius 0–255) → `WriteableBitmap` → `ImageBrush` | blur off the UI thread, then hand it to XAML |
| Mica | `MicaController` + `SystemBackdropConfiguration` (`ICompositionSupportsSystemBackdrop`) | needs `using WinRT;`; dispose it (there is no `Close()`) |
| Acrylic | `DesktopAcrylicController` | same shape |
| Window opacity | `WS_EX_LAYERED` + `SetLayeredWindowAttributes` | a WinUI 3 window has no `Opacity` property |

The opacity details matter:

```csharp
// WS_EX_LAYERED stays set at all times (MicaController needs it)
if ((exStyle & WS_EX_LAYERED) == 0) { SetWindowLong(...WS_EX_LAYERED...); SetWindowPos(...SWP_FRAMECHANGED...); }

// Default mode: the slider drives alpha.  Mica/Acrylic: pin to 255, the backdrop owns the surface.
byte alpha = settings.BackgroundBlur == BlurMode.Default
    ? (byte)Math.Clamp(settings.WindowOpacity * 255, 25, 255)
    : (byte)255;
SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
```

Other verified findings:

- `Window.SystemBackdrop = new MicaBackdrop()` and `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)`
  both **fail** on Win11 here (the latter turns the window pure black). Use `MicaController`.
- While a backdrop is active, keep the title bar and sidebar fully opaque too, otherwise you get a
  translucent title bar over opaque content.

---

## 8. Settings persistence and external changes

```csharp
// %LOCALAPPDATA%\<App>\settings.json
private static string SettingsPath => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "<App>", "settings.json");

internal static AppSettings Load() { /* deserialize + fall back when the file/paths are stale */ }
internal void Save() { /* create the directory, write indented JSON */ }
```

- The path must be **shared by the whole app** (an early bug had the engine and the settings window
  writing different files).
- If the runtime needs to notice external edits, use a `FileSystemWatcher` with a 300ms debounce; but
  the settings window itself must not write while editing (Section 5.4).

---

## 9. Logging and diagnostics

```csharp
internal static class AppLog
{
    internal static string LogPath => Path.Combine(Path.GetTempPath(), "<App>.log");
    internal static void Log(string message) { /* append one timestamped line; swallow failures */ }
}
```

Never guess geometry/animation problems — measure them:

```csharp
// sample every 16ms and log absolute coordinates
var timer = DispatcherQueue.CreateTimer();
timer.Interval = TimeSpan.FromMilliseconds(16);
timer.Tick += (_, _) =>
{
    var y = pane.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
    AppLog.Log($"t{tick} paneY={y:0.#} w={pane.ActualWidth:0.#} vis={pane.Visibility}");
    // to reproduce interactions, click buttons through ButtonAutomationPeer + IInvokeProvider
};
timer.Start();
```

Delete the temporary instrumentation afterwards (`grep` for the marker string and confirm).

---

## 10. Step-by-step recipe for a new tool

1. **Create the project** with the `.csproj` and `app.manifest` from Section 2 (skip
   `requireAdministrator` unless you really need it — otherwise every launch prompts UAC).
2. **Write the app shell**: `App.xaml` holds only `XamlControlsResources`; in `OnLaunched` create, in
   order: background service/engine → tray icon → settings window → `Activate()`, all inside
   try/catch with logging.
3. **Build the window**: root `Grid` with two rows (32px title bar / content),
   `ExtendsContentIntoTitleBar = true`, implement `GetTitleBarBrush()` and
   `ApplyCaptionButtonColors()`, and refresh both on theme changes.
4. **Add navigation**: `NavigationView(LeftCompact, 48/200)`, one page per feature, `Tag` routing,
   pages built on demand (`DisposablePage` + `ScrollViewer` + scroll caching).
5. **Style the sidebar**: pane background and 12px rounding on the content side (keep the zero-size
   guard), flatten the template insets, wire up the collapse animation (animate the pane width only).
6. **Build the settings model**: `AppSettings` (properties + `Load`/`Save`/`Clone`/`CopyFrom`), a draft
   in the settings window with `MarkDirty()`, and the floating Apply/Cancel card.
7. **Lay out controls**: numeric rows from Section 6.1, `RadioButton` for enums, `ToggleSwitch` for
   booleans; disable-and-relabel rows that are meaningless in the current mode.
8. **Add appearance**: theme + default background (image/blur/opacity) + Mica/Acrylic + window opacity
   (Section 7). Pin opacity and lock the slider while a backdrop is active.
9. **Self-test**: run it, change every setting, press Apply, close and reopen to confirm persistence;
   when geometry looks off, instrument it as in Section 9.
10. **Finish**: remove the temporary diagnostics, confirm a clean build, then commit.

---

## 11. WinUI 3 pitfalls

| Symptom | Cause / fix |
| --- | --- |
| The exe copied to the project root exits instantly (`0x8000801A`) | self-contained runtime DLLs live in the bin output; launch from there |
| `dotnet build` fails with MSB3027/MSB3021 | an old instance holds the exe; elevated `taskkill` first |
| Minimize/close buttons are invisible in light mode | system button colours follow the system theme; set `AppWindow.TitleBar.Button*Color` |
| A window has no `Opacity` property | use `WS_EX_LAYERED` + `SetLayeredWindowAttributes` |
| Mica does nothing / window turns black | use `MicaController`; `MicaBackdrop` and the DWM attribute are unreliable |
| Animations on layout properties do nothing | set `EnableDependentAnimation = true` |
| `UIElement` has no `IsEnabled` | disable the `Control` children individually |
| `RectangleGeometry` has no `RadiusX/RadiusY` | use a Composition `CreateRectangleClip` |
| `Visual` has no `Shadow` property | use a `SpriteVisual` + `CompositionDropShadow` |
| A storyboard targets the wrong property path | use the dependency property name directly: `"Width"`, `"TranslateX"` |
| Thin gaps above/below the sidebar | the template pane has a 3px margin plus a 1px host border (Section 4.2) |
| The collapse "has no animation" | the template's close is 120ms and shrinks the pane at the same time (Section 4.3) |
| Dragging a slider breaks | never apply/rebuild on every tick; use the draft model |
| Rounding a panel makes it disappear | never let a Composition clip have a zero-sized box |

---

## 12. References

- The reference implementation of this spec: **OsuCursorWin** (an osu! cursor replacer, WinUI 3 port).
- WinUI 3 template source ships inside the NuGet package and is readable:
  `~/.nuget/packages/microsoft.windowsappsdk.winui/<ver>/lib/net6.0-windows10.*/Microsoft.WinUI/Themes/generic.xaml`
  (invaluable when an animation or inset is not visible in your own code)

License: MIT. Code snippets come from the MIT-licensed OsuCursorWin project
(Copyright (c) 2022 solstice23).

---

## 13. Reference implementation

`template/` is a compilable skeleton of this spec (namespace and assembly name
`UiTemplate`): `dotnet build -c Release` inside it reports 0 errors. It is the
step-by-step recipe of Section 10 in code, one file per step.

| File | Contents |
| --- | --- |
| `template/UiTemplate.csproj` | Unpackaged, self-contained WinUI 3 project (net8.0-windows10.0.19041.0); self-contained means it must be launched from `bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\` |
| `template/app.manifest` | PerMonitorV2 DPI awareness; `asInvoker`, so no UAC prompt |
| `template/App.xaml` / `App.xaml.cs` | `XamlControlsResources`; `OnLaunched` creates settings → tray → window, all in try/catch |
| `template/AppLog.cs` | Timestamped lines to `%TEMP%\UiTemplate.log`; never throws |
| `template/AppSettings.cs` | Settings persistence (`Load`/`Save`/`Clone`/`CopyFrom`) in `%LOCALAPPDATA%\UiTemplate\settings.json` |
| `template/AppearanceManager.cs` | Theme, window opacity (`WS_EX_LAYERED`), Mica/Acrylic, background image with its GDI+ gaussian blur |
| `template/TrayIcon.cs` | Tray icon over `Shell_NotifyIcon`: Show window / Toggle sample feature / Exit |
| `template/ShellWindow.cs` | The window: 32px title bar, `NavigationView` with three pages (General / Appearance / Advanced), the draft + Apply/Cancel model and the numeric row helper |
| `template/README.md` | Build and launch-path rules, close-hides behaviour, and the rename steps |

Copy the directory and rename it to start a new tool: change `RootNamespace` /
`AssemblyName`, adjust `AppxMSBuildToolsPath` if needed, then replace the `Strings`
block at the top of `ShellWindow.cs` and the three `Build*Page()` methods.
