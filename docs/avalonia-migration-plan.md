# MicaAgenda WPF → Avalonia 迁移范围评估

> 目标：同一套代码在 Windows / macOS / Linux 三平台打包发布，并与 my-mindmap-agent 做到**任务级双向同步**。
> 本文档是迁移前的一次性范围清点，作为后续拆分任务的依据。

## 1. 现状清点

| 项 | 值 |
|---|---|
| 解决方案 | `MicaAgenda.sln`（`MicaAgenda.App` + `MicaAgenda.Core` + `MicaAgenda.Tests` + `MicaAgenda.Tests.Windows`） |
| 目标框架 | WPF 宿主 `net9.0-windows`（`UseWPF` / `UseWindowsForms`）；`MicaAgenda.Core` 与跨平台测试为 `net9.0` |
| 总规模 | 约 14 900 行（App 约 13 850，Tests 约 1 054） |
| 主窗口 XAML | `MainWindow.xaml` 1 665 行 |
| 设置窗口 XAML | `SettingsWindow.xaml` 251 行 |
| 主窗口 code-behind | `MainWindow.xaml.cs` 3 128 行 |
| 主要 ViewModel | `MainViewModel.cs` 1 238 行（已移入 `MicaAgenda.Core`） |
| 测试 | 45 个（`MainViewModelTests` 32 / `CalendarCoreTests` 11 个方法，含 `Theory` 展开后共 45 例） |

**基线验证（2026-09-12）**：
- `dotnet test MicaAgenda.Tests/MicaAgenda.Tests.csproj` → 失败 0 / 通过 **40**（跨平台 `net9.0`，只依赖 `MicaAgenda.Core`）
- `dotnet test MicaAgenda.Tests.Windows/MicaAgenda.Tests.Windows.csproj` → 失败 0 / 通过 **5**（注册表自启、Win32 像素换算、WPF 圆角裁剪）
- `dotnet test MicaAgenda.sln` → 失败 0 / 通过 **45**（两个测试工程都执行）
- 合计 40 + 5 = **45**，与迁移前基线一致。

> ✅ 已修复的缺陷（2026-09-12）：此前 `MicaAgenda.Tests` **没有登记进 `MicaAgenda.sln`**，导致 `dotnet test MicaAgenda.sln` **静默跑 0 个测试并返回成功**（假绿）。现已把 `MicaAgenda.Core` / `MicaAgenda.Tests` / `MicaAgenda.Tests.Windows` 全部补进 sln，并验证 sln 级测试确实执行 45 例。
>
> 拆分方式：跨平台测试留在 `net9.0` 的 `MicaAgenda.Tests`，只引用 `MicaAgenda.Core`；平台绑定测试移入 `net9.0-windows` 的 `MicaAgenda.Tests.Windows`，引用 WPF 宿主。后者需要访问 `internal` 成员，故在 `MicaAgenda.App/AssemblyInfo.cs` 补了 `InternalsVisibleTo("MicaAgenda.Tests.Windows")`。

> 📦 打包发布指南已另行输出：[`docs/package-and-release-multiplatform-SKILL.md`](package-and-release-multiplatform-SKILL.md)。其中明确记录：**WPF 宿主无法产出 macOS / Linux 包**（`NETSDK1082`），三平台矩阵必须在 Avalonia 宿主落地后才可启用。

### 主窗口 XAML 的 WPF 语法密度（决定 UI 重写成本）

| 构造 | 数量 | Avalonia 兼容性 |
|---|---|---|
| `Trigger` | 106 | ❌ 无此机制，必须改选择器 |
| `DataTrigger` | 36 | ❌ 无此机制 |
| `ControlTemplate` | 50 | ⚠️ 概念相同，模板内绑定/部件命名需改 |
| `<Style>` | 40 | ⚠️ 选择器语法不同 |
| `DynamicResource` | 141 | ⚠️ 机制在，资源键要重建 |
| `StaticResource` | 87 | ⚠️ 同上 |
| `Binding` | 121 | ✅ 大体兼容 |
| `x:Name` | 35 | ✅ |
| 事件绑定 | 83 | ⚠️ 事件签名/路由事件类型不同 |
| `Storyboard` / 动画 | 0 | ✅ 无动画需重写 |
| `x:Name`（设置窗口） | 48 | ✅ |

## 2. 分层结论

### A. 可直接搬（约 5 500 行，≈40%）

**Models / ViewModels —— 零 WPF UI 依赖**
- `MainViewModel`(1238)、`DayCellViewModel`(254)、`TaskItemViewModel`(227)、`MonthBlockViewModel`、`MonthSummaryViewModel`、`ViewModelBase`
- 只用 `INotifyPropertyChanged` + `ObservableCollection`，无 `Dispatcher` / `Brush` / `Visibility` 引用
- `RelayCommand` 用 `System.Windows.Input.ICommand` —— Avalonia 复用同一命名空间与接口，**零改动**

**Services（纯 BCL）**
- `TaskApiServer`(814)、`McpServer`(727)、`McpDocsProvider`(607)、`BackupService`(441)、`ReportService`(363)、`ChinaHolidayService`(361)、`TaskReportBuilder`(348)、`ReminderService`(276)、`MindMapReviewSyncService`(242)、`WebhookSender`(222)、`CalendarDataStore`(119)、`AppConfigStore`(119)、`CalendarService`、`AuthUtil`

两个关键验证结论：

1. **`HttpListener` 是跨平台的。** 官方文档明确：Windows 上基于 `HTTP.sys`，**Linux/macOS 上是托管实现**，差异仅为「不支持 HTTPS、特性较少」。本项目两个服务器都只监听 `http://localhost:PORT/` 与 `http://127.0.0.1:PORT/` 明文，鉴权是自实现的 Bearer token（`AuthUtil`），不依赖 NTLM/Windows 认证 → **`McpServer` 与 `TaskApiServer` 零改动即可在 mac/Linux 运行**。这也是与 my-mindmap-agent 集成的关键链路，风险等级低。
   - 参考：[HttpListener Class (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistener?view=net-9.0) —— “on Windows it's built on `HTTP.sys`, while on Linux and macOS it uses a managed implementation that doesn't support HTTPS”

2. **数据目录会「能用但不地道」。** 13 处使用 `Environment.SpecialFolder.ApplicationData`：Windows → `%APPDATA%`，macOS/Linux → `~/.config`。功能可用，但 macOS 惯例是 `~/Library/Application Support`。建议抽一个 `AppPaths` 统一解析（mac 用 `~/Library/Application Support/MicaAgenda`），顺带把散落的路径拼接收口。

**Converters（4 个，约 80 行）**
- `IValueConverter` 接口签名一致，仅需替换 using：`System.Windows.Data` → `Avalonia.Data.Converters`

### B. 必须重写（约 2 500 行）

| # | 对象 | 行数 | 说明 |
|---|---|---|---|
| 1 | `MainWindow.xaml` + `SettingsWindow.xaml` | 1 916 | **最大单块工作量**。106 个 `Trigger` + 36 个 `DataTrigger` 在 Avalonia 无对应机制，须逐个改写为选择器（`:pointerover` / `:pressed` / `:disabled` / `:checked`）+ `Style` 嵌套，或把状态提到 ViewModel 用 `Classes` 绑定；50 个 `ControlTemplate` 内绑定需重写；6 套主题（Mica / 亚克力 ×3 色 / 磨砂 / 石墨）的 20+ 色刷资源字典需重建 |
| 2 | `MainWindow.xaml.cs` 的 Win32 窗口管理簇（1282–1725 行） | ~444 | 自定义拖拽移动、8 方向边缘缩放、工作区钳制、Z 序升降（`RaiseToTop` / `LowerToBottom` / `ForceLowerToBottom`）、光标命中测试、`DpiChanged`。改用 Avalonia 的 `ExtendClientAreaToDecorationsHint`、`Window.ResizeGrip`、`Screen.WorkingArea`、`ScalingChanged`、`Topmost`/`ShowActivated` 重写 |
| 3 | `ToastHelper.cs` | 128 | WPF 无边框透明窗口 + `DoubleAnimation` → Avalonia 无边框窗口 + `Transitions`/动画 |
| 4 | `ClipboardHelper.cs` | 194 | 整块 Win32（`OpenClipboard`/`GlobalAlloc` + 退避重试）→ Avalonia `IClipboard.SetTextAsync`（自带跨平台重试语义）。**该文件基本可整体删除**，其存在本身只是为了绕开 WPF 的 `CLIPBRD_E_CANT_OPEN` |
| 5 | `App.xaml.cs` 单实例与启动 | 127 | `Global\...` 命名 Mutex（`Global\` 前缀是 Windows-only）+ `FindWindow`/`SetForegroundWindow`/`ShowWindow` 激活已有窗口 → 平台无关命名 Mutex（或文件锁）+ Avalonia 窗口激活。`EnsureWindirForWpf()` 整段删除 |
| 6 | `MessageBox.Show` ×3 处 + 对话框 | — | Avalonia 无 `MessageBox`；需一个统一对话框封装（或引入 `MessageBox.Avalonia`） |

### C. 平台能力：条件化或降级

| 特性 | 现有实现 | 迁移方案 |
|---|---|---|
| Mica / 亚克力 / 磨砂背景（`WindowEffects.cs` 176 行） | `dwmapi` + `user32` P/Invoke（`DwmSetWindowAttribute` / `SetWindowCompositionAttribute`） | Avalonia `TransparencyLevelHint`：Win11 可给 Mica/Acrylic，macOS 给 Blur，**Linux 依赖合成器，无合成器时必须降级为纯色**。另注：当前 `Apply()` 本身就是空实现（只调 `Disable`） |
| **桌面嵌入**（`DesktopEmbedService.cs` 119 行）：沉到所有窗口之下 + 隐藏任务栏按钮 + 不抢焦点 + 周期性看门狗 | `WS_EX_TOOLWINDOW` / `WS_EX_NOACTIVATE` / `HWND_BOTTOM` / `SetWindowPos` | **无跨平台等价物**。建议 Windows 保留 P/Invoke 实现并在非 Windows 隐藏该选项；非 Windows 降级为「无边框 + 可切换置顶」 |
| 托盘图标（`TrayIconService.cs` 68 行） | WinForms `NotifyIcon` + `ContextMenuStrip` | Avalonia 内建 `TrayIcon`（三平台支持），改写量小 |
| 开机自启（`AutoStartService.cs` 50 行） | 注册表 `...\CurrentVersion\Run` | 三平台各一套：Windows 注册表 / macOS `~/Library/LaunchAgents/*.plist` / Linux `~/.config/autostart/*.desktop`。建议抽 `IAutoStartService` 三实现 |
| 高优先级自启（`HighPriorityStartupService.cs` 89 行） | `schtasks /RL HIGHEST` + PowerShell `-Priority High` | **Windows-only**，非 Windows 隐藏该项；跨平台可用 `Process.PriorityClass` 尽力而为 |
| 打开备份目录 | `Process.Start("explorer.exe", dir)` | `ILauncher.LaunchFileInfoAsync` |
| 打开项目主页 | `Process.Start(UseShellExecute=true)` | `ILauncher.LaunchUriAsync` |
| 单实例并激活已有窗口 | `FindWindow` + `SetForegroundWindow` | 见 B5 |
| `ResizeMode.NoResize`（锁定窗口） | WPF 属性 | Avalonia `CanResize` |
| `SystemParameters.PrimaryScreenWidth`（Toast 定位） | WPF | `Screens.Primary.WorkingArea` |

### D. 测试与安全网

- `MainViewModelTests`（32 个）→ **零改动**（纯 VM/Services 断言）
- `CalendarCoreTests`（11 个方法 / 共 13 例）→ 其中 2–3 个直接操作注册表（`Registry.CurrentUser.OpenSubKey`）需改为条件编译（`#if WINDOWS`）或改测抽象接口
- **迁移前基线已跑绿：45/45 通过**（2026-09-12，详见第 1 节）。UI 重写期间全程靠它们兜回归（UI 层无自动化测试，属已知缺口）

## 3. 工作量与阶段拆分

| 阶段 | 内容 | 产出 | 对应任务 |
|---|---|---|---|
| 0 | 基线：现有测试全绿（WPF 版，**已验证 45/45**） | 回归安全网 | #26 ✅ |
| 1 | 建档：新建 Avalonia 项目，搬 Models + ViewModels + Services + Converters，跑通编译与 45 个测试 | 无 UI 的可用核心 | #28 |
| 2 | 平台抽象层：`AppPaths` / `IAutoStartService` / `IClipboard` / `ILauncher` / 对话框，Windows 特性条件化 | 三平台可编译 | #28 |
| 3 | 主窗口 UI：XAML 重写（Style/Trigger/Template）+ 窗口管理簇 + Toast + 托盘 | 功能对齐的可运行界面 | #28 |
| 4 | 设置窗口 UI + 6 套主题资源字典 | 功能对齐 | #28 |
| 5 | 三平台 CI：`dotnet publish` + 打包（Win NSIS/Inno、macOS .app/.dmg、Linux AppImage/.deb） | Release 产物 | #29 |
| 6 | 任务级双向同步协议（两端） | 高度集成 | #30 / #31 |

## 4. 风险清单

1. **XAML Trigger 改写是最大不确定项**（106+36 处）。建议先做一个「一屏」垂直切片（例如月视图 + 任务卡片）验证改写套路，再批量推进。
2. **Linux 透明窗口 / 亚克力不可靠**（依赖合成器）。需明确「无合成器时降级为不透明」的验收口径，否则 Linux 用户会遇到黑底窗口（WPF 版注释里同样记录过这个坑）。
3. **macOS 未签名分发的 Gatekeeper 拦截**：`.dmg` 双击会被拦。需决定是否走签名/公证，还是在文档中给 `xattr -dr com.apple.quarantine` 说明。
4. **macOS 桌面嵌入无解**，只能砍掉或降级——需确认这是可接受的功能差异。
5. **GPL-3.0 许可**：仓库为 GPL-3.0，跨平台发布时必须随附 LICENSE 与源码（源码 zip），不能只发二进制。
6. 与 my-mindmap-agent 的同步：现有 `MindMapReviewSyncService` 只处理 `[MM复习]` 前缀任务的完成态，且依赖 REST `/api/desk-calendar/*`（其 DTO 缺 `updatedAt`）。要做**任务级双向同步**必须改用 MCP 通道，见 #30。

## 5. 已决策（2026-09-12）

1. **替换 vs 并存 → 用单一 Avalonia 代码库整体替换 WPF。**
   目标结构：`MicaAgenda.Core`（跨平台类库：Models / ViewModels / Services / Converters）+ `MicaAgenda.Desktop`（Avalonia 界面 + 平台服务）+ `MicaAgenda.Tests`（引用 Core）。
   旧 WPF 工程 `MicaAgenda.App` 在 Avalonia 版达到功能对齐前保留在仓库中作为对照，对齐后删除（历史仍在 git 里）。
2. **Windows 专属特性 → 非 Windows 直接隐藏。**
   桌面嵌入（`DesktopEmbedService`）与高优先级自启（`HighPriorityStartupService`）仅 Windows 显示；mac/Linux 设置页不呈现这两项——不给用户「点了没反应的开关」。
3. **macOS 签名 → 先发未签名包**，在 README 注明 `xattr -dr com.apple.quarantine`（或右键打开）绕过 Gatekeeper；暂不引入 Apple Developer 账号与 CI 签名密钥。

## 6. 验收口径（迁移完成的判定）

- [ ] 三平台（Windows x64 / macOS x64+arm64 / Linux x64）均能 `dotnet publish` 出可运行产物
- [ ] `MicaAgenda.Tests` 全部通过（≥ 基线 45 例），且**测试项目已登记进 sln**（修掉静默 0 测试的缺陷）
- [ ] Windows 端功能与现版对齐：视图切换（年/月/周）、任务增删改查、内联编辑、标签、托盘、主题与不透明度、备份、报表、提醒 webhook、MCP 服务、任务 API 服务
- [ ] macOS / Linux 端可运行，且「桌面嵌入 / 高优先级自启」为隐藏而非报错
- [ ] 与 my-mindmap-agent 的 MCP 通道在 mac/Linux 上可用（`http://127.0.0.1:<port>/mcp`）
- [ ] Release 产物随附 GPL-3.0 LICENSE 与源码 zip
