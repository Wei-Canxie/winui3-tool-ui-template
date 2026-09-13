# WinUI 3 Desktop Tool UI Design Spec and Implementation Manual

[中文](README_leader.md) | English

> Version: 2026-09-13 ｜ Applies to: Windows 11 + .NET 8 + Windows App SDK 2.4 (the WinUI package resolves to 2.3.6)
> Source: the WinUI 3 implementation of a tray-resident tool that actually ships (an osu! cursor replacer, MIT).
> Every conclusion comes from measured logs, not from "this is probably how it should look".
> Companion repository: <https://github.com/Wei-Canxie/winui3-tool-ui-template> (documentation + a compilable template project)

---

## 0. How to read this document

**Human developers**: read Sections 1 and 2 first to establish the judgment criteria, then jump to Section 14 and follow the ten-step recipe; when something weird happens, check Section 13.

**AI agents**: treat Section 2 (the iron rules), Section 3 (project configuration), Section 14 (the recipe) and Section 16 (the acceptance checklist)
as hard constraints; Section 13 is a "symptom → cause → action" lookup table. Every constant, path and duration is given as an exact value in
the text, so do not invent values from experience.

**This whole UI in one sentence**: a sidebar-navigation shell that can collapse into a 48px icon strip, with a custom-drawn title bar and a settings
panel built on "changes land in a draft first and only take effect when you press Apply"; the look supports theme / background image / Mica / Acrylic / window opacity.

### 0.1 One-page overview

| Dimension | Value |
| --- | --- |
| Window | Unpackaged + self-contained, no runtime installation required |
| Root layout | Exactly two rows: a 32px custom-drawn title bar + the content area |
| Navigation | `NavigationView`, `LeftCompact`, 48px collapsed / 200px expanded |
| Sidebar | Full-column themed background, 12px rounding on the content side, 120ms width animation on collapse |
| Settings model | Draft object + a floating "Apply / Cancel changes" card in the bottom-right corner |
| Numeric controls | Label \| slider \| text box \| − / +, four columns 110 / * / 70 / auto |
| Appearance | Theme (follow system / light / dark), background image + gaussian blur, Mica, Acrylic, window opacity |
| Opacity implementation | `WS_EX_LAYERED` + `SetLayeredWindowAttributes` (a WinUI 3 window has no `Opacity`) |
| Settings storage | `%LOCALAPPDATA%\<App>\settings.json` |
| Logging | `%TEMP%\<App>.log`, plain append-only text |
| Lifetime | Closing the window = hiding it; the tray icon stays resident |
| Self-contained output size | 149MB / 88 DLLs (measured) |
| Clean build time | about 8.4s (measured, release, NuGet cache already restored) |

---

## 1. Design goals

1. **Look Windows-native**: custom-draw the title bar but keep the system buttons; sidebar navigation, rounding and translucent materials all match the system.
2. **Controllable edits**: every change in the settings panel lands in a draft first; it only really takes effect and gets written to disk when the user presses "Apply"; "Cancel changes" returns to the last applied state in one step.
3. **No stutter while editing**: dragging a slider must never do heavy work (no rebuilding pages, no writing to disk, no restarting the engine).
4. **Run silently**: resident in the tray, closing the window only hides it; logs go to a file, no dialogs.
5. **Diagnosable**: any "that looks wrong" problem must be locatable from measured data in the logs, never by guessing.

---

## 2. The five iron rules (each one has a real counter-example)

### Rule 1: Do not do heavy work for "live preview"

**Counter-example**: early versions called "apply appearance" on every slider tick, and applying the appearance rebuilt the entire settings page (in order to refresh control colors).
The result: the slider being dragged got destroyed → the drag broke; a full page build on every tick → visible stutter.

**What to do**: a control callback does exactly two things — write the draft, and `MarkDirty()`. The real application happens when the user presses "Apply".
Anything that needs live feedback (for example the numeric text on a label) can be updated in place, which is cheap.

### Rule 2: Do not fight the system template for the same property

**Counter-example**: to make the sidebar collapse "animated", we first rebuilt an animation every 16ms to hold down the template's clip property.
It ended up overwriting the template's own animation and vice versa, and the sidebar was **completely invisible** for a few frames at the beginning of the collapse before it came back.

**What to do**: the template's collapse lasts only 120ms and squeezes the pane to 48px at the same time (which is why the slide is invisible), but it is fast and stable.
The correct strategy is to **animate only one property that we fully control ourselves** (the pane's width), and to give it the same duration and curve
as the template, so both shrink in sync and neither overrides the other. See Section 7.3 for details.

### Rule 3: Measure every geometry problem, never speculate

**Counter-example**: there were thin gaps above and below the sidebar, and nothing in the source code explained them. Only after logging the absolute
`TransformToVisual` coordinates of every level of the visual tree did we pin it down: the template adds a 3px margin to the pane, and there is an outer host border that is off by 1px.

**What to do**: temporarily insert a `DispatcherQueue.CreateTimer()` (16ms) sampling loop that logs element coordinates / sizes / visibility,
then delete the instrumentation the moment the cause is found (use `grep` on the marker string to confirm nothing is left).

### Rule 4: System button colors must follow the in-app theme by hand

**Counter-example**: after switching to the light theme inside the app, the title bar was light, but the minimize / maximize / close buttons were still white — white buttons on a white background.

**What to do**: sync `AppWindow.TitleBar.Button*Color` every time the appearance is applied (see Section 5.2),
including the hover state, the pressed state and the unfocused state.

### Rule 5: A self-contained build can only be launched from the output directory

**Counter-example**: copying `bin\...\win-x64\<App>.exe` on its own into the project root and launching it — the process exits instantly with exit code `0x8000801A`.
The reason is the absence of the 88 runtime DLLs that live in the sibling directory.

**What to do**: always launch from the output directory; run `taskkill /F /IM <App>.exe` before switching to a new build,
otherwise the locked exe makes the build report MSB3027/MSB3021.

---

## 3. Project configuration

### 3.1 `.csproj`

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
    <RootNamespace>UiTemplate</RootNamespace>
    <AssemblyName>UiTemplate</AssemblyName>
    <UseWinUI>true</UseWinUI>
    <UseWPF>false</UseWPF>
    <WindowsPackageType>None</WindowsPackageType>       <!-- unpackaged -->
    <EnableMsixTooling>false</EnableMsixTooling>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>  <!-- no runtime install required -->
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Platforms>x86;x64;ARM64</Platforms>
    <RuntimeIdentifiers>win-x86;win-x64;win-arm64</RuntimeIdentifiers>
    <Platform>x64</Platform>
    <PlatformTarget>x64</PlatformTarget>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <!-- the dotnet CLI does not ship the PRI/Appx tasks; point at your installed VS2022 or PriGen fails -->
    <AppxMSBuildToolsPath>C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\</AppxMSBuildToolsPath>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="2.4.0" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.4654" />
  </ItemGroup>
</Project>
```

- When you need GDI/WinForms capabilities (tray icons, GDI+ blur, Win32 file dialogs), add the framework references:
  `<FrameworkReference Include="Microsoft.WindowsDesktop.App" />` and `...WindowsForms` (optional).
- `AppxMSBuildToolsPath` is a **machine-specific path**; when you hand this to someone else, they must change it to their own VS install path.
- The build may report `XamlCompiler warning WMC1509: No LocalAssembly parameter given during MarkupCompilePass2`;
  this is harmless (it means the WinUI package version in the NuGet cache does not exactly match the version declared by WASDK, and measurement shows it does not affect runtime behavior).

### 3.2 `app.manifest`

```xml
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAware xmlns="http://schemas.microsoft.com/SMI/2005/WindowsSettings">true/pm</dpiAware>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
    </windowsSettings>
  </application>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v2">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="asInvoker" />   <!-- only change to requireAdministrator when you actually need admin -->
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>
</assembly>
```

**Only use `requireAdministrator` if the tool genuinely needs administrator rights**: once it is enabled, every launch throws a UAC prompt,
and a non-elevated shell cannot start it at all (`start <exe>` fails silently and the process does not stay resident), which makes the debugging loop longer.

### 3.3 Build and run rules

```bash
# 1) Kill the old instance before switching to a new build (otherwise the locked exe → MSB3027/MSB3021)
taskkill /F /IM UiTemplate.exe          # elevated scenarios need a high-privilege shell

# 2) Build
dotnet build -c Release                 # measured at about 8.4s for a clean build

# 3) Launch from the output directory (do not copy the exe elsewhere)
bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/UiTemplate.exe
```

The minimum standard for confirming a successful launch: three records appear in `%TEMP%\<App>.log` — tray registered → window created → window activated.

---

## 4. Architecture and file responsibilities

The template project has 11 files (about 2467 lines across the .cs / .xaml / .csproj / README files; the table below packs `App.xaml` and `App.xaml.cs` into one row):

| File | Responsibility | Key points |
| --- | --- | --- |
| `UiTemplate.csproj` | Project configuration | Unpackaged + self-contained; `AppxMSBuildToolsPath` must be adjusted per machine |
| `app.manifest` | DPI and privileges | PerMonitorV2; `asInvoker` by default |
| `App.xaml` / `App.xaml.cs` | Application entry point | Holds nothing but `XamlControlsResources`; startup order: tray → window → activate, with try/catch and logging all the way through |
| `AppLog.cs` | Logging | Appends a timestamped line to `%TEMP%\<App>.log`, fails silently |
| `AppSettings.cs` | Settings model | Properties + `Load` / `Save` / `Clone` / `CopyFrom`, JSON persisted to `%LOCALAPPDATA%\<App>\settings.json` |
| `AppearanceManager.cs` | Appearance | Theme, window opacity (`WS_EX_LAYERED`), Mica/Acrylic, background image + gaussian blur |
| `ShellWindow.cs` | The window itself | Shell layout, custom-drawn title bar, navigation, sidebar, the draft/apply model, control factory |
| `TrayIcon.cs` | Tray | `Shell_NotifyIcon`; menu: show window / toggle the demo feature / exit |
| `README.md` | Usage notes | Build and run rules plus the adaptation steps |
| `.gitignore` | Repository hygiene | Ignores `bin/`, `obj/` (the self-contained output is 149MB — never commit it) |

The class layering:

```
App (Application)
 ├─ TrayIcon            ← Win32 message loop + menu
 └─ ShellWindow (Window)                ← all the UI logic
      ├─ AppSettings   (_liveSettings / _settings draft / _applied snapshot)
      └─ AppearanceManager (static)        ← theme / material / opacity
```

**Core concept**: `AppSettings` exists in memory in three copies at the same time — the one the runtime is using (`_liveSettings`),
the draft the UI is editing (`_settings`), and the snapshot of the last successful apply (`_applied`).
"Apply" mirrors the draft onto the runtime and refreshes the snapshot; "Cancel changes" copies the snapshot back into the draft.

---

## 5. Shell: the custom-drawn title bar

### 5.1 The root layout is strictly two rows

```csharp
ExtendsContentIntoTitleBar = true;

var root = new Grid();
root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 32px title bar
root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // content

_titleBarRoot = new Border { Height = 32, Background = GetTitleBarBrush() };
_titleBarText = new TextBlock
{
    Text = Title,
    VerticalAlignment = VerticalAlignment.Center,
    Margin = new Thickness(12, 0, 0, 0),
    FontWeight = FontWeights.SemiBold,
    Foreground = GetTitleBarForeground(),
};
_titleBarRoot.Child = _titleBarText;
Grid.SetRow(_titleBarRoot, 0);
```

Title bar background: dark `#2D2D2D`, light `#F3F3F3`, with alpha following the window opacity
(`titleBarOpacity = opacity <= 0.9 ? opacity + 0.1 : opacity`, which makes the title bar slightly crisper than the content).

**Do not turn floating buttons or a status bar into a third row**: the sidebar and the overlay both only cover the "content row",
so any extra row leaves a band at the bottom of the window that has neither the sidebar background nor the material over it (we have been burned by this).

### 5.2 System button colors (must follow the in-app theme)

```csharp
var titleBar = AppWindow.TitleBar;
var isDark = IsDarkTheme();

titleBar.ButtonBackgroundColor = Colors.Transparent;
titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
titleBar.ButtonInactiveForegroundColor = isDark ? Color.FromArgb(0xFF, 0x7A, 0x7A, 0x7A)
                                               : Color.FromArgb(0xFF, 0x8A, 0x8A, 0x8A);
titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
titleBar.ButtonHoverBackgroundColor = isDark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
                                             : Color.FromArgb(0x18, 0x00, 0x00, 0x00);
titleBar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
titleBar.ButtonPressedBackgroundColor = isDark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
                                               : Color.FromArgb(0x10, 0x00, 0x00, 0x00);
```

The dark/light decision must go through one shared function (reading the registry when following the system):

```csharp
using var key = Registry.CurrentUser.OpenSubKey(
    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
return key?.GetValue("AppsUseLightTheme") is int i && i == 0;
```

### 5.3 Closing the window hides it (tray tools)

```csharp
AppWindow.Closing += (_, e) => { e.Cancel = true; AppWindow.Hide(); };
```

Companion rule: the tray menu's "Show window" must do `AppWindow.Show(); Activate();`, and reopening must not rebuild the window (that way the draft state survives).

---

## 6. Navigation and pages

### 6.1 Navigation control parameters

```csharp
var nav = new NavigationView
{
    IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
    IsSettingsVisible = false,
    PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact,
    OpenPaneLength = 200,          // expanded width
    CompactPaneLength = 48,        // collapsed width (the icon strip)
    IsPaneOpen = false,            // collapsed by default
};
nav.MenuItems.Add(new NavigationViewItem { Content = "General",   Icon = new SymbolIcon(Symbol.View),  Tag = "general" });
nav.MenuItems.Add(new NavigationViewItem { Content = "Appearance", Icon = new SymbolIcon(Symbol.Target), Tag = "appearance" });
nav.MenuItems.Add(new NavigationViewItem { Content = "Advanced",  Icon = new SymbolIcon(Symbol.Setting), Tag = "advanced" });
```

### 6.2 Pages built in C# + subscription management

Pages are "rebuilt on every switch" (control instances are not cached), so event-subscription leaks have to be solved:
use a container that collects the unsubscribe actions.

```csharp
internal sealed class DisposablePage : StackPanel, IDisposable
{
    private readonly List<Action> _unsubscribeActions = new();
    public void RegisterUnsubscribe(Action action) => _unsubscribeActions.Add(action);

    public void Dispose()
    {
        foreach (var a in _unsubscribeActions) { try { a(); } catch { } }
        _unsubscribeActions.Clear();
    }
}
```

Switching pages and caching scroll positions:

```csharp
nav.SelectionChanged += (s, e) =>
{
    if (nav.SelectedItem is NavigationViewItem item && item.Tag is string tag)
    {
        CacheScrollPosition();                               // record the old page's VerticalOffset into the dictionary
        if (_currentPage is IDisposable old) old.Dispose();
        _currentTag = tag;
        _currentPage = BuildPage(tag);                       // returns a ScrollViewer
        nav.Content = _currentPage;
        RestoreScrollPosition(tag, _currentPage);
    }
};

FrameworkElement BuildPage(string tag)
{
    var page = new DisposablePage { Padding = new Thickness(16), Spacing = 8 };
    switch (tag)
    {
        case "general":    BuildGeneralPage(page);    break;
        case "appearance": BuildAppearancePage(page); break;
        case "advanced":   BuildAdvancedPage(page);   break;
    }
    var scroller = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    scroller.ViewChanged += (_, _) => _scrollCache[tag] = scroller.VerticalOffset;
    return scroller;
}
```

**The current page must be rebuilt after a theme change or a language change**, otherwise the brushes that have already been created (headings, card background) will not follow along.

---

## 7. The sidebar (the most expensive part)

### 7.1 Visuals: full-column background + 12px rounding on the content side

```csharp
private void SyncSidebarBackground()
{
    var splitView = FindSplitViewPane(_nav);        // find the SplitView recursively in the visual tree
    if (splitView?.Pane is not FrameworkElement pane) return;

    pane.Background = new SolidColorBrush(IsDarkTheme()
        ? Color.FromArgb(255, 0x2D, 0x2D, 0x2D)
        : Colors.White);

    // XAML's RectangleGeometry has no RadiusX/RadiusY, so rounding can only come from a Composition clip
    var clip = ElementCompositionPreview.GetElementVisual(pane).Compositor.CreateRectangleClip();
    clip.TopLeftRadius = clip.BottomLeftRadius = new Vector2(0, 0);       // flush against the window's left edge: square corners
    clip.TopRightRadius = clip.BottomRightRadius = new Vector2(12, 12);   // content side: rounded
    SyncClipBounds(clip, pane);
    ElementCompositionPreview.GetElementVisual(pane).Clip = clip;
    pane.SizeChanged += (_, _) => SyncClipBounds(clip, pane);
}

private static void SyncClipBounds(RectangleClip clip, FrameworkElement pane)
{
    // Critical: do not update while the size is not positive. Clipping to an empty rectangle makes the entire sidebar invisible for several frames.
    if (!(pane.ActualWidth > 0) || !(pane.ActualHeight > 0)) return;
    clip.Left = 0f;  clip.Top = 0f;
    clip.Right = (float)pane.ActualWidth;
    clip.Bottom = (float)pane.ActualHeight;
}
```

### 7.2 Geometry: erase the template's extra insets

Measured (absolute coordinates from the log): `NavigationView`'s `PaneContentGrid` (which is `SplitView.Pane`)
carries a 3px vertical margin, and there is an outer host `Border` that is off by 1px, showing up as "4px of empty space above and below the sidebar".

```csharp
pane.Margin = new Thickness(0);            // remove the template's 3px

// flatten Margin/Padding/BorderThickness/Background on every ancestor between the pane and the SplitView
private static void FlattenPaneAncestors(DependencyObject pane, DependencyObject? stopAt)
{
    var parent = VisualTreeHelper.GetParent(pane);
    while (parent != null && parent != stopAt)
    {
        switch (parent)
        {
            case Border b:
                b.Margin = new Thickness(0); b.Padding = new Thickness(0);
                b.BorderThickness = new Thickness(0);
                b.Background = new SolidColorBrush(Colors.Transparent);
                break;
            case Panel p:
                p.Margin = new Thickness(0);
                p.Background = new SolidColorBrush(Colors.Transparent);
                break;
            case ContentPresenter cp:
                cp.Margin = new Thickness(0);
                break;
        }
        parent = VisualTreeHelper.GetParent(parent);
    }
}
```

Result: only the 1px inset of the template's host `Border` is kept (deliberately — it makes the edge look cleaner).
Measured fit data: the navigation area is `y=32 h=639` (bottom edge 671) and the pane is likewise `y=32 h=639` (bottom edge 671), so the two coincide exactly.

### 7.3 Collapse animation: animate one property only

**What the template actually does (measured, not what the documentation says)**:

- Expand: `PaneClipRectangleTransform.TranslateX` slides from `-(OpenPaneLength-CompactPaneLength)`
  (in this configuration = −152) to 0, over **350ms**, with KeySpline `0.1,0.9 0.2,1.0` (fast first, slow later).
- Collapse: the same property runs in reverse and lasts only **120ms**, and the closed state squeezes the pane width down to `CompactPaneLength` in the same frame;
  a 48px-wide pane that is then clipped shows no visible slide at all — which is why the collapse "looks like it has no animation".
- The `Cancel` in the `PaneClosing` event does **not** stop that close; setting `IsPaneOpen` back to `true`
  from a property-changed callback leaves NavigationView in a strange half-closed state (the pane width gets stuck at something like 130/104).
- Hard-holding the template's clip property (rebuilding the animation every frame) overwrites the template's animation and vice versa → the sidebar flickers.

**The approach we adopted**: never touch the template's clip animation; animate only the pane's own width, with the same duration and curve as the template:

```csharp
private const int SidebarCloseMs = 120;                       // same time window as the template's collapse
private static readonly Point SplineStart = new(0.1, 0.9);    // the template's curve
private static readonly Point SplineEnd   = new(0.2, 1.0);

_nav.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) =>
{
    if (_nav.IsPaneOpen) ResetSidebarWidth();                 // expanding: drop the width that was pinned during collapse
    else AnimateSidebarCollapse();
});

private void AnimateSidebarCollapse()
{
    var splitView = FindSplitViewPane(_nav);
    if (splitView?.Pane is not FrameworkElement pane || splitView.CompactPaneLength <= 0) return;

    var startWidth = pane.ActualWidth > splitView.CompactPaneLength
        ? pane.ActualWidth
        : splitView.OpenPaneLength;

    pane.Width = startWidth;        // pin it; otherwise SplitView immediately arranges the pane at its compact width

    var animation = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true }; // mandatory for layout properties
    animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = startWidth });
    animation.KeyFrames.Add(new SplineDoubleKeyFrame
    {
        KeyTime = TimeSpan.FromMilliseconds(SidebarCloseMs),
        KeySpline = new KeySpline { ControlPoint1 = SplineStart, ControlPoint2 = SplineEnd },
        Value = splitView.CompactPaneLength,
    });
    Storyboard.SetTarget(animation, pane);
    Storyboard.SetTargetProperty(animation, "Width");

    var sb = new Storyboard();
    sb.Children.Add(animation);
    sb.Completed += (_, _) => { /* wrap up: stop the storyboard, clear the marker */ };
    sb.Begin();
}
```

Three things you must remember:

1. `EnableDependentAnimation = true` is required, otherwise animations on layout properties are **silently ignored**.
2. When expanding you must call `pane.ClearValue(FrameworkElement.WidthProperty)`, otherwise the pane stays 48px wide forever.
3. The collapse duration must match the template's (the same time window), otherwise the two curves cut the picture apart. Measured:
   our width animation runs 118ms from start to finish, in sync with the template's 120ms, and the picture is continuous with no flicker.

---

## 8. Settings interaction model: draft + Apply / Cancel changes

### 8.1 Data model

```csharp
private readonly AppSettings _liveSettings;   // the one the runtime is using (engine and tray both read it)
private readonly AppSettings _settings;       // the draft the UI is editing
private AppSettings _applied;                 // snapshot of the last successful apply (used by "Cancel changes" to roll back)
private bool _dirty;
```

```csharp
// the two methods AppSettings must provide
internal AppSettings Clone()             // JSON round-trip deep copy; no need to copy field by field
{
    var json = JsonSerializer.Serialize(this);
    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
}

internal void CopyFrom(AppSettings other)   // overwrite field by field (draft → runtime, snapshot → draft)
{
    var copy = other.Clone();
    Theme = copy.Theme;
    WindowOpacity = copy.WindowOpacity;
    // …every field
}
```

### 8.2 Control callbacks do exactly two things

```csharp
// slider
slider.ValueChanged += (_, _) =>
{
    _settings.SampleStrength = slider.Value;                 // 1. write the draft
    label.Text = $"Strength: {slider.Value:0.#}";            // cheap feedback refreshed in place
    MarkDirty();                                             // 2. mark that there are unapplied changes
};
```

**Hard rule: never call `Save()` from a control callback.** Otherwise unapplied changes get written to disk; if a runtime component
watches the settings file with `FileSystemWatcher`, it loads those values immediately and the draft model stops working on the spot.

### 8.3 The floating card

```csharp
_applyCard = new Border
{
    HorizontalAlignment = HorizontalAlignment.Right,
    VerticalAlignment = VerticalAlignment.Bottom,
    Margin = new Thickness(0, 0, 24, 16),
    Padding = new Thickness(10, 8, 10, 8),
    CornerRadius = new CornerRadius(8),
    Background = IsDarkTheme()
        ? new SolidColorBrush(Color.FromArgb(0xE6, 0x2D, 0x2D, 0x2D))
        : new SolidColorBrush(Color.FromArgb(0xF0, 0xFF, 0xFF, 0xFF)),
    Visibility = Visibility.Collapsed,      // only appears when something changed
};

var cancel = new Button { Content = "Cancel changes", MinWidth = 96 };
var apply  = new Button { Content = "Apply", MinWidth = 96 };
if (Application.Current.Resources["AccentButtonStyle"] is Style accent) apply.Style = accent;  // wrapped in try/catch
cancel.Click += (_, _) => CancelPendingChanges();
apply.Click  += (_, _) => ApplyPendingChanges();
```

Key point: the card floats over the **bottom-right corner of the content row** (`Grid.SetRow(card, 1)`) and does not take a layout row; that way the sidebar still
spans the full window height, and no uncovered band is left at the bottom of the window either.

### 8.4 Apply and Cancel

```csharp
private void ApplyPendingChanges()
{
    _liveSettings.CopyFrom(_settings);      // 1. mirror onto the runtime instance
    ApplySideEffects();                     // 2. runtime side effects (e.g. push the new values to the engine/tray)
    ApplyAppearance(rebuildPage: true);     // 3. theme/material/title bar/sidebar + rebuild the current page
    _settings.Save();                       // 4. persist
    _applied = _settings.Clone();           // 5. refresh the snapshot
    HideApplyCard();
}

private void CancelPendingChanges()
{
    _settings.CopyFrom(_applied);           // roll the draft back
    HideApplyCard();
    RebuildCurrentPage();                   // rebuild the page so every control shows the rolled-back values
}
```

**"External changes" from the tray** (e.g. toggling a switch in the menu) take another path: change `_liveSettings` directly and `Save()`,
then re-sync the draft once — but only when the **draft is clean** (`!_dirty`); if the user has unapplied changes in hand, do not touch the draft.

---

## 9. Control conventions

### 9.1 Numeric row (one shape everywhere)

```csharp
private Grid BuildSliderWithTextBox(string label, double value, double sliderMin, double sliderMax,
                                    Action<double> apply, double step = 1.0, string format = "0.##",
                                    double? textMin = null, double? textMax = null)
{
    var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110, GridUnitType.Pixel) }); // label
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });    // slider
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70, GridUnitType.Pixel) });  // text box
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                          // ±

    var slider = new Slider
    {
        Minimum = sliderMin, Maximum = sliderMax, Value = Math.Clamp(value, sliderMin, sliderMax),
        SmallChange = step, LargeChange = step * 10, StepFrequency = step,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0),
    };
    var valueBox = new TextBox { Text = value.ToString(format) };

    var minusBtn = new Button { Content = new TextBlock { Text = "−", FontSize = 16,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        Width = 32, Height = 32, Padding = new Thickness(0) };
    var plusBtn  = new Button { Content = new TextBlock { Text = "+", FontSize = 16,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        Width = 32, Height = 32, Padding = new Thickness(0) };
    // slider.ValueChanged / valueBox.LostFocus / ± Click → apply(v) (MarkDirty is the caller's job)
    return grid;
}
```

| Convention | Value |
| --- | --- |
| Column widths | 110px label / star slider / 70px text box / auto buttons |
| Buttons | 32×32, `Padding = 0`, content is a centered `TextBlock` |
| Symbols | the minus sign is U+2212 `−` (the MINUS SIGN, not a hyphen), the plus sign is a plain `+`, font size 16 |
| Text box format | use the round-trippable `0.##`; do **not** use `P0`/`0%` (a user typing `80` gets parsed as 8000%) |
| Value range | the text box may exceed the slider range (`textMin/textMax`); the slider is only for fast dragging |

### 9.2 Other controls

| Scenario | Control | Convention |
| --- | --- | --- |
| Enums (theme, background effect) | horizontal `RadioButton` | descriptive text goes in parentheses, e.g. `Mica (云母)` |
| Boolean | `ToggleSwitch` | `OnContent/OffContent = "开"/"关"` ("on"/"off") |
| Standalone checkbox | `CheckBox` | |
| Section heading | `TextBlock` | `FontSize = 20`, `FontWeight = SemiBold` |
| De-emphasized note | `TextBlock` | `FontSize = 12`, `Opacity = 0.6` |
| Button group | `StackPanel` | `Orientation = Horizontal`, `Spacing = 8` |

### 9.3 A disabled mode must disable the whole row and rewrite the text

```csharp
void UpdateOpacityRow()
{
    var locked = _settings.BackgroundBlur != BlurMode.Default;   // opacity means nothing under Mica/Acrylic
    foreach (var child in opacityRow.Children)
        if (child is Control c) c.IsEnabled = !locked;

    opacityLabel.Text = locked
        ? "窗口不透明度: 100%（云母/亚克力模式固定）"   // "Window opacity: 100% (fixed in Mica/Acrylic mode)"
        : $"窗口不透明度: {_settings.WindowOpacity:P0}";  // "Window opacity: <value>"
}
```

> `IsEnabled` lives on `Control`, not on `UIElement` (`Grid`/`StackPanel` cannot be disabled directly) —
> so the child controls inside the row have to be disabled one by one.

---

## 10. Appearance system

| Mode | Implementation | Notes |
| --- | --- | --- |
| Theme | `root.RequestedTheme = Light / Dark / Default` | "follow system" reads `AppsUseLightTheme` from the registry |
| Default background | background image → GDI+ gaussian blur → `WriteableBitmap` → `ImageBrush` | the blur is computed off the UI thread before being handed to XAML |
| Mica | `MicaController` + `SystemBackdropConfiguration` | needs `using WinRT;`; use `Dispose()` (there is no `Close()`) |
| Acrylic | `DesktopAcrylicController` | same as above |
| Window opacity | `WS_EX_LAYERED` + `SetLayeredWindowAttributes` | a WinUI 3 window has no `Opacity` property |

### 10.1 Window opacity

```csharp
public static void ApplyOpacity(Window window, AppSettings settings)
{
    var hwnd = WindowNative.GetWindowHandle(window);
    var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

    // WS_EX_LAYERED is always kept set: MicaController needs it
    if ((exStyle & WS_EX_LAYERED) == 0)
    {
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    // default mode: the slider drives alpha; Mica/Acrylic: hard-coded 255 (the backdrop material owns the window surface)
    var alpha = settings.BackgroundBlur == BlurMode.Default
        ? (byte)Math.Clamp(settings.WindowOpacity * 255, 25, 255)
        : (byte)255;

    SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
}
```

### 10.2 Materials and the rules that couple with them

- Under a material mode, the title bar and the sidebar must stay **opaque**, otherwise you get the split look of "translucent title bar + opaque content".
- Under a material mode, the opacity slider must be **disabled and its text rewritten** (see 9.3).
- When switching modes, remember to dispose the previous controller (`MicaController.Dispose()`) so that backgrounds do not stack up.

### 10.3 APIs that measured dead / unreliable (do not use)

| API | Measured result |
| --- | --- |
| `Window.SystemBackdrop = new MicaBackdrop()` | the log says it was applied, but visually nothing changes at all |
| `DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ...)` | the window turns solid black `#000000` |
| `Visual.Shadow` | the property does not exist (CS1061); you need `SpriteVisual` + `CompositionDropShadow` |
| `RectangleGeometry.RadiusX/RadiusY` | the property does not exist (that is WPF); use the Composition `CreateRectangleClip` |
| `UIElement.IsEnabled` | does not exist (it is on `Control`) |

---

## 11. Persistence and external changes

```csharp
internal static string SettingsPath => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "UiTemplate", "settings.json");

internal static AppSettings Load()
{
    try
    {
        if (File.Exists(SettingsPath))
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
            if (settings != null)
            {
                // fallback: if the path is dead or the file was deleted, revert to the default background so the panel does not go blank
                if (string.IsNullOrEmpty(settings.BackgroundImagePath)
                    || !File.Exists(settings.BackgroundImagePath))
                    settings.BackgroundImagePath = AppSettings.DefaultBackgroundPath;
                return settings;
            }
        }
    }
    catch (Exception ex) { AppLog.Log($"Failed to load settings: {ex}"); }
    return new AppSettings();
}

internal void Save()
{
    try
    {
        var dir = Path.GetDirectoryName(SettingsPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this,
            new JsonSerializerOptions { WriteIndented = true }));
    }
    catch (Exception ex) { AppLog.Log($"Failed to save settings: {ex}"); }
}
```

The rules:

- **One path for the whole application** (historically there was a bug where the engine and the settings window each wrote their own file and overwrote each other).
- When you need to notice external changes, use `FileSystemWatcher` plus a 300ms debounce before reloading; the settings window itself must never write while editing.
- Default resources (the default background image and so on) are output next to the exe, so locate them with `AppContext.BaseDirectory`.

---

## 12. Logging and diagnostics

```csharp
internal static class AppLog
{
    internal static string LogPath => Path.Combine(Path.GetTempPath(), "UiTemplate.log");

    internal static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}"); }
        catch { }   // a logging failure must never affect the main flow
    }
}
```

### 12.1 Geometry problems: sample and measure

```csharp
var timer = DispatcherQueue.CreateTimer();
timer.Interval = TimeSpan.FromMilliseconds(16);
int tick = 0;
timer.Tick += (_, _) =>
{
    tick++;
    var y = pane.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
    AppLog.Log($"SAMPLE t{tick} open={_nav.IsPaneOpen} clipX={clipX:0.#} w={pane.ActualWidth:0.#} " +
               $"vis={pane.Visibility}/{paneRoot?.Visibility} y={y:0.#}");
    if (tick >= 150) timer.Stop();
};
timer.Start();
```

This sampling method has solved, in this project: the cause of the thin gaps above and below the sidebar, the truth behind the collapse
"having no animation", and the flicker caused by stacked animations.

### 12.2 Reproducing interactions: simulated clicks

```csharp
var peer = new ButtonAutomationPeer(toggleButton);
(peer as Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)?.Invoke();
```

### 12.3 Discipline

- Instrumentation must carry a marker you can `grep` for (such as `SAMPLE`, `TEMP`);
- delete it the moment the cause is found, and rebuild to confirm `grep` finds no leftovers;
- before delivery, run a clean build once more (`rm -rf bin obj`) to confirm 0 error.

---

## 13. Troubleshooting manual (symptom → cause → action)

| Symptom | Cause | Action |
| --- | --- | --- |
| exe exits instantly, exit code `0x8000801A` | the self-contained runtime DLLs are not next to it | launch from `bin\...\<RID>\`, do not copy the exe |
| build reports MSB3027 / MSB3021 | an old instance is holding the exe | run `taskkill /F /IM <App>.exe`, then rebuild |
| system buttons are invisible in light mode | the button colors follow the system theme | set `AppWindow.TitleBar.Button*Color` |
| the window cannot be made transparent as a whole | a WinUI 3 window has no `Opacity` | `WS_EX_LAYERED` + `SetLayeredWindowAttributes` |
| Mica does not take effect / the window is solid black | `MicaBackdrop` / the DWM attribute are unreliable | use `MicaController` |
| an animation on a layout property does nothing at all | `EnableDependentAnimation` is missing | set it to `true` |
| thin gaps above and below the sidebar | the template pane's 3px margin + a 1px host border | zero the pane Margin + flatten the ancestors (Section 7.2) |
| the sidebar collapse "has no animation" | the template's collapse lasts only 120ms and squeezes the pane in the same frame | animate the pane width only, with the template's duration and curve (Section 7.3) |
| the sidebar flickers for a few frames while collapsing | several animations fighting over one property / the rounding clip being set to a 0 size | keep only one animation; add a non-positive-size guard to `SyncClipBounds` |
| dragging a slider gets interrupted and stutters | live application + a full page rebuild | use the draft model, only Apply takes effect |
| the engine does nothing after settings are applied | only the draft was changed, never mirrored onto the runtime instance | call `_liveSettings.CopyFrom(_settings)` first when applying |
| settings are lost after a restart | nothing was persisted in the control callbacks / the paths are not unified | call `Save()` only on the apply path; one `SettingsPath` for the whole application |
| externally changed settings get overwritten | the watcher's load and the draft conflict | do not sync the draft while it is dirty; tray changes write straight to the runtime |
| some controls keep their old colors after a theme switch | brushes that already exist do not follow automatically | rebuild the current page |
| the sidebar rounding clips the content | the Composition clip applies to the whole pane | clip only the pane's background element, or give the content padding |
| typing `80` into the value box becomes 8000% | a `P0`/`0%` format was used → parsed back as 80 → 8000% | use `0.##` and handle the denominator when writing percentage values |
| the admin build cannot be started from an ordinary shell | the manifest demands elevation | start it from an elevated shell, or switch back to `asInvoker` |
| the output is huge | the self-contained runtime | acceptable (149MB); for a small footprint switch to non-self-contained plus a bootstrapper install |
| `XamlCompiler warning WMC1509` | the WinUI package version in the NuGet cache disagrees with the one WASDK declares | harmless, ignore it |

---

## 14. The ten-step recipe from scratch (each step with an acceptance criterion)

| Step | What to do | Acceptance criterion |
| --- | --- | --- |
| 1 | Create the project as in Section 3 (csproj + manifest) | `dotnet build` passes; launching from the output directory shows an empty window |
| 2 | Write the App shell: tray → window → activate, with try/catch and logging all the way through | three startup records appear in the log |
| 3 | Build the two-row root layout + the custom-drawn title bar + system button colors | switching the light/dark theme changes both the title bar and the button colors |
| 4 | Drop in a `NavigationView` (48/200, `Tag` routing) + three empty pages | collapse/expand works; switching pages throws no errors; scroll positions are remembered |
| 5 | Sidebar visuals and geometry: background, 12px rounding, zero-size guard, flattened insets | the sidebar touches the title bar at the top and the window bottom at the bottom; rounding only on the content side |
| 6 | Collapse animation: animate only the pane width (120ms, same curve) | the collapse has a visible animation and no flicker; after expanding, the pane width is restored |
| 7 | Settings model: `AppSettings` + draft + the floating Apply/Cancel card | changing any setting does not take effect immediately; Apply takes effect and persists; Cancel rolls back |
| 8 | Controls: the numeric trio, enums, booleans, disabled modes with rewritten text | the trio can be dragged, typed into and nudged with ±; disabled rows cannot be interacted with |
| 9 | Appearance: theme, background image + blur, Mica/Acrylic, window opacity | all four combinations switch correctly; under a material mode opacity is locked at 100% |
| 10 | Self-test and wrap-up | delete the instrumentation; clean build with 0 warning/0 error; settings survive closing and reopening the window |

---

## 15. Checklist for adapting this into your own tool

1. Change `AssemblyName` / `RootNamespace` / the window title / the tray tooltip text.
2. Replace the three sample pages (`General` / `Appearance` / `Advanced`) with your feature pages;
   use a `DisposablePage` for each page and `RegisterUnsubscribe` every event subscription inside it.
3. Add or remove fields in `AppSettings`, and update the field list in `CopyFrom` to match (very easy to miss).
4. Decide whether you need admin rights: only change `app.manifest` to `requireAdministrator` if you genuinely do.
5. Concentrate the runtime side effects (pushing values to the engine/service/device) in `ApplySideEffects()`,
   and keep up the discipline that "control callbacks never touch the runtime".
6. When you need a new appearance mode, add a branch in `AppearanceManager` and unlock/lock the relevant sliders in the UI at the same time.
7. Update `README.md`: the build and run rules (especially launching from bin and killing the old instance first) must be spelled out clearly.

---

## 16. Acceptance checklist for AI agents

Run these in order; every item must be backed by real output, and "it looks right" is not allowed:

```bash
# 1) A clean build must be 0 error (0 warning is better)
rm -rf bin obj && dotnet build -c Release 2>&1 | tail -6

# 2) The output exists and can be launched (from the output directory, do not copy the exe)
ls bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/<App>.exe
bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/<App>.exe
# Acceptance: %TEMP%\<App>.log shows tray registered → window created → window activated

# 3) Red-line check: no Save() may appear in control callbacks
grep -rn "Save()" --include=*.cs .        # should only hit: the apply button, tray external changes, the exit path

# 4) Hygiene check: build output must not enter the repository
git status --short                        # bin/ obj/ must not appear

# 5) Instrumentation cleanup check (replace the markers with whatever strings you used)
grep -rn "TEMP\|SAMPLE" --include=*.cs .  # should be empty

# 6) Close the test instance before delivery
taskkill /F /IM <App>.exe
```

Behavior-level acceptance (this needs a human to look at it, or sampling per Section 12.1):

- [ ] Collapsing the sidebar produces a continuous animation that neither flickers nor disappears as a whole mid-way.
- [ ] After expanding, the sidebar width returns to normal (it does not stay at 48px).
- [ ] Dragging any slider is not interrupted and shows no perceptible stutter, and the setting does **not** take effect immediately.
- [ ] After pressing "Apply" the settings take effect immediately and survive a restart; after pressing "Cancel changes" the UI returns to the last applied state.
- [ ] In light mode the three system buttons on the title bar are black and recognizable.
- [ ] In Mica/Acrylic mode the window opacity slider is disabled and its text states 100%.
- [ ] After closing the window the process still sits in the tray, and the tray's "Show window" brings it back with unapplied drafts intact.

---

## 17. Appendix: measured data and known limitations

### 17.1 Measured data

| Item | Measured value |
| --- | --- |
| Expand animation (template) | 350ms, KeySpline `0.1,0.9 0.2,1.0`, clip TranslateX −152 → 0 |
| Collapse animation (template) | 120ms, same curve, and the pane width is squeezed to 48px in the same frame |
| Collapse animation (this design) | start 05:59:31.921 → end 05:59:32.040 = **118ms** |
| Navigation area geometry | `y=32 h=639` (bottom edge 671) |
| Sidebar geometry (after flattening) | pane `y=32 h=639` (bottom edge 671), 1px inset retained |
| Sidebar inset (before flattening) | 4px above and below (3px template margin + 1px host border) |
| Self-contained output | 149MB / 88 DLLs |
| Clean Release build | about 8.4s |
| Template source size | about 2467 lines (11 files, including the README and project files) |
| Measured log paths | `%TEMP%\UiTemplate.log`; settings `%LOCALAPPDATA%\UiTemplate\settings.json` |

### 17.2 Known limitations

- The data is based on Windows 11 + .NET 8 + WASDK 2.4 (WinUI package resolves to 2.3.6); older WASDK releases (e.g. 1.5) lack
  APIs such as `MicaController.SystemBackdropConfiguration` and `GaussianBlurEffect`, so the code has to be replaced with downgraded equivalents.
- The sidebar insets and the animation durations depend on the current NavigationView template; if Microsoft changes the template, re-measure per Section 12.1 and adjust the constants.
- Under high DPI / multi-monitor scaling, verify pixels with the same measured approach; this document contains no scaling conversion logic.
- Background blur uses downsampling (long edge capped at 512, radius capped at 24) to keep the cost down; if you need higher precision, raise the caps yourself and evaluate the cost.

---

## 18. References

- Template and documentation repository: <https://github.com/Wei-Canxie/winui3-tool-ui-template>
- Reference implementation (MIT): OsuCursorWin — every code recipe and measured number in this document comes from its WinUI 3 version
- WinUI 3 template source (the ultimate authority when debugging invisible animations/insets):
  `~/.nuget/packages/microsoft.windowsappsdk.winui/<ver>/lib/net6.0-windows10.*/Microsoft.WinUI/Themes/generic.xaml`
