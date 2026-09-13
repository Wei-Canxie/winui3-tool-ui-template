# WinUI 3 桌面工具 UI 设计规范

> 这套规范来自一个已上线的 WinUI 3 桌面工具（osu! 光标替换器）的实际实现，目标是把
> "一个常驻托盘的 Windows 桌面工具，需要多页设置面板 + 定制外观" 这类 UI 从零搭起来。
> 阅读者可以是另一个 AI Agent：按第 10 节的配方逐步照做，就能得到外观与交互一致的 UI。
>
> 文中给出的代码都是可直接套用的最小片段，命名与结构沿用原实现。

---

## 1. 总体目标与设计取向

| 取向 | 具体表现 |
| --- | --- |
| 自绘标题栏 | 窗口用 `ExtendsContentIntoTitleBar`，标题栏由应用自己画（32px 一行），配色跟随**应用内主题**而不是系统主题 |
| 侧边导航外壳 | `NavigationView` + `LeftCompact`：收起是 48px 图标条，展开 200px，带圆角与展开/收起动画 |
| 页面即代码 | 设置页不用 XAML 文件，而是在 C# 里构建 `StackPanel` + `ScrollViewer`，便于按条件动态生成控件 |
| 变更可控 | 所有设置改动先进"草稿"，右下角浮出**应用 / 取消更改**，点应用才落盘并作用到运行时 |
| 外观可调 | 主题（跟随系统/亮/暗）、背景图 + 高斯模糊、云母（Mica）、亚克力（Acrylic）、窗口不透明度 |
| 沉默运行 | 关窗只是隐藏，托盘图标常驻；设置改动不打断正在进行的拖拽 |
| 可诊断 | 全程写日志到 `%TEMP%\<App>.log`，几何/动画问题靠日志里的实测数据定位 |

三个反复被验证的原则：

1. **不要在"实时预览"上做重活。** 早期版本每次滑条变化都重建整页，结果拖拽被中断、卡顿。
   现在滑块只改内存值 + 标记脏，真正的应用发生在用户点"应用"时。
2. **不要和系统模板的动画抢同一条属性。** 详见第 4 节：模板的收起动画是 120ms 且会同时压缩
   pane，硬去按住它的裁剪属性会让整个侧边栏闪烁。正确做法是只动画一个我们自己控制的属性。
3. **一切几何问题都要量。** WinUI 的模板内缩（例如 NavigationView 的 pane 有 3px 外边距 +
   1px 宿主边框）在源码里看不到，只有把 `TransformToVisual` 的实测坐标打进日志才能定位。

---

## 2. 工程配置（非打包 WinUI 3）

`.csproj` 的关键属性：

```xml
<TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
<TargetPlatformMinVersion>10.0.17763.0</TargetPlatformMinVersion>
<UseWinUI>true</UseWinUI>
<UseWPF>false</UseWPF>
<WindowsPackageType>None</WindowsPackageType>          <!-- 非打包（unpackaged） -->
<EnableMsixTooling>false</EnableMsixTooling>
<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>  <!-- 免安装运行时 -->
<Nullable>enable</Nullable>
<ApplicationManifest>app.manifest</ApplicationManifest>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<!-- dotnet CLI 不自带 PRI/Appx 任务，指到已装的 VS2022，否则 PriGen 会失败 -->
<AppxMSBuildToolsPath>C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\</AppxMSBuildToolsPath>
```

依赖：`Microsoft.WindowsAppSDK`（本实现用 2.4.0）+ `Microsoft.Windows.SDK.BuildTools`。
需要 WinForms/GDI 能力时再加 `<FrameworkReference Include="Microsoft.WindowsDesktop.App.WindowsForms" />`。

`app.manifest`：PerMonitorV2 DPI，需要管理员权限时加 `requireAdministrator`。

> **必须知道的启动坑**：自包含（SelfContained）构建下，窗口运行时 DLL 都在
> `bin\<Platform>\<Config>\<TFM>\<RID>\` 里，**项目根目录只有一个孤零零的 exe 是跑不起来的**
> （会以 `0x8000801A` 秒退）。构建完成后请从输出目录启动；测试新的构建前先用提权
> `taskkill /F /IM <App>.exe` 杀掉旧实例，否则 `dotnet build` 会报 MSB3027/MSB3021 文件被占用。

---

## 3. 窗口外壳：自绘标题栏 + NavigationView

### 3.1 根布局只有两行

```csharp
ExtendsContentIntoTitleBar = true;

var root = new Grid();
root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                    // 32px 标题栏
root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 内容

_titleBarRoot = new Border { Height = 32, Background = GetTitleBarBrush() };
_titleBarText = new TextBlock { Text = Title, VerticalAlignment = VerticalAlignment.Center,
                                Margin = new Thickness(12, 0, 0, 0), FontWeight = FontWeights.SemiBold };
_titleBarRoot.Child = _titleBarText;
Grid.SetRow(_titleBarRoot, 0);
```

- 标题栏用 `Border`（高度固定 32）承载应用名，左对齐、12px 左边距。
- **不要**给标题栏加额外的行或间距；侧边栏的贴合问题几乎都源自这里多出来的像素。

### 3.2 标题栏与系统按钮的配色必须手动跟随应用主题

系统只按**系统主题**给最小化/最大化/关闭按钮上色，应用内切到亮色时会变成白底白按钮，
所以要在应用外观时同步：

```csharp
var titleBar = AppWindow.TitleBar;
var isDark = IsDarkTheme();

titleBar.ButtonBackgroundColor = Colors.Transparent;              // 融入自绘标题栏
titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
titleBar.ButtonForegroundColor = isDark ? Colors.White : Colors.Black;
titleBar.ButtonHoverForegroundColor = isDark ? Colors.White : Colors.Black;
titleBar.ButtonPressedForegroundColor = isDark ? Colors.White : Colors.Black;
titleBar.ButtonHoverBackgroundColor = isDark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)
                                             : Color.FromArgb(0x18, 0x00, 0x00, 0x00);
titleBar.ButtonPressedBackgroundColor = isDark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)
                                               : Color.FromArgb(0x10, 0x00, 0x00, 0x00);
```

标题栏底色由应用给：暗色 `#2D2D2D`、亮色 `#F3F3F3`，并按窗口不透明度调整 alpha。

### 3.3 托盘工具：关窗即隐藏

```csharp
AppWindow.Closing += (_, e) => { e.Cancel = true; AppWindow.Hide(); };
```

### 3.4 导航外壳

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
nav.MenuItems.Add(new NavigationViewItem { Content = "外观", Icon = new SymbolIcon(Symbol.View), Tag = "appearance" });
// …其余页签同构，Tag 用于路由
```

页面切换时**重建**页面（而不是缓存控件实例），并缓存/恢复滚动位置：

```csharp
nav.SelectionChanged += (s, e) =>
{
    if (nav.SelectedItem is NavigationViewItem item && item.Tag is string tag)
    {
        CacheScrollPosition();                       // 记下旧页的 VerticalOffset
        if (_currentPage is IDisposable old) old.Dispose();   // 解除事件订阅，避免泄漏
        _currentTag = tag;
        _currentPage = BuildPage(tag);
        nav.Content = _currentPage;
        RestoreScrollPosition(tag, _currentPage);
    }
};
```

`DisposablePage` 是一个继承 `StackPanel` 的小容器，把"这个页面注册过的取消订阅动作"收集起来，
页面被丢弃时统一执行：

```csharp
internal sealed class DisposablePage : StackPanel, IDisposable
{
    private readonly List<Action> _unsubscribeActions = new();
    public void RegisterUnsubscribe(Action action) => _unsubscribeActions.Add(action);
    public void Dispose() { foreach (var a in _unsubscribeActions) { try { a(); } catch { } } _unsubscribeActions.Clear(); }
}
```

> 主题变化后要重建当前页，否则控件里已经生成的颜色刷子不会跟着换。

---

## 4. 侧边栏视觉与动画（含实测结论）

### 4.1 视觉：整列背景 + 右侧 12px 圆角

```csharp
var splitView = FindSplitViewPane(_nav);            // 视觉树里找 SplitView
if (splitView?.Pane is FrameworkElement pane)
{
    pane.Background = new SolidColorBrush(isDark ? Color.FromArgb(255, 0x2D, 0x2D, 0x2D) : Colors.White);

    // 圆角用 Composition 裁剪（XAML 的 RectangleGeometry 没有 RadiusX/RadiusY）
    var clip = compositor.CreateRectangleClip();
    clip.TopLeftRadius = clip.BottomLeftRadius = new Vector2(0, 0);      // 贴窗口左边，直角
    clip.TopRightRadius = clip.BottomRightRadius = new Vector2(12, 12);  // 靠内容一侧圆角
    SyncClipBounds(clip, pane);
    ElementCompositionPreview.GetElementVisual(pane).Clip = clip;
    pane.SizeChanged += (_, _) => SyncClipBounds(clip, pane);
}

static void SyncClipBounds(RectangleClip clip, FrameworkElement pane)
{
    // 关键：尺寸非正时不要更新，否则会裁成一个空矩形，整条侧边栏在若干帧内完全不可见
    if (!(pane.ActualWidth > 0) || !(pane.ActualHeight > 0)) return;
    clip.Left = 0f; clip.Top = 0f;
    clip.Right = (float)pane.ActualWidth; clip.Bottom = (float)pane.ActualHeight;
}
```

### 4.2 抹掉模板多出来的内缩，只保留 1px

NavigationView 的 `PaneContentGrid`（即 `SplitView.Pane`）自带上边距，外层还有一个偏了 1px
的宿主 `Border`，实测表现为"侧边栏上下各空 4px"。做法是归零 pane 自身边距并抹平它到
SplitView 之间的祖先：

```csharp
pane.Margin = new Thickness(0);                       // 模板那 3px 边距
FlattenPaneAncestors(pane, splitView);                // 把祖先的 Margin/Padding/Border/背景清掉
// 结果：仅保留模板宿主 Border 的 1px 内缩（刻意保留，视觉上收边更干净）
```

### 4.3 展开/收起动画：只动画一个属性

先给结论（都来自实测日志，不是推测）：

- 展开：模板把 `PaneClipRectangleTransform.TranslateX` 从 `-(OpenPaneLength-CompactPaneLength)`
  滑到 0，350ms，KeySpline `0.1,0.9 0.2,1.0`（先快后慢）。
- 收起：同一条属性反向，但只有 **120ms**；更关键的是关闭态会把 pane 宽度瞬间压到
  `CompactPaneLength`，于是"宽度只有 48px 的 pane 再被裁剪"看不出任何滑动 —— 用户看到的就是
  瞬间跳变。`PaneClosing` 事件的 `Cancel` **拦不住**这次关闭；在属性变更回调里把
  `IsPaneOpen` 改回 true 会让 NavigationView 停在半关闭的怪状态。
- 硬按住模板的裁剪窗口（每帧重建一次动画）会和模板自己的动画互相覆盖，导致侧边栏闪烁。

**采用的做法**：完全不碰模板的裁剪动画，只动画 pane 自己的宽度，并且让时长与曲线和模板一致，
两者同步收缩，谁也压不住谁：

```csharp
private const int SidebarCloseMs = 120;                 // 与模板收起一致
private static readonly Point Spline1 = new(0.1, 0.9);  // 模板同款
private static readonly Point Spline2 = new(0.2, 1.0);

nav.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) =>
{
    if (nav.IsPaneOpen) ResetSidebarWidth();            // 展开：撤掉收起时钉的宽度
    else AnimateSidebarCollapse();
});

void AnimateSidebarCollapse()
{
    var splitView = FindSplitViewPane(_nav);
    var pane = splitView.Pane as FrameworkElement;
    var startWidth = pane.ActualWidth > splitView.CompactPaneLength ? pane.ActualWidth : splitView.OpenPaneLength;

    pane.Width = startWidth;                            // 钉住：否则 SplitView 直接 arrange 成紧凑宽度
    var sb = new Storyboard();
    var anim = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };  // 布局属性必须开
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

要点：

- `EnableDependentAnimation = true` 是**必需**的，否则 `Width` 这类布局属性上的动画会被静默忽略。
- 展开时必须 `pane.ClearValue(FrameworkElement.WidthProperty)`，否则 pane 会永远留在 48px 宽。
- 收起的时长要和模板一致（同一时间窗），否则两条曲线会互相切断画面。

---

## 5. 设置交互模型：草稿 + 应用 / 取消更改

### 5.1 为什么不实时应用

每个控件改动都立刻应用外观，会连带重建整页（为了同步控件配色），而重建会销毁正在被拖拽的
滑条 —— 拖动被中断，并且每次 tick 都有一次整页构建，肉眼可见卡顿。所以：

- 控件回调**只**做两件事：把值写进草稿对象、`MarkDirty()`。
- 真正应用发生在用户点"应用"时（或窗口初始化时）。

### 5.2 草稿对象

设置窗口持有两个 `AppSettings` 实例：`_liveSettings`（运行时/引擎正在用的）与 `_settings`
（UI 编辑的草稿，构造函数里 `Clone()` 而来），另有一个 `_applied` 快照供"取消更改"回滚。

```csharp
internal AppSettings Clone()            // JSON 往返深拷贝，避免逐个字段抄
internal void CopyFrom(AppSettings o)   // 逐字段覆盖（用于把草稿镜像回运行时实例）
```

### 5.3 浮动卡片

"应用 / 取消更改"不做成占据布局的一行（那样窗口底部会多出一条既不被侧边栏也不被遮罩覆盖的
带状区域），而是浮在内容区右下角：

```csharp
_applyBar = new Border
{
    HorizontalAlignment = HorizontalAlignment.Right,
    VerticalAlignment = VerticalAlignment.Bottom,
    Margin = new Thickness(0, 0, 24, 16),
    Padding = new Thickness(10, 8, 10, 8),
    CornerRadius = new CornerRadius(8),
    Background = GetFloatingBarBrush(),      // 暗色 #E6 2D2D2D / 亮色 #F0 FFFFFF
    Child = buttons,                         // [取消更改] [应用(AccentButtonStyle)]
    Visibility = Visibility.Collapsed,       // 有改动才出现
};
Grid.SetRow(_applyBar, 1);                   // 与内容同一行 → 侧边栏仍占满整窗高度
```

按钮规范：`MinWidth = 96`，"应用"用系统 `AccentButtonStyle`（`Application.Current.Resources`），
取消在前、应用在后。

### 5.4 应用与取消的完整流程

```csharp
private void ApplyPendingChanges()
{
    _liveSettings?.CopyFrom(_settings);                    // 1. 镜像到运行时实例
    _engine?.ApplyCursorWidth(_settings.CursorWidth);      // 2. 引擎侧副作用
    ApplyAppearanceCore();                                 // 3. 主题/背景/标题栏/侧边栏 + 重建当前页
    _settings.Save();                                      // 4. 落盘（JSON）
    _applied = _settings.Clone();                          // 5. 更新快照
    HideApplyBar();
}

private void CancelPendingChanges()
{
    _settings.CopyFrom(_applied);   // 回滚草稿
    HideApplyBar();
    RebuildCurrentPage();           // 重建页面，让所有控件显示回滚后的值
}
```

**重要约束**：控件回调里**不要**调用 `Save()`。否则草稿中未应用的改动会写进磁盘，而运行时
（如果有 FileSystemWatcher 监听设置文件）会立刻加载它们，草稿模型就失效了。

---

## 6. 控件规范

### 6.1 数值行三件套：标签 | 滑条 | 数字框 | − / +

```csharp
private Grid BuildSliderWithTextBox(string label, double value, double sliderMin, double sliderMax,
                                    Action<double> apply, double step = 1.0, string format = "0.##",
                                    double? textMin = null, double? textMax = null)
{
    var grid = new Grid();                        // 4 列：110px 标签 | * 滑条 | 70px 数字框 | auto 按钮
    var slider = new Slider { Minimum = sliderMin, Maximum = sliderMax, Value = value,
                              SmallChange = step, LargeChange = step * 10, StepFrequency = step };
    var valueBox = new TextBox { Text = value.ToString(format) };
    var minus = new Button { Content = new TextBlock { Text = "−", FontSize = 16,
                              HorizontalAlignment = HorizontalAlignment.Center },
                             Width = 32, Height = 32, Padding = new Thickness(0) };
    var plus  = new Button { Content = new TextBlock { Text = "+", FontSize = 16, … }, Width = 32, Height = 32 };
    // slider.ValueChanged / 数字框失焦 / ± 点击 → apply(v) → 由调用方 MarkDirty()
}
```

约定：

- 减号用 U+2212 `−`（en dash），不是连字符，视觉上更居中；`+` 用普通加号。
- 按钮固定 32×32、字号 16、`Padding = 0`，内容用 `TextBlock` 居中。
- 数字框允许超出滑条范围（`textMin/textMax`），滑条只负责快速拖动。
- 标签文字用"值 + 单位"形式实时刷新（例如 `背景图片不透明度: 80%`）。

### 6.2 其余控件

| 场景 | 控件 | 说明 |
| --- | --- | --- |
| 枚举（主题、背景效果） | `RadioButton` 横向排列 | 描述性文案放在括号里，如 `云母 (Mica)` |
| 布尔开关 | `ToggleSwitch` | `OnContent/OffContent = "开"/"关"` |
| 互斥开关（开机自启） | `CheckBox` | |
| 分组标题 | `TextBlock { FontSize = 20, FontWeight = SemiBold }` | |
| 需要实时生效的滑条 | 只 `MarkDirty()`，等"应用" | 见第 5 节 |

当某个设置项在特定模式下无意义（例如云母/亚克力模式下窗口不透明度固定 100%），
要**禁用整行并改写标签文案**，而不是留着可拖但无效果：

```csharp
foreach (var child in row.Children) if (child is Control c) c.IsEnabled = !locked;
label.Text = locked ? "窗口不透明度: 100%（云母/亚克力模式固定）" : $"窗口不透明度: {v:P0}";
```

> 注意 `IsEnabled` 在 `Control` 上，`UIElement` 没有这个属性（`Grid`/`StackPanel` 用不了），
> 需要逐个禁用其子控件。

---

## 7. 外观系统

| 模式 | 实现 | 要点 |
| --- | --- | --- |
| 主题 | `root.RequestedTheme = Light/Dark/Default` | 跟随系统时读注册表 `AppsUseLightTheme` |
| 默认背景 | 背景图 + GDI+ 高斯模糊（半径 0–255）→ `WriteableBitmap` → `ImageBrush` | 模糊在 UI 线程外算好再交给 XAML，避免卡顿 |
| 云母 Mica | `MicaController` + `SystemBackdropConfiguration`（`ICompositionSupportsSystemBackdrop`） | 需要 `using WinRT;`；`Dispose()` 释放，没有 `Close()` |
| 亚克力 Acrylic | `DesktopAcrylicController` | 同上 |
| 窗口不透明度 | `WS_EX_LAYERED` + `SetLayeredWindowAttributes` | WinUI 3 窗口没有 `Opacity` 属性，只能用分层窗口 |

窗口不透明度的关键细节：

```csharp
// WS_EX_LAYERED 始终保留（MicaController 需要它）
if ((exStyle & WS_EX_LAYERED) == 0) { SetWindowLong(...WS_EX_LAYERED...); SetWindowPos(...SWP_FRAMECHANGED...); }

// 默认模式：滑条控制 alpha；云母/亚克力：写死 255（背景材质自己拥有窗口表面）
byte alpha = settings.BackgroundBlur == BlurMode.Default
    ? (byte)Math.Clamp(settings.WindowOpacity * 255, 25, 255)
    : (byte)255;
SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
```

其他已验证的结论：

- `Window.SystemBackdrop = new MicaBackdrop()` 与 `DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE)`
  在本机 Win11 上**无效**（后者还会让窗口变成纯黑），因此统一走 `MicaController`。
- 背景材质生效时标题栏与侧边栏也要同步为不透明，否则会出现"半透明标题栏 + 不透明内容"的割裂。

---

## 8. 设置持久化与外部变更

```csharp
// %LOCALAPPDATA%\<App>\settings.json
private static string SettingsPath => Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "<App>", "settings.json");

internal static AppSettings Load() { /* 反序列化 + 兜底（文件缺失/路径失效时回退默认值）*/ }
internal void Save() { /* 目录不存在则创建，JSON 缩进写盘 */ }
```

- 路径要**全应用统一**（历史上有过引擎与设置窗口各写一份文件的 bug）。
- 运行时若需要感知外部改动，用 `FileSystemWatcher` + 300ms 防抖后重新加载；
  但设置窗口自己不要边改边写，见第 5.4 节。

---

## 9. 日志与诊断

```csharp
internal static class AppLog
{
    internal static string LogPath => Path.Combine(Path.GetTempPath(), "<App>.log");
    internal static void Log(string message) { /* 追加一行，时间戳用 O 格式，失败静默 */ }
}
```

几何/动画问题不要猜，直接量：

```csharp
// 每 16ms 采样一次，把绝对坐标写进日志
var timer = DispatcherQueue.CreateTimer();
timer.Interval = TimeSpan.FromMilliseconds(16);
timer.Tick += (_, _) =>
{
    var y = pane.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
    AppLog.Log($"t{tick} paneY={y:0.#} w={pane.ActualWidth:0.#} vis={pane.Visibility}");
    // 需要复现交互时，可以用 ButtonAutomationPeer + IInvokeProvider 模拟点击按钮
};
timer.Start();
```

排查完记得把临时代码删干净（`grep` 标记字符串确认）。

---

## 10. 把本套设计套用到新工具的分步配方

1. **建工程**：按第 2 节的 `.csproj` + `app.manifest`（先不加 `requireAdministrator`，
   只有真的需要时才加，否则每次启动都要 UAC）。
2. **写 App 外壳**：`App.xaml` 只放 `XamlControlsResources`；`App.xaml.cs` 的 `OnLaunched`
   里按顺序创建：后台服务/引擎 → 托盘图标 → 设置窗口 → `Activate()`，全程 try/catch 记日志。
3. **搭窗口**：根 `Grid` 两行（32px 标题栏 / 内容），`ExtendsContentIntoTitleBar = true`，
   写好 `GetTitleBarBrush()` / `ApplyCaptionButtonColors()`，主题切换时一起刷新。
4. **放导航**：`NavigationView(LeftCompact, 48/200)`，按功能分页，`Tag` 路由，
   页面用 `BuildPage(tag)` 现场构建（`DisposablePage` + `ScrollViewer` + 滚动位置缓存）。
5. **调侧边栏**：设置 pane 背景与右侧 12px 圆角裁剪（记住 0 尺寸保护），
   抹平模板内缩，接上收起动画（只动画 pane 宽度）。
6. **建设置模型**：`AppSettings`（属性 + `Load`/`Save`/`Clone`/`CopyFrom`），
   设置窗口用草稿 + `MarkDirty()`，右下角浮动卡片负责应用/取消。
7. **摆控件**：所有数值项用第 6.1 节的三件套，枚举用 `RadioButton`，布尔用 `ToggleSwitch`；
   在无意义的模式下禁用整行并改写文案。
8. **加外观**：主题 + 默认背景（图/模糊/不透明度）+ 云母/亚克力 + 窗口不透明度（第 7 节），
   注意材质模式下把不透明度写死并锁定滑条。
9. **自测**：跑起来后逐项改设置 → 点应用 → 关窗重开确认落盘；几何异常就按第 9 节打点实测。
10. **收尾**：删掉临时诊断代码，确认构建 0 error，再提交。

---

## 11. WinUI 3 踩坑清单

| 现象 | 原因 / 对策 |
| --- | --- |
| 根目录的 exe 秒退（`0x8000801A`） | 自包含运行时 DLL 在 bin 输出目录，必须从输出目录启动 |
| `dotnet build` 报 MSB3027/MSB3021 | 旧实例占用 exe，先提权 `taskkill` |
| 亮色模式下最小化/关闭按钮看不见 | 系统按钮颜色跟随系统主题，需手动设置 `AppWindow.TitleBar.Button*Color` |
| 窗口没有 `Opacity` 属性 | 用 `WS_EX_LAYERED` + `SetLayeredWindowAttributes` |
| Mica 不生效 / 纯黑 | 用 `MicaController`；`MicaBackdrop` 与 DWM 属性都不可靠 |
| 布局属性上的动画没反应 | `EnableDependentAnimation = true` |
| `UIElement` 没有 `IsEnabled` | 逐个禁用 `Control` 子控件 |
| `RectangleGeometry` 没有 `RadiusX/RadiusY` | 用 Composition `CreateRectangleClip` |
| `Visual` 没有 `Shadow` 属性 | 需要 `SpriteVisual` + `CompositionDropShadow` |
| `Storyboard.SetTargetProperty(anim, "宽度")` 写错路径 | 直接写 DP 名，例如 `"Width"`、`"TranslateX"` |
| 侧边栏上下有细缝 | 模板 pane 有 3px 边距 + 1px 宿主边框，见第 4.2 节 |
| 收起动画"不存在" | 模板收起只有 120ms 且同时压缩 pane，见第 4.3 节 |
| 拖滑条被打断 | 不要实时应用/重建整页，用草稿模型 |
| 圆角裁剪后整块消失 | Composition 裁剪不要设成 0 尺寸 |

---

## 12. 参考

- 本规范的实现样本：**OsuCursorWin**（osu! 光标替换工具，WinUI 3 版）
- WinUI 3 模板源码可在 NuGet 包里直接读：
  `~/.nuget/packages/microsoft.windowsappsdk.winui/<ver>/lib/net6.0-windows10.*/Microsoft.WinUI/Themes/generic.xaml`
  （排查看不到的动画/内缩时非常有用）

许可证：MIT。文中代码片段来自 MIT 许可的 OsuCursorWin 项目（Copyright (c) 2022 solstice23）。

---

## 13. 参考实现

`template/` 目录是本规范的可编译骨架（命名空间与程序集名均为 `UiTemplate`），
在目录里执行 `dotnet build -c Release` 可以 0 error 通过。它是第 10 节十步配方的
落地版本，每个文件对应其中的一步。

| 文件 | 作用 |
| --- | --- |
| `template/UiTemplate.csproj` | 非打包 + 自包含的 WinUI 3 工程（net8.0-windows10.0.19041.0）；自包含意味着必须从 `bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\` 启动 |
| `template/app.manifest` | PerMonitorV2 DPI 感知；`asInvoker`，不请求管理员 |
| `template/App.xaml` / `App.xaml.cs` | `XamlControlsResources`；`OnLaunched` 里依次创建设置 → 托盘 → 窗口，全程 try/catch |
| `template/AppLog.cs` | 追加时间戳到 `%TEMP%\UiTemplate.log`，失败不抛 |
| `template/AppSettings.cs` | 设置持久化（`Load`/`Save`/`Clone`/`CopyFrom`），落在 `%LOCALAPPDATA%\UiTemplate\settings.json` |
| `template/AppearanceManager.cs` | 主题、窗口不透明度（`WS_EX_LAYERED`）、云母/亚克力、背景图 + GDI+ 高斯模糊 |
| `template/TrayIcon.cs` | `Shell_NotifyIcon` 托盘图标：显示窗口 / 切换示例开关 / 退出 |
| `template/ShellWindow.cs` | 窗口本体：32px 标题栏、`NavigationView` 三页（General / Appearance / Advanced）、草稿 + 应用/取消模型、数值行助手 |
| `template/README.md` | 构建与运行路径规则、关窗即隐藏、以及改名改造步骤 |

把 `template/` 复制一份改名即可开新工具：改 `RootNamespace` / `AssemblyName`、
按需调整 `AppxMSBuildToolsPath`，然后替换 `ShellWindow.cs` 顶部的 `Strings` 文案块
与三个 `Build*Page()`。
