# WinUI 3 桌面工具 UI 设计规范与落地手册

[English](README_leader.en.md) | 中文

> 版本：2026-09-13 ｜ 适用：Windows 11 + .NET 8 + Windows App SDK 2.4（WinUI 包解析为 2.3.6）
> 来源：一个已实际运行的托盘常驻工具（osu! 光标替换器，MIT）的 WinUI 3 实现，
> 所有结论都来自实测日志，不是"看起来应该这样"。
> 配套仓库：<https://github.com/Wei-Canxie/winui3-tool-ui-template>（文档 + 可编译模板工程）

---

## 0. 怎么读这份文档

**人类开发者**：先读第 1、2 节建立判断标准，再跳到第 14 节按十步配方做；遇到怪现象查第 13 节。

**AI Agent**：把第 2 节（铁律）、第 3 节（工程配置）、第 14 节（配方）、第 16 节（验收清单）
当作硬约束执行；第 13 节是"现象 → 原因 → 动作"的检索表。所有常数、路径、时长都在文中给出确切值，
不要凭经验臆造。

**一句话概括这套 UI**：一个能缩成 48px 图标条的侧边导航外壳，配自绘标题栏和一套"改动先进草稿、
点应用才生效"的设置面板，外观支持主题 / 背景图 / 云母 / 亚克力 / 窗口不透明度。

### 0.1 一页速览

| 维度 | 取值 |
| --- | --- |
| 窗口 | 非打包（unpackaged）+ 自包含（self-contained），免装运行时 |
| 根布局 | 只有两行：32px 自绘标题栏 + 内容区 |
| 导航 | `NavigationView`，`LeftCompact`，收起 48px / 展开 200px |
| 侧边栏 | 整列主题底色，靠内容一侧 12px 圆角，收起 120ms 宽度动画 |
| 设置模型 | 草稿对象 + 右下角浮动"应用 / 取消更改"卡片 |
| 数值控件 | 标签 \| 滑条 \| 数字框 \| − / +，四列 110 / * / 70 / auto |
| 外观 | 主题（跟随系统/亮/暗）、背景图 + 高斯模糊、云母、亚克力、窗口不透明度 |
| 透明度实现 | `WS_EX_LAYERED` + `SetLayeredWindowAttributes`（WinUI 3 窗口没有 `Opacity`） |
| 设置存储 | `%LOCALAPPDATA%\<App>\settings.json` |
| 日志 | `%TEMP%\<App>.log`，纯追加文本 |
| 生命周期 | 关窗 = 隐藏；托盘图标常驻 |
| 自包含产物体积 | 149MB / 88 个 DLL（实测） |
| 干净构建耗时 | 约 8.4s（实测，release，已还原 NuGet 缓存） |

---

## 1. 设计目标

1. **像 Windows 原生**：自绘标题栏但保留系统按钮；侧边导航、圆角、半透明材质都跟系统一致。
2. **改动可控**：设置面板里所有改动先落草稿，用户点"应用"才真正生效并写盘；点"取消更改"一键回到上次应用的状态。
3. **编辑过程不卡**：拖动滑条时绝不做重活（不重建页面、不写盘、不重启引擎）。
4. **沉默运行**：托盘常驻，关窗只是隐藏；日志写文件，不弹窗。
5. **可诊断**：任何"看着不对"的问题都要能用日志里的实测数据定位，而不是靠猜。

---

## 2. 五条铁律（每条都有真实反例）

### 铁律 1：不要在"实时预览"上做重活

**反例**：早期版本每次滑条 tick 都调用一次"应用外观"，而应用外观会重建整个设置页（为了刷新控件配色）。
结果是：拖动中的滑条被销毁 → 拖拽中断；每次 tick 一次整页构建 → 肉眼可见卡顿。

**做法**：控件回调只做两件事 —— 写草稿、`MarkDirty()`。真正的应用发生在用户点"应用"时。
需要实时反馈的部分（例如标签上的数值文字）就地更新即可，那是廉价的。

### 铁律 2：不要和系统模板的动画抢同一条属性

**反例**：为了让侧边栏收起"有动画"，我们先是每 16ms 重建一个动画去按住模板的裁剪属性，
结果和模板自己的动画互相覆盖，侧边栏在收起开头有几帧**完全不可见**，然后才恢复。

**做法**：模板的收起只有 120ms 且同时把 pane 压到 48px（滑动因此不可见），但它快而稳。
正确策略是**只动画一个我们自己能完全掌控的属性**（pane 的宽度），并且让它的时长与曲线
和模板保持一致，两者同步收缩，谁也不压谁。详见第 7.3 节。

### 铁律 3：几何问题一律实测，不要推测

**反例**：侧边栏上下各有一条细缝，源码里完全看不出原因。把视觉树每一层的
`TransformToVisual` 绝对坐标打进日志后才定位到：模板给 pane 加了 3px 边距，外层还有一个偏了 1px 的宿主边框。

**做法**：临时插一段 `DispatcherQueue.CreateTimer()`（16ms）采样，把元素坐标/尺寸/可见性写日志，
定位后立刻删掉插桩（用标记字符串 `grep` 确认清干净）。

### 铁律 4：系统按钮配色必须手动跟随应用主题

**反例**：应用内切到亮色主题后，标题栏是浅色，但最小化/最大化/关闭按钮仍然是白色 —— 白底白按钮。

**做法**：每次应用外观时同步 `AppWindow.TitleBar.Button*Color`（见第 5.2 节），
包含悬停态、按下态、失焦态。

### 铁律 5：自包含构建只能从输出目录启动

**反例**：把 `bin\...\win-x64\<App>.exe` 单独复制到项目根目录再启动 —— 进程秒退，退出码 `0x8000801A`。
原因是没有同级目录里的 88 个运行时 DLL。

**做法**：永远从输出目录启动；换新构建前先 `taskkill /F /IM <App>.exe`，
否则 exe 被占用会让构建报 MSB3027/MSB3021。

---

## 3. 工程配置

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
    <WindowsPackageType>None</WindowsPackageType>       <!-- 非打包 -->
    <EnableMsixTooling>false</EnableMsixTooling>
    <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>  <!-- 免装运行时 -->
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <Platforms>x86;x64;ARM64</Platforms>
    <RuntimeIdentifiers>win-x86;win-x64;win-arm64</RuntimeIdentifiers>
    <Platform>x64</Platform>
    <PlatformTarget>x64</PlatformTarget>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <ApplicationManifest>app.manifest</ApplicationManifest>
    <!-- dotnet CLI 不自带 PRI/Appx 任务；指到已安装的 VS2022，否则 PriGen 失败 -->
    <AppxMSBuildToolsPath>C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\</AppxMSBuildToolsPath>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.WindowsAppSDK" Version="2.4.0" />
    <PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.26100.4654" />
  </ItemGroup>
</Project>
```

- 需要 GDI/WinForms 能力（例如托盘、GDI+ 模糊、Win32 文件对话框）时加框架引用：
  `<FrameworkReference Include="Microsoft.WindowsDesktop.App" />` 与 `...WindowsForms`（可选）。
- `AppxMSBuildToolsPath` 是**本机路径**，交给别人用时要改成他们自己的 VS 安装路径。
- 构建时可能出现 `XamlCompiler warning WMC1509: No LocalAssembly parameter given during MarkupCompilePass2`，
  这是无害的（说明 NuGet 缓存里的 WinUI 包版本与 WASDK 声明版本不完全一致，实测不影响运行）。

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
        <requestedExecutionLevel level="asInvoker" />   <!-- 需要管理员时才改 requireAdministrator -->
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

**只在该工具真的需要管理员权限时才用 `requireAdministrator`**：一旦启用，之后每次启动都会弹 UAC，
且非提权 shell 无法启动它（`start <exe>` 会静默失败、进程不驻留），调试链路会变长。

### 3.3 构建与运行规则

```bash
# 1) 换新构建前先杀掉旧实例（否则 exe 被占用 → MSB3027/MSB3021）
taskkill /F /IM UiTemplate.exe          # 提权场景需要 high 权限的 shell

# 2) 构建
dotnet build -c Release                 # 干净构建实测约 8.4s

# 3) 从输出目录启动（不要复制 exe 到别处）
bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/UiTemplate.exe
```

验证启动成功的最低标准：`%TEMP%\<App>.log` 里出现"托盘注册 → 窗口创建 → 窗口激活"三条记录。

---

## 4. 架构与文件职责

模板工程共 11 个文件（`.cs` / `.xaml` / `.csproj` / README 合计约 2467 行；下表把 `App.xaml` 与 `App.xaml.cs` 合并为一行）：

| 文件 | 职责 | 关键点 |
| --- | --- | --- |
| `UiTemplate.csproj` | 工程配置 | 非打包 + 自包含；`AppxMSBuildToolsPath` 需按本机修改 |
| `app.manifest` | DPI 与权限 | PerMonitorV2；默认 asInvoker |
| `App.xaml` / `App.xaml.cs` | 应用入口 | 只放 `XamlControlsResources`；启动顺序：托盘 → 窗口 → 激活，全程 try/catch 记日志 |
| `AppLog.cs` | 日志 | 追加时间戳到 `%TEMP%\<App>.log`，失败静默 |
| `AppSettings.cs` | 设置模型 | 属性 + `Load` / `Save` / `Clone` / `CopyFrom`，JSON 落在 `%LOCALAPPDATA%\<App>\settings.json` |
| `AppearanceManager.cs` | 外观 | 主题、窗口不透明度（`WS_EX_LAYERED`）、云母/亚克力、背景图 + 高斯模糊 |
| `ShellWindow.cs` | 窗口本体 | 外壳布局、自绘标题栏、导航、侧边栏、草稿/应用模型、控件工厂 |
| `TrayIcon.cs` | 托盘 | `Shell_NotifyIcon`；菜单：显示窗口 / 切换示例功能 / 退出 |
| `README.md` | 使用说明 | 构建运行规则与改造步骤 |
| `.gitignore` | 仓库卫生 | 忽略 `bin/`、`obj/`（自包含产物 149MB，绝不要提交） |

类的分层关系：

```
App (Application)
 ├─ TrayIcon            ← Win32 消息循环 + 菜单
 └─ ShellWindow (Window)                ← UI 全部逻辑
      ├─ AppSettings   (_liveSettings / _settings 草稿 / _applied 快照)
      └─ AppearanceManager (静态)        ← 主题/材质/不透明度
```

**核心概念**：`AppSettings` 在内存里同时存在三份 —— 运行时正在用的（`_liveSettings`）、
UI 正在编辑的草稿（`_settings`）、上次成功应用的快照（`_applied`）。
"应用"把草稿镜像到运行时并刷新快照；"取消更改"把快照拷回草稿。

---

## 5. 外壳：自绘标题栏

### 5.1 根布局严格两行

```csharp
ExtendsContentIntoTitleBar = true;

var root = new Grid();
root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 32px 标题栏
root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 内容

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

标题栏底色：暗色 `#2D2D2D`、亮色 `#F3F3F3`，alpha 跟随窗口不透明度
（`titleBarOpacity = opacity <= 0.9 ? opacity + 0.1 : opacity`，让标题栏略比内容清晰）。

**不要把浮动按钮、状态条等做成第三行**：侧边栏与遮罩都只覆盖"内容行"，
多出来的行会在窗口底部形成一条既没有侧边栏底色也没有材质覆盖的带状区域（踩过这个坑）。

### 5.2 系统按钮配色（必须跟随应用主题）

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

暗色与亮色的判定要统一走一个函数（跟随系统时读注册表）：

```csharp
using var key = Registry.CurrentUser.OpenSubKey(
    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
return key?.GetValue("AppsUseLightTheme") is int i && i == 0;
```

### 5.3 关窗即隐藏（托盘工具）

```csharp
AppWindow.Closing += (_, e) => { e.Cancel = true; AppWindow.Hide(); };
```

配套：托盘菜单"显示窗口"要 `AppWindow.Show(); Activate();`，重新打开时不要重建窗口（保留草稿状态）。

---

## 6. 导航与页面

### 6.1 导航控件参数

```csharp
var nav = new NavigationView
{
    IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
    IsSettingsVisible = false,
    PaneDisplayMode = NavigationViewPaneDisplayMode.LeftCompact,
    OpenPaneLength = 200,          // 展开宽度
    CompactPaneLength = 48,        // 收起宽度（图标条）
    IsPaneOpen = false,            // 默认收起
};
nav.MenuItems.Add(new NavigationViewItem { Content = "General",   Icon = new SymbolIcon(Symbol.View),  Tag = "general" });
nav.MenuItems.Add(new NavigationViewItem { Content = "Appearance", Icon = new SymbolIcon(Symbol.Target), Tag = "appearance" });
nav.MenuItems.Add(new NavigationViewItem { Content = "Advanced",  Icon = new SymbolIcon(Symbol.Setting), Tag = "advanced" });
```

### 6.2 页面在 C# 里构建 + 订阅管理

页面是"每次切换都重建"的（不缓存控件实例），因此必须解决事件订阅泄漏：
用一个收集取消订阅动作的容器。

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

切页与滚动位置缓存：

```csharp
nav.SelectionChanged += (s, e) =>
{
    if (nav.SelectedItem is NavigationViewItem item && item.Tag is string tag)
    {
        CacheScrollPosition();                               // 记录旧页 VerticalOffset 到字典
        if (_currentPage is IDisposable old) old.Dispose();
        _currentTag = tag;
        _currentPage = BuildPage(tag);                       // 返回 ScrollViewer
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

**主题变化或语言变化后必须重建当前页**，否则已经生成的画刷（标题、卡片底色）不会跟着换。

---

## 7. 侧边栏（最花功夫的部分）

### 7.1 视觉：整列底色 + 靠内容一侧 12px 圆角

```csharp
private void SyncSidebarBackground()
{
    var splitView = FindSplitViewPane(_nav);        // 视觉树里递归找 SplitView
    if (splitView?.Pane is not FrameworkElement pane) return;

    pane.Background = new SolidColorBrush(IsDarkTheme()
        ? Color.FromArgb(255, 0x2D, 0x2D, 0x2D)
        : Colors.White);

    // XAML 的 RectangleGeometry 没有 RadiusX/RadiusY，圆角只能用 Composition 裁剪
    var clip = ElementCompositionPreview.GetElementVisual(pane).Compositor.CreateRectangleClip();
    clip.TopLeftRadius = clip.BottomLeftRadius = new Vector2(0, 0);       // 贴窗口左侧：直角
    clip.TopRightRadius = clip.BottomRightRadius = new Vector2(12, 12);   // 靠内容侧：圆角
    SyncClipBounds(clip, pane);
    ElementCompositionPreview.GetElementVisual(pane).Clip = clip;
    pane.SizeChanged += (_, _) => SyncClipBounds(clip, pane);
}

private static void SyncClipBounds(RectangleClip clip, FrameworkElement pane)
{
    // 关键：尺寸非正时不要更新。裁成空矩形会让整条侧边栏在若干帧内完全不可见。
    if (!(pane.ActualWidth > 0) || !(pane.ActualHeight > 0)) return;
    clip.Left = 0f;  clip.Top = 0f;
    clip.Right = (float)pane.ActualWidth;
    clip.Bottom = (float)pane.ActualHeight;
}
```

### 7.2 几何：抹掉模板多出来的内缩

实测（日志里的绝对坐标）：`NavigationView` 的 `PaneContentGrid`（就是 `SplitView.Pane`）
带 3px 垂直边距，外层还有一个偏了 1px 的宿主 `Border`，表现为"侧边栏上下各空 4px"。

```csharp
pane.Margin = new Thickness(0);            // 去掉模板那 3px

// 把 pane 到 SplitView 之间的祖先的 Margin/Padding/BorderThickness/Background 全部抹平
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

结果：只保留模板宿主 `Border` 的 1px 内缩（刻意的，视觉上收边更干净）。
实测贴合数据：导航区 `y=32 h=639`（底边 671），pane 同样 `y=32 h=639`（底边 671），两者完全重合。

### 7.3 收起动画：只动画一个属性

**模板的实际行为（实测，不是文档描述）**：

- 展开：`PaneClipRectangleTransform.TranslateX` 从 `-(OpenPaneLength-CompactPaneLength)`
  （本配置 = −152）滑到 0，**350ms**，KeySpline `0.1,0.9 0.2,1.0`（先快后慢）。
- 收起：同一属性反向，只有 **120ms**，而且关闭态会同时把 pane 宽度压到 `CompactPaneLength`；
  一个 48px 宽的 pane 再被裁剪看不出任何滑动 —— 所以收起"看起来没有动画"。
- `PaneClosing` 事件的 `Cancel` **拦不住**这次关闭；在属性变更回调里把 `IsPaneOpen` 改回 `true`
  会让 NavigationView 停在半关闭的怪状态（pane 宽度卡在 130/104 之类）。
- 硬按住模板的裁剪属性（每帧重建动画）会与模板动画互相覆盖 → 侧边栏闪烁。

**采用的做法**：完全不碰模板的裁剪动画，只动画 pane 自身的宽度，时长与曲线和模板一致：

```csharp
private const int SidebarCloseMs = 120;                       // 与模板收起同时间窗
private static readonly Point SplineStart = new(0.1, 0.9);    // 模板同款曲线
private static readonly Point SplineEnd   = new(0.2, 1.0);

_nav.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) =>
{
    if (_nav.IsPaneOpen) ResetSidebarWidth();                 // 展开：撤掉收起时钉的宽度
    else AnimateSidebarCollapse();
});

private void AnimateSidebarCollapse()
{
    var splitView = FindSplitViewPane(_nav);
    if (splitView?.Pane is not FrameworkElement pane || splitView.CompactPaneLength <= 0) return;

    var startWidth = pane.ActualWidth > splitView.CompactPaneLength
        ? pane.ActualWidth
        : splitView.OpenPaneLength;

    pane.Width = startWidth;        // 钉住：否则 SplitView 会立刻把 pane arrange 成紧凑宽度

    var animation = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true }; // 布局属性必须开
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
    sb.Completed += (_, _) => { /* 收尾：停表、清标记 */ };
    sb.Begin();
}
```

必须记住的三点：

1. `EnableDependentAnimation = true` 必需，否则布局属性上的动画会被**静默忽略**。
2. 展开时必须 `pane.ClearValue(FrameworkElement.WidthProperty)`，否则 pane 永远停在 48px 宽。
3. 收起的时长要和模板一致（同一时间窗），否则两条曲线会互相切断画面。实测：
   我们的宽度动画从开始到结束 118ms，与模板的 120ms 同步，画面连贯无闪烁。

---

## 8. 设置交互模型：草稿 + 应用 / 取消更改

### 8.1 数据模型

```csharp
private readonly AppSettings _liveSettings;   // 运行时正在用的（引擎/托盘都会读）
private readonly AppSettings _settings;       // UI 正在编辑的草稿
private AppSettings _applied;                 // 上次成功应用的快照（供"取消更改"回滚）
private bool _dirty;
```

```csharp
// AppSettings 里必须提供的两个方法
internal AppSettings Clone()             // JSON 往返深拷贝，不必逐字段抄
{
    var json = JsonSerializer.Serialize(this);
    return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
}

internal void CopyFrom(AppSettings other)   // 逐字段覆盖（用于草稿 → 运行时、快照 → 草稿）
{
    var copy = other.Clone();
    Theme = copy.Theme;
    WindowOpacity = copy.WindowOpacity;
    // …全部字段
}
```

### 8.2 控件回调只做两件事

```csharp
// 滑条
slider.ValueChanged += (_, _) =>
{
    _settings.SampleStrength = slider.Value;                 // 1. 写草稿
    label.Text = $"Strength: {slider.Value:0.#}";            // 就地刷新的廉价反馈
    MarkDirty();                                             // 2. 标记有未应用的改动
};
```

**红线：控件回调里绝不调用 `Save()`**。否则未应用的改动会写进磁盘；如果有运行时组件用
`FileSystemWatcher` 监听设置文件，它会立刻加载这些值，草稿模型当场失效。

### 8.3 浮动卡片

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
    Visibility = Visibility.Collapsed,      // 有改动才出现
};

var cancel = new Button { Content = "Cancel changes", MinWidth = 96 };
var apply  = new Button { Content = "Apply", MinWidth = 96 };
if (Application.Current.Resources["AccentButtonStyle"] is Style accent) apply.Style = accent;  // try/catch 包住
cancel.Click += (_, _) => CancelPendingChanges();
apply.Click  += (_, _) => ApplyPendingChanges();
```

要点：卡片浮在**内容行的右下角**（`Grid.SetRow(card, 1)`），不占布局行；因此侧边栏仍然占满整窗高度，
也不会在窗口底部留下无覆盖的带状区域。

### 8.4 应用与取消

```csharp
private void ApplyPendingChanges()
{
    _liveSettings.CopyFrom(_settings);      // 1. 镜像到运行时实例
    ApplySideEffects();                     // 2. 运行时副作用（例如把新值推给引擎/托盘）
    ApplyAppearance(rebuildPage: true);     // 3. 主题/材质/标题栏/侧边栏 + 重建当前页
    _settings.Save();                       // 4. 落盘
    _applied = _settings.Clone();           // 5. 更新快照
    HideApplyCard();
}

private void CancelPendingChanges()
{
    _settings.CopyFrom(_applied);           // 回滚草稿
    HideApplyCard();
    RebuildCurrentPage();                   // 重建页面，让所有控件显示回滚后的值
}
```

**托盘的"外部改动"**（例如菜单里切换某个开关）走另一条路：直接改 `_liveSettings` 并 `Save()`，
然后在**草稿干净**（`!_dirty`）时才把草稿重新同步一次；如果用户手上有未应用的改动，就不要动草稿。

---

## 9. 控件规范

### 9.1 数值行（统一形态）

```csharp
private Grid BuildSliderWithTextBox(string label, double value, double sliderMin, double sliderMax,
                                    Action<double> apply, double step = 1.0, string format = "0.##",
                                    double? textMin = null, double? textMax = null)
{
    var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110, GridUnitType.Pixel) }); // 标签
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });    // 滑条
    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70, GridUnitType.Pixel) });  // 数字框
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
    // slider.ValueChanged / valueBox.LostFocus / ± Click → apply(v)（由调用方 MarkDirty）
    return grid;
}
```

| 约定 | 取值 |
| --- | --- |
| 列宽 | 110px 标签 / 星号 滑条 / 70px 数字框 / auto 按钮 |
| 按钮 | 32×32，`Padding = 0`，内容用 `TextBlock` 居中 |
| 符号 | 减号用 U+2212 `−`（真正的减号 MINUS SIGN，不是连字符），加号用普通 `+`，字号 16 |
| 数字框格式 | 用可回读的 `0.##`；**不要**用 `P0`/`0%`（用户输入 `80` 会被解析成 8000%） |
| 数值范围 | 数字框可超出滑条范围（`textMin/textMax`），滑条只负责快速拖动 |

### 9.2 其他控件

| 场景 | 控件 | 约定 |
| --- | --- | --- |
| 枚举（主题、背景效果） | 横向 `RadioButton` | 描述性文字放括号，如 `Mica (云母)` |
| 布尔 | `ToggleSwitch` | `OnContent/OffContent = "开"/"关"` |
| 独立勾选 | `CheckBox` | |
| 区块标题 | `TextBlock` | `FontSize = 20`，`FontWeight = SemiBold` |
| 弱化说明 | `TextBlock` | `FontSize = 12`，`Opacity = 0.6` |
| 按钮组 | `StackPanel` | `Orientation = Horizontal`，`Spacing = 8` |

### 9.3 无效模式要禁用整行并改写文案

```csharp
void UpdateOpacityRow()
{
    var locked = _settings.BackgroundBlur != BlurMode.Default;   // 云母/亚克力下不透明度无意义
    foreach (var child in opacityRow.Children)
        if (child is Control c) c.IsEnabled = !locked;

    opacityLabel.Text = locked
        ? "窗口不透明度: 100%（云母/亚克力模式固定）"
        : $"窗口不透明度: {_settings.WindowOpacity:P0}";
}
```

> `IsEnabled` 在 `Control` 上，`UIElement` 没有（`Grid`/`StackPanel` 无法直接禁用）——
> 要逐个禁用行内的子控件。

---

## 10. 外观系统

| 模式 | 实现 | 注意 |
| --- | --- | --- |
| 主题 | `root.RequestedTheme = Light / Dark / Default` | 跟随系统读注册表 `AppsUseLightTheme` |
| 默认背景 | 背景图 → GDI+ 高斯模糊 → `WriteableBitmap` → `ImageBrush` | 模糊在 UI 线程外算好再交给 XAML |
| 云母 Mica | `MicaController` + `SystemBackdropConfiguration` | 需 `using WinRT;`；用 `Dispose()`（无 `Close()`） |
| 亚克力 Acrylic | `DesktopAcrylicController` | 同上 |
| 窗口不透明度 | `WS_EX_LAYERED` + `SetLayeredWindowAttributes` | WinUI 3 窗口没有 `Opacity` 属性 |

### 10.1 窗口不透明度

```csharp
public static void ApplyOpacity(Window window, AppSettings settings)
{
    var hwnd = WindowNative.GetWindowHandle(window);
    var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);

    // WS_EX_LAYERED 始终保留：MicaController 需要它
    if ((exStyle & WS_EX_LAYERED) == 0)
    {
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    // 默认模式：滑条控制 alpha；云母/亚克力：写死 255（背景材质自己拥有窗口表面）
    var alpha = settings.BackgroundBlur == BlurMode.Default
        ? (byte)Math.Clamp(settings.WindowOpacity * 255, 25, 255)
        : (byte)255;

    SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
}
```

### 10.2 材质与联动规则

- 材质模式下，标题栏与侧边栏要保持**不透明**，否则出现"半透明标题栏 + 不透明内容"的割裂。
- 材质模式下不透明度滑条要**禁用并改写文案**（见 9.3）。
- 切换模式时记得释放上一个 controller（`MicaController.Dispose()`），避免叠加多个背景。

### 10.3 实测无效 / 不可靠的 API（不要用）

| API | 实测结果 |
| --- | --- |
| `Window.SystemBackdrop = new MicaBackdrop()` | 日志显示已应用，但视觉上无任何变化 |
| `DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ...)` | 窗口直接变成纯黑 `#000000` |
| `Visual.Shadow` | 该属性不存在（CS1061），需要 `SpriteVisual` + `CompositionDropShadow` |
| `RectangleGeometry.RadiusX/RadiusY` | 该属性不存在（那是 WPF），用 Composition `CreateRectangleClip` |
| `UIElement.IsEnabled` | 不存在（在 `Control` 上） |

---

## 11. 持久化与外部变更

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
                // 兜底：路径失效/文件被删时回退默认背景，避免面板变成空白
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

规则：

- **全应用统一路径**（历史上出现过引擎与设置窗口各写一份文件、互相覆盖的 bug）。
- 需要感知外部改动时用 `FileSystemWatcher` + 300ms 防抖后重新加载；设置窗口自己绝不边改边写。
- 默认资源（默认背景图等）随 exe 输出到同目录，用 `AppContext.BaseDirectory` 定位。

---

## 12. 日志与诊断

```csharp
internal static class AppLog
{
    internal static string LogPath => Path.Combine(Path.GetTempPath(), "UiTemplate.log");

    internal static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:O} {message}{Environment.NewLine}"); }
        catch { }   // 日志失败绝不影响主流程
    }
}
```

### 12.1 几何问题：采样实测

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

这套采样方法在本项目里解决过：侧边栏上下细缝的成因、收起"没有动画"的真相、
以及多动画叠加导致的闪烁。

### 12.2 交互复现：模拟点击

```csharp
var peer = new ButtonAutomationPeer(toggleButton);
(peer as Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)?.Invoke();
```

### 12.3 纪律

- 插桩必须带可 `grep` 的标记（如 `SAMPLE`、`TEMP`）；
- 定位完立即删除，并重新构建确认 `grep` 无残留；
- 交付前再跑一次干净构建（`rm -rf bin obj`）确认 0 error。

---

## 13. 排查手册（现象 → 原因 → 动作）

| 现象 | 原因 | 动作 |
| --- | --- | --- |
| exe 秒退，退出码 `0x8000801A` | 自包含运行时 DLL 不在旁边 | 从 `bin\...\<RID>\` 启动，别复制 exe |
| 构建报 MSB3027 / MSB3021 | 旧实例占用 exe | `taskkill /F /IM <App>.exe` 后重建 |
| 亮色模式下系统按钮看不见 | 按钮颜色跟随系统主题 | 设置 `AppWindow.TitleBar.Button*Color` |
| 窗口无法整体变透明 | WinUI 3 窗口没有 `Opacity` | `WS_EX_LAYERED` + `SetLayeredWindowAttributes` |
| 云母不生效 / 窗口纯黑 | `MicaBackdrop` / DWM 属性不可靠 | 用 `MicaController` |
| 布局属性上的动画毫无反应 | 缺 `EnableDependentAnimation` | 设为 `true` |
| 侧边栏上下有细缝 | 模板 pane 3px 边距 + 1px 宿主边框 | 归零 pane Margin + 抹平祖先（第 7.2 节） |
| 侧边栏收起"没有动画" | 模板收起仅 120ms 且同时压缩 pane | 只动画 pane 宽度，时长曲线与模板一致（第 7.3 节） |
| 收起时侧边栏闪烁几帧 | 多套动画抢同一属性 / 圆角裁剪被设成 0 尺寸 | 只保留一个动画；`SyncClipBounds` 加非正尺寸保护 |
| 拖滑条被打断、卡顿 | 实时应用 + 整页重建 | 草稿模型，点"应用"才生效 |
| 应用设置后引擎没反应 | 只改了草稿没镜像到运行时实例 | 应用时先 `_liveSettings.CopyFrom(_settings)` |
| 重启后设置丢失 | 控件回调里没落盘 / 路径不统一 | 只在应用路径 `Save()`；全应用同一 `SettingsPath` |
| 外部改了设置又被覆盖 | 监听器加载与草稿冲突 | 草稿脏时不同步草稿；托盘改动直接写运行时 |
| 切主题后部分控件颜色不变 | 已生成的画刷不会自动跟 | 重建当前页 |
| 侧边栏圆角削掉了内容 | Composition 裁剪作用于整个 pane | 只裁 pane 背景元素，或给内容留内边距 |
| 数值框输入 `80` 变成 8000% | 用了 `P0`/`0%` 格式 → 解析回 80 → 8000% | 用 `0.##`，百分比在写值时分母处理 |
| 长文件名/长文本把后面的按钮顶出窗口、点不到 | 横向 StackPanel 按子元素的完整期望宽度测量，`TextTrimming` 因此不生效 | 换成 `Grid`：文本放星号列、按钮放 auto 列，完整内容放进 `ToolTip` |
| 管理员版无法从普通 shell 启动 | manifest 要求提权 | 用提权 shell 启动；或改回 asInvoker |
| 打包体积巨大 | 自包含运行时 | 可以接受（149MB）；要小体积改用非自包含 + 引导安装 |
| `XamlCompiler warning WMC1509` | NuGet 缓存 WinUI 包版本与 WASDK 声明不一致 | 无害，忽略 |

---

## 14. 从零开始的十步配方（每步带验收标准）

| 步骤 | 做什么 | 验收标准 |
| --- | --- | --- |
| 1 | 按第 3 节建工程（csproj + manifest） | `dotnet build` 通过；从输出目录启动能看到空窗口 |
| 2 | 写 App 外壳：托盘 → 窗口 → 激活，全程 try/catch 记日志 | 日志里出现三条启动记录 |
| 3 | 搭两行根布局 + 自绘标题栏 + 系统按钮配色 | 切换亮/暗主题，标题栏与按钮颜色都跟着变 |
| 4 | 放 `NavigationView`（48/200，Tag 路由）+ 三个空页面 | 收起/展开正常；切页不报错；滚动位置有记忆 |
| 5 | 侧边栏视觉与几何：底色、12px 圆角、零尺寸保护、抹平内缩 | 侧边栏上贴标题栏底、下贴窗口底；圆角只在靠内容一侧 |
| 6 | 收起动画：只动画 pane 宽度（120ms，同曲线） | 收起有可见动画、无闪烁；展开后 pane 宽度恢复 |
| 7 | 设置模型：`AppSettings` + 草稿 + 浮动应用/取消卡片 | 改任意设置不立即生效；点应用生效并落盘；取消可回滚 |
| 8 | 控件：数值三件套、枚举、布尔、无效模式禁用并改写文案 | 三件套可拖、可输入、可用 ± 微调；禁用行不可交互 |
| 9 | 外观：主题、背景图 + 模糊、云母/亚克力、窗口不透明度 | 四种组合切换正常；材质模式下不透明度锁 100% |
| 10 | 自测与收尾 | 删插桩；干净构建 0 warning/0 error；关窗重开设置仍在 |

---

## 15. 改造成自己工具的清单

1. 改 `AssemblyName` / `RootNamespace` / 窗口标题 / 托盘提示文字。
2. 替换三个示例页（`General` / `Appearance` / `Advanced`）为你的功能页；
   每页用 `DisposablePage` 并在其中 `RegisterUnsubscribe` 所有事件订阅。
3. 在 `AppSettings` 增删字段，并同步更新 `CopyFrom` 的字段列表（很容易漏）。
4. 决定是否需要管理员：真的需要才把 `app.manifest` 改成 `requireAdministrator`。
5. 把运行时副作用（推给引擎/服务/设备）集中到 `ApplySideEffects()` 里，
   保持"控件回调不碰运行时"的纪律。
6. 需要新外观模式时，在 `AppearanceManager` 里加分支，并在 UI 上同步解锁/锁定相关滑条。
7. 更新 `README.md`：构建运行规则（尤其是从 bin 启动、先杀旧实例）必须写清楚。

---

## 16. 给 AI Agent 的验收清单

按顺序执行，每条都要有真实输出支撑，不允许仅凭"看起来对"：

```bash
# 1) 干净构建必须是 0 error（0 warning 更好）
rm -rf bin obj && dotnet build -c Release 2>&1 | tail -6

# 2) 产物存在且能启动（从输出目录，不要复制 exe）
ls bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/<App>.exe
bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/<App>.exe
# 验收：%TEMP%\<App>.log 出现 托盘注册 → 窗口创建 → 窗口激活

# 3) 红线检查：控件回调里不得出现 Save()
grep -rn "Save()" --include=*.cs .        # 只应命中：应用按钮、托盘外部改动、退出路径

# 4) 卫生检查：构建产物不得进仓库
git status --short                        # 不应出现 bin/ obj/

# 5) 插桩清理检查（把标记换成你用的字符串）
grep -rn "TEMP\|SAMPLE" --include=*.cs .  # 应为空

# 6) 交付前关闭测试实例
taskkill /F /IM <App>.exe
```

行为层面的验收（需要真人看一眼或按第 12.1 节采样验证）：

- [ ] 收起侧边栏时有连续动画，中途不闪烁、不整体消失。
- [ ] 展开后侧边栏宽度恢复正常（不是停在 48px）。
- [ ] 拖动任意滑条时不被中断、无可感卡顿，且设置**没有**立即生效。
- [ ] 点"应用"后设置立即生效且重启仍在；点"取消更改"后界面回到上次应用的状态。
- [ ] 亮色模式下标题栏三个系统按钮是黑色且可辨认。
- [ ] 云母/亚克力模式下窗口不透明度滑条被禁用且文案说明为 100%。
- [ ] 关窗后进程仍驻留托盘，点托盘"显示窗口"能恢复且保留未应用的草稿。

---

## 17. 附录：实测数据与已知限制

### 17.1 实测数据

| 项目 | 实测值 |
| --- | --- |
| 展开动画（模板） | 350ms，KeySpline `0.1,0.9 0.2,1.0`，clip TranslateX −152 → 0 |
| 收起动画（模板） | 120ms，同曲线，且 pane 宽度同帧压到 48px |
| 收起动画（本方案） | 开始 05:59:31.921 → 结束 05:59:32.040 = **118ms** |
| 导航区几何 | `y=32 h=639`（底边 671） |
| 侧边栏几何（抹平后） | pane `y=32 h=639`（底边 671），保留 1px 内缩 |
| 侧边栏内缩（抹平前） | 上下各 4px（3px 模板边距 + 1px 宿主边框） |
| 自包含产物 | 149MB / 88 个 DLL |
| 干净 Release 构建 | 约 8.4s |
| 模板源码规模 | 约 2467 行（11 个文件，含 README 与工程文件） |
| 日志路径实测 | `%TEMP%\UiTemplate.log`；设置 `%LOCALAPPDATA%\UiTemplate\settings.json` |

### 17.2 已知限制

- 数据基于 Windows 11 + .NET 8 + WASDK 2.4（WinUI 包解析 2.3.6）；更老的 WASDK（如 1.5）缺少
  `MicaController.SystemBackdropConfiguration`、`GaussianBlurEffect` 等 API，代码需降级替换。
- 侧边栏内缩与动画时长依赖当前 NavigationView 模板；微软若改模板，需要按第 12.1 节重新实测后调整常数。
- 高 DPI / 多显示器缩放下建议同样用实测方式核对像素，本文不含缩放换算逻辑。
- 背景模糊采用降采样（长边上限 512、半径上限 24）以控制耗时；需要更高精度请自行提高上限并评估耗时。

---

## 18. 参考

- 模板与文档仓库：<https://github.com/Wei-Canxie/winui3-tool-ui-template>
- 参考实现（MIT）：OsuCursorWin —— 本文所有代码配方与实测数据都来自它的 WinUI 3 版本
- WinUI 3 模板源码（排查看不见的动画/内缩时的终极依据）：
  `~/.nuget/packages/microsoft.windowsappsdk.winui/<ver>/lib/net6.0-windows10.*/Microsoft.WinUI/Themes/generic.xaml`
