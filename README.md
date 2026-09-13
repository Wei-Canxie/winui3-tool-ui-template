# WinUI 3 桌面工具 UI 模板

[English](README.en.md) | 中文

一套可直接照做的 **WinUI 3 桌面工具 UI 设计规范**：自绘标题栏 + 侧边导航外壳 + 多页设置面板 +
可调外观（主题 / 背景图 / 云母 / 亚克力 / 窗口不透明度），全部来自一个已实际运行的托盘常驻工具。

它的用途很具体：**让另一个 Agent（或开发者）从零搭起同风格的 UI 时，不必重新踩一遍坑。**

- 📐 设计规范与代码配方：[docs/UI-DESIGN.md](docs/UI-DESIGN.md)（[English](docs/UI-DESIGN.en.md)）
- 🧪 所有结论都是实测得来的（附实测数据与判断依据），不是"看起来应该这样"

> 本仓库只包含文档，不含应用程序源码。规范来源于 MIT 许可的
> [OsuCursorWin](https://github.com/xyc-233/OsuCursirWin) 项目。

---

## 特性详述

### 1. 外壳：自绘标题栏 + 侧边导航

- `ExtendsContentIntoTitleBar`，根布局只有两行：**32px 标题栏** + 内容区。
- 标题栏底色跟随**应用内主题**（暗 `#2D2D2D` / 亮 `#F3F3F3`），并按窗口不透明度调 alpha。
- 最小化 / 最大化 / 关闭按钮的颜色**手动同步应用主题**（系统只按系统主题上色，
  否则亮色模式会出现"白底白按钮"），包含悬停与按下态。
- `NavigationView` + `LeftCompact`：收起 48px 图标条、展开 200px；页签用 `Tag` 路由，
  页面在 C# 里现场构建（`StackPanel` + `ScrollViewer`），切换时缓存并恢复滚动位置。
- 关窗即隐藏、托盘常驻：`AppWindow.Closing → e.Cancel = true; AppWindow.Hide()`。

### 2. 侧边栏（本套设计里最花功夫的部分）

- **整列背景 + 靠内容一侧 12px 圆角**，贴窗口一侧保持直角；圆角用 Composition
  `CreateRectangleClip`（XAML 的 `RectangleGeometry` 没有 `RadiusX/RadiusY`）。
- 抹平 NavigationView 模板自带的内缩（pane 有 3px 边距 + 1px 宿主边框），
  最终只保留 1px 内缩，侧边栏与标题栏底部、窗口底边严丝合缝。
- **收起动画**：模板自带的收起只有 120ms 且同时把 pane 压成 48px，滑动完全看不见。
  本方案不跟模板抢属性，只动画 pane 自身宽度（同 120ms、同一条 KeySpline），两条曲线同步收缩，
  实测 118ms 一次跑完，不闪、不跳。
- 圆角裁剪带零尺寸保护：尺寸非正时不更新裁剪，避免"整条侧边栏消失若干帧"。

### 3. 设置交互：草稿 + 应用 / 取消更改

- 所有控件改动**只写草稿**并标记脏，右下角浮出 **应用 / 取消更改** 卡片（8px 圆角、
  半透明底、随主题变色、`MinWidth = 96`、应用用系统 `AccentButtonStyle`）。
- 卡片浮在内容区右下角而不占布局行 —— 否则窗口底部会多出一条既不被侧边栏也不被遮罩覆盖的带状区域。
- **应用**：草稿镜像到运行时实例 → 引擎副作用 → 外观与标题栏 → 落盘 → 更新快照。
- **取消更改**：从快照回滚草稿并重建当前页。
- 设定硬规则：控件回调里**绝不**调用 `Save()`，否则未应用的改动会写盘、被运行时监听器加载，
  草稿模型立即失效。

### 4. 控件规范

- 数值项统一用三件套：`标签 | 滑条 | 数字框 | − / +`，四列宽度 110 / * / 70 / auto。
- 按钮固定 32×32、字号 16、内容 `TextBlock` 居中；减号用 U+2212 `−` 而不是连字符。
- 数字框允许超出滑条范围；滑条负责快速拖动，`SmallChange/LargeChange/StepFrequency` 同步 step。
- 枚举用横向 `RadioButton`，布尔用 `ToggleSwitch`，区块标题 `FontSize = 20 / SemiBold`。
- 某设置在当前模式下无意义时（如云母模式下窗口不透明度固定 100%），
  **禁用整行并改写文案**，而不是留一个拖了没反应的滑条。

### 5. 外观系统

| 模式 | 实现要点 |
| --- | --- |
| 主题 | `RequestedTheme`；跟随系统时读注册表 `AppsUseLightTheme` |
| 默认背景 | 背景图 + GDI+ 高斯模糊（半径 0–255）→ `WriteableBitmap` → `ImageBrush`，模糊在 UI 线程外算 |
| 云母 Mica | `MicaController` + `SystemBackdropConfiguration`（`MicaBackdrop`/DWM 属性实测无效） |
| 亚克力 | `DesktopAcrylicController` |
| 窗口不透明度 | `WS_EX_LAYERED` + `SetLayeredWindowAttributes`；WinUI 3 窗口没有 `Opacity` 属性 |

- `WS_EX_LAYERED` 常驻（`MicaController` 需要它）。
- 云母 / 亚克力模式下不透明度**写死 255** 并锁定滑条（背景材质自己拥有窗口表面），
  标题栏与侧边栏同步改为不透明。

### 6. 持久化与诊断

- 设置统一存 `%LOCALAPPDATA%\<App>\settings.json`（全应用同一路径，避免双份文件互相覆盖）。
- 需要感知外部改动时用 `FileSystemWatcher` + 300ms 防抖重新加载；设置窗口自己不边改边写。
- 日志写 `%TEMP%\<App>.log`；几何 / 动画问题一律用 `DispatcherQueueTimer` + `TransformToVisual`
  采样实测坐标定位，排查完删除临时插桩。

### 7. 工程与运行

- 非打包（`WindowsPackageType = None`）+ 自包含（`WindowsAppSDKSelfContained = true`），
  免装运行时。
- `.csproj` 里必须把 `AppxMSBuildToolsPath` 指向本机 VS2022，否则命令行 `dotnet build` 的
  `PriGen` 会失败。
- 自包含构建下**只能从 `bin\...\<RID>\` 目录启动**（根目录那个孤立的 exe 会以 `0x8000801A` 秒退）；
  换新构建前先提权 `taskkill` 掉旧实例，否则构建报 MSB3027/MSB3021。

---

## 怎么把它当成模板用

1. 读 [docs/UI-DESIGN.md](docs/UI-DESIGN.md) 第 2 节，按里面的 `.csproj` / `app.manifest` 建工程。
2. 照第 10 节的十步配方推进：外壳 → 导航 → 侧边栏 → 设置模型 → 控件 → 外观 → 自测。
3. 第 11 节是一张现象到原因的对照表，遇到怪问题先查它。
4. 需要更底层的真相时，直接读 NuGet 包里的 WinUI 模板：
   `~/.nuget/packages/microsoft.windowsappsdk.winui/<ver>/lib/net6.0-windows10.*/Microsoft.WinUI/Themes/generic.xaml`

## 已知限制

- 规范针对 **Win11 / WASDK 2.4（WinUI 包解析为 2.3.6）+ .NET 8** 实测；
  更低版本（如 WASDK 1.5）缺少 `MicaController.SystemBackdropConfiguration`、`GaussianBlurEffect` 等 API。
- 侧边栏与标题栏的贴合、动画结论都建立在 NavigationView 当前模板之上；
  如果微软改了模板（内缩值、动画时长），需要按第 9 节重新实测后调整常数。
- 高 DPI / 多显示器缩放下建议同样用实测方式核对像素（本规范未包含缩放换算逻辑）。

## 许可与致谢

MIT。文档中的代码片段来自 MIT 许可的
[OsuCursorWin](https://github.com/xyc-233/OsuCursirWin)（Copyright (c) 2022 solstice23）。
