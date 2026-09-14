using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;
using MicaAgenda.App.Services;
using MicaAgenda.App.ViewModels;
using MicaAgenda.Desktop.Services;

namespace MicaAgenda.Desktop;

public partial class MainWindow : Window
{
    private readonly AppConfigStore _configStore = new();
    private AppConfig _config;
    private MainViewModel? _viewModel;
    private IntPtr _nativeHandle;
    private TrayIconService? _tray;

    // 关闭被彻底阻断（桌面小部件不该可点 × 关闭）；退出走托盘"退出"菜单，先置位再放行。
    private bool _allowClose;

    // Windows 桌面嵌入用：WPF 会自行重置 HWND 样式导致嵌入失效，故周期性重新压底。
    private DispatcherTimer? _embedWatchdog;
    private bool _embedWatchdogHooked;

    // 行内输入（点加号 / 双击编辑）在嵌入桌面模式下必须临时放开窗口激活，
    // 否则 WS_EX_NOACTIVATE 会让键盘消息永远送不进来。释放完全交给看门狗轮询
    // （见 ShouldHoldTextEntry），不依赖任何回调 —— 漏一次也会在 200ms 内自愈。
    private bool _textEntryActive;
    private DateTime _textEntryGraceUntilUtc;
    private DateTime _textEntryDeadlineUtc;

    // 宽限期内无条件保持放开（刚点开输入框时激活还没落地）；上限兜底，防止逻辑卡住时长期浮在别的窗口上。
    // 上限给得很宽松：正常收回靠「窗口还是前台 + 输入框还有焦点」这两个轮询条件，用户一旦点到别处
    // 就会在 200ms 内收回；只有「一直开着输入框不动」才会走到这个上限。
    private static readonly TimeSpan TextEntryGrace = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan TextEntryMaxDuration = TimeSpan.FromMinutes(30);

    // 时间轴边缘扩展冷却，防止布局变化引发连锁扩展。
    private DateTime _lastExtend = DateTime.MinValue;

    // 数据落盘：WPF 宿主每次改动后 SaveAsync，Avalonia 宿主此前完全没接，改动关闭即丢。
    // 用「防抖定时器（脏了才写）+ 退出前兜底」覆盖所有改动入口，避免在每个处理器里散落保存调用。
    private readonly CalendarDataStore _store = new();
    private readonly ChinaHolidayService _holidayService = new();
    private DispatcherTimer? _saveTimer;

    // 后台服务：与 WPF 宿主对等。_syncRoot 与 MainViewModel/各服务共享，保证跨线程访问 CalendarData 的安全。
    private readonly object _syncRoot = new();
    private McpServer? _mcpServer;
    private ReminderService? _reminderService;
    private BackupService? _backupService;
    private ReportService? _reportService;
    private MindMapReviewSyncService? _mindMapSyncService;
    private TaskApiServer? _apiServer;

    // API/MCP/同步在后台线程改数据后，合并刷新（防高频重建 UI 与重复落盘）。
    private bool _apiRefreshPending;
    private readonly object _apiRefreshLock = new();

    public MainWindow()
    {
        InitializeComponent();
        _config = _configStore.Load();

        // 桌面小部件不允许用「×」或 Alt+F4 / Cmd+W 关闭：任何模式下都阻断关闭。
        Closing += (_, e) => e.Cancel = !_allowClose;

        // 嵌入桌面模式下首帧不得抢焦点 / 激活。
        if (_config.EmbedDesktop)
        {
            ShowActivated = false;
        }

        // 与 WPF 宿主对齐的首屏流程：先空数据渲染出来，再异步加载真实数据。
        _viewModel = new MainViewModel(new CalendarData(), syncRoot: _syncRoot);
        DataContext = _viewModel;

        Opened += OnOpened;
        _tray = new TrayIconService(ShowCalendar, OpenSettings, ExitApplication);
        _ = InitializeAsync();

        // 防抖自动保存：每 800ms 检查一次，脏了才落盘（覆盖所有改动入口，含月视图/右侧面板）。
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _saveTimer.Tick += (_, _) =>
        {
            // 顺手确认窗口外观没被宿主/系统改回去（"×"自己回来、嵌入失效）。
            // 幂等：样式已是目标值时不做任何写入，代价只有一两次 GetWindowLong。
            ReinforceWindowChrome();

            if (_viewModel?.IsDirty == true)
            {
                _ = SaveAsync();
            }
        };
        _saveTimer.Start();
    }

    private async System.Threading.Tasks.Task InitializeAsync()
    {
        try
        {
            var data = await _store.LoadAsync();
            var year = DateOnly.FromDateTime(DateTime.Now).Year;
            var holidays = await _holidayService.LoadCachedOrEmbeddedAsync(year);
            _viewModel = new MainViewModel(data, holidays: holidays, syncRoot: _syncRoot);
            _viewModel.ReviewTaskDeleted += NotifyReviewDeletion;
            DataContext = _viewModel;

            // 先把开机启动相关的诉求落实到本次运行（Windows 专属）。
            ApplyStartupOptions();

            // 数据就位后再启动后台服务（与 WPF 宿主同序）：API/MCP/提醒/备份/报告/思维导图同步。
            StartServices();

            // 应用已保存的视觉设置：背景模式 / 透明度 / 按视图记忆的窗口位置尺寸 + 同步工具栏控件。
            ApplySettingsToWindow();
        }
        catch (Exception ex)
        {
            // 数据加载失败不应崩溃：保持空数据，宿主仍可显示
            AppLog.Error(ex, "MainWindow.Initialize");
        }
    }

    /// <summary>窗口已显示、原生句柄可用后，执行一次桌面嵌入 + 移除关闭按钮。</summary>
    private void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            ApplyNoCloseButton();
            ApplyBackground();
            ApplyDesktopEmbed();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "MainWindow.OnOpened");
        }
    }

    private IntPtr NativeHandle()
    {
        if (_nativeHandle == IntPtr.Zero)
        {
            _nativeHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }

        return _nativeHandle;
    }

    /// <summary>任何模式下都彻底去掉右上角关闭按钮（阻断 + 隐藏）。</summary>
    private void ApplyNoCloseButton()
    {
        if (OperatingSystem.IsWindows())
        {
            var hwnd = NativeHandle();
            if (hwnd != IntPtr.Zero)
            {
                DesktopEmbedService.RemoveCaptionButtons(hwnd);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            MacDesktopEmbedService.HideCloseButton(this);
        }
        // Linux：Closing 已阻断关闭；去掉按钮需要无边框/EWMH，留待托盘阶段一起处理。
    }

    private void ApplyDesktopEmbed()
    {
        if (OperatingSystem.IsWindows())
        {
            // 先起看门狗：此刻句柄未必就绪（Avalonia 的原生窗口可能还没建好），看门狗拿到句柄后
            // 会自己把样式补齐，不依赖这一次调用是否成功。
            StartChromeWatchdog();

            var hwnd = NativeHandle();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            DesktopEmbedService.HideFromTaskbar(hwnd);

            if (_config.EmbedDesktop)
            {
                DesktopEmbedService.EmbedToDesktop(hwnd);
            }
            else
            {
                DesktopEmbedService.SetNoActivateStyle(hwnd, false);
            }

            if (_config.LockWindow)
            {
                CanResize = false;
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            MacDesktopEmbedService.ApplyTo(this, _config.EmbedDesktop);
        }
    }

    /// <summary>
    /// 启动窗口外观看门狗（Windows 专属）。不管有没有开嵌入都跑：
    /// 开了嵌入时负责把窗口压回最底层，没开时至少保证「×」等标题栏按钮不会自己回来。
    /// 所有操作都幂等，"样式已是目标值"时只是一两次 GetWindowLong，代价可以忽略。
    /// </summary>
    private void StartChromeWatchdog()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (!_embedWatchdogHooked)
        {
            _embedWatchdogHooked = true;
            // 33ms（30 次/秒）属于过度轮询：每个 tick 都要走两次窗口样式 / Z 序系统调用，
            // 白白占用 UI 线程。200ms（5 次/秒）足以在一瞬间纠正，系统调用量降到 1/6。
            _embedWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _embedWatchdog.Tick += (_, _) =>
            {
                try
                {
                    ReinforceWindowChrome();
                }
                catch (Exception ex)
                {
                    AppLog.Error(ex, "ChromeWatchdog");
                }
            };
        }

        _embedWatchdog?.Start();
    }

    // ===== 行内输入框抢焦点 =====

    /// <summary>
    /// 把键盘焦点交给刚出现的行内输入框。
    ///
    /// IsVisible 刚变 true 的元素要等一次布局才真正进入可视树，所以按 Loaded 优先级找一次、
    /// 再用 Background 兜底重试。定位必须按 DataContext 精确匹配：直接取第一个 TextBox 会命中
    /// 任务条目自己的编辑框（平时不可见），Focus() 静默失败 —— 正是"点了加号却打不了字"的成因。
    ///
    /// 嵌入桌面模式还要顺带放开窗口激活（见 <see cref="BeginTextEntry"/>）：被
    /// WS_EX_NOACTIVATE 挡住的窗口收不到键盘消息，光调 Focus() 是打不进字的。
    /// 放开只在一小段宽限期内无条件生效，之后由看门狗按实际情况收尾，漏不掉。
    /// </summary>
    private void FocusInlineTextBox(Func<TextBox?> locate)
    {
        void TryFocus()
        {
            var box = locate();
            if (box is null)
            {
                return;
            }

            BeginTextEntry();
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
        }

        Dispatcher.UIThread.Post(TryFocus, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(TryFocus, DispatcherPriority.Background);
    }

    /// <summary>
    /// 把窗口外观重新拉回"桌面小部件"该有的样子（幂等，随时可重复调用）。
    ///
    /// 宿主与系统会在某些时机（点击激活、改尺寸、DPI 变化、切换窗口状态）重写窗口样式：
    /// 把标题栏系统按钮（尤其是"×"）放回来，或者抹掉 WS_EX_NOACTIVATE —— 用户看到的就是
    /// 「叉号自己回来了」「不再是内嵌的了」。与其去堵每一个触发点，不如周期性重放；
    /// 这里用到的四个方法在"样式已是目标值"时都是空操作，一次调用只多一两次 GetWindowLong。
    /// </summary>
    private void ReinforceWindowChrome()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var hwnd = NativeHandle();
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // 任何模式下都彻底去掉标题栏系统按钮（最小化 / 最大化 / 关闭），并隐藏任务栏按钮。
        // 关键点：不能只在启动时做一次 —— 宿主在激活、改尺寸、DPI 变化、从托盘 Show() 等时机
        // 都会把窗口样式写回去，所以必须周期性重放。
        DesktopEmbedService.RemoveCaptionButtons(hwnd);
        DesktopEmbedService.HideFromTaskbar(hwnd);

        if (!_config.EmbedDesktop)
        {
            // 关掉嵌入时必须撤掉「禁止激活」：否则窗口会既沉在其他窗口之下、又点不动。
            _textEntryActive = false;
            DesktopEmbedService.SetNoActivateStyle(hwnd, false);
            return;
        }

        // 正在行内输入：保持窗口可激活，别把 WS_EX_NOACTIVATE 写回去，否则字打不进去。
        if (ShouldHoldTextEntry(hwnd))
        {
            return;
        }

        _textEntryActive = false;
        DesktopEmbedService.EnsureEmbedded(hwnd);
    }

    // ===== 嵌入桌面下的文本输入（放开激活 + 轮询自愈）=====

    /// <summary>
    /// 开始行内输入：临时放开窗口激活，让刚弹出的输入框真的能收到键盘。
    ///
    /// 嵌入桌面给窗口加了 WS_EX_NOACTIVATE（系统层面禁止激活）：鼠标点击照常生效、键盘消息
    /// 却永远送不进来 —— 表现就是"加号点得动、输入框弹出来了却打不了字"。这里把该样式临时
    /// 去掉，并给自己两段时限：宽限期内无条件保持（保证第一时间能打字），之后交给
    /// <see cref="ShouldHoldTextEntry"/> 按"还在编辑吗"逐 tick 判断，最长不超过
    /// <see cref="TextEntryMaxDuration"/> 一定恢复沉底。
    ///
    /// 刻意不在这里停看门狗：释放逻辑全部由看门狗轮询负责，任何一次回调漏掉都会在 200ms 内
    /// 自愈 —— v3.3.0 就是把恢复挂在提交 / 取消 / 失焦事件上，漏一次就把窗口永久留成了
    /// "普通窗口"（用户看到的是内嵌失效、标题栏按钮全回来了）。
    /// </summary>
    private void BeginTextEntry()
    {
        if (!OperatingSystem.IsWindows() || !_config.EmbedDesktop)
        {
            return;
        }

        var hwnd = NativeHandle();
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            _textEntryActive = true;
            var now = DateTime.UtcNow;
            _textEntryGraceUntilUtc = now.Add(TextEntryGrace);
            _textEntryDeadlineUtc = now.Add(TextEntryMaxDuration);

            DesktopEmbedService.SetNoActivateStyle(hwnd, false);
            Activate();
            DesktopEmbedService.RequestForeground(hwnd);
        }
        catch (Exception ex)
        {
            _textEntryActive = false;
            AppLog.Error(ex, "MainWindow.BeginTextEntry");
        }
    }

    /// <summary>
    /// 是否还要继续压住"沉在桌面"的状态（true = 正在编辑，先别把激活禁令写回去）。
    /// 纯轮询、无事件依赖：条件一旦不成立，下一 tick（200ms）立刻恢复嵌入。
    /// </summary>
    private bool ShouldHoldTextEntry(IntPtr hwnd)
    {
        if (!_textEntryActive)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (now > _textEntryDeadlineUtc)
        {
            return false;
        }

        // 宽限期内不看外部状态：刚点开输入框时激活还没落地，这里必须稳住。
        if (now < _textEntryGraceUntilUtc)
        {
            return true;
        }

        // 窗口已不在前台（用户切走了 / 点了别的应用）就不再需要键盘焦点。
        return IsActive
            && DesktopEmbedService.IsForegroundWindow(hwnd)
            && AnyInlineEditorFocused();
    }

    /// <summary>可视树里是否还有行内输入框拿着键盘焦点（提交 / 取消后它们会隐藏并失焦）。</summary>
    private bool AnyInlineEditorFocused()
    {
        foreach (var textBox in this.GetVisualDescendants().OfType<TextBox>())
        {
            if (textBox.IsFocused && textBox.IsEffectivelyVisible)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>在整棵可视树里按 DataContext 找输入框（复习条目 / 面板条目用的是不同实例）。</summary>
    private TextBox? FindVisibleTextBoxByDataContext(object dataContext) =>
        this.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(box => box.IsVisible && ReferenceEquals(box.DataContext, dataContext));

    // ===== 托盘：显示 + 退出 =====

    private void ShowCalendar()
    {
        if (!IsVisible)
        {
            Show();
        }

        Activate();
    }

    // ===== 设置窗口 =====

    private async void OpenSettings()
    {
        if (_viewModel is null)
        {
            return;
        }

        // 备份/报告服务正常都会建好；万一启动阶段异常没建起来，这里兜底补，避免设置面板拿到 null。
        _backupService ??= new BackupService(_viewModel.Data, _syncRoot, () => _config);
        _reportService ??= new ReportService(_viewModel.Data, _syncRoot, () => _config);

        var dialog = new SettingsWindow(
            _config,
            _configStore,
            () => $"http://localhost:{_config.ApiPort}",
            _backupService,
            () => $"http://localhost:{_config.McpPort}/mcp",
            _holidayService,
            _reportService)
        {
            DeleteAllTasksRequested = () =>
            {
                var n = _viewModel?.DeleteAllTasks() ?? 0;
                _ = SaveAsync();
                return n;
            },
            MindMapSyncRequested = SyncMindMapNowAsync,
            TasksImported = () =>
            {
                _viewModel?.RebuildCalendar();
                _viewModel?.MarkDirty();
                _ = SaveAsync();
            },
            HolidayRefreshRequested = () => _ = RefreshHolidaysAsync(),
            ApplyRequested = OnConfigApplied
        };

        // 嵌入桌面模式下主窗体置底且不激活，设置框用非模态显示，避免被一起压底/无法激活。
        if (_config.EmbedDesktop)
        {
            dialog.Show();
        }
        else
        {
            await dialog.ShowDialog(this);
        }
    }

    private void OnConfigApplied(AppConfig config)
    {
        // 端口/开关可能变了：重启后台服务；嵌入/锁定按新配置尽力重应用（完全切换嵌入态需重启）。
        RestartServices();

        if (OperatingSystem.IsWindows())
        {
            CanResize = !config.LockWindow;
        }

        ApplyDesktopEmbed();

        // 关掉嵌入：窗口还压在最底层，得把它捞回前台，否则用户会以为"设置没生效"。
        // （禁止激活的样式已经在 ApplyDesktopEmbed 里撤掉了，这里能真的激活起来。）
        if (OperatingSystem.IsWindows() && !config.EmbedDesktop)
        {
            Activate();
        }
    }

    private void RestartServices()
    {
        DisposeServices();
        lock (_apiRefreshLock)
        {
            _apiRefreshPending = false;
        }

        StartServices();
    }

    private async System.Threading.Tasks.Task RefreshHolidaysAsync()
    {
        try
        {
            var year = DateOnly.FromDateTime(DateTime.Now).Year;
            var holidays = await _holidayService.LoadAndRefreshAsync(year);
            _viewModel?.SetHolidays(holidays);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "MainWindow.RefreshHolidays");
        }
    }

    private async void ExitApplication()
    {
        _saveTimer?.Stop();
        _allowClose = true;

        // 记住当前视图的窗口位置/尺寸（会 MarkDirty，确保随后落盘）。
        SaveCurrentViewBounds();

        // 退出前兜底落盘：防抖定时器可能还没轮到，这里确保脏数据不丢。
        if (_viewModel?.IsDirty == true)
        {
            try
            {
                await SaveAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "MainWindow.ExitApplication.Save");
            }
        }

        DisposeServices();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            lifetime.Shutdown();
        }
    }

    /// <summary>把当前数据写回磁盘并清除脏标记；落盘失败不致命（保留 IsDirty 以便重试）。</summary>
    private async System.Threading.Tasks.Task SaveAsync()
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            await _store.SaveAsync(_viewModel.Data);
            _viewModel.MarkSaved();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "MainWindow.SaveAsync");
        }
    }

    // ===== 后台服务（与 WPF 宿主对等：API/MCP/提醒/备份/报告/思维导图同步）=====

    /// <summary>
    /// 把「高优先级启动」落实到本次运行（Windows 专属，注册表开机自启在保存设置时已写）。
    /// 进程优先级由程序自己提，不需要管理员权限、必定生效 —— 这也是「高优先级」
    /// 真正能被感知到的部分；计划任务缺失时补登记一次，但开机过程中不弹 UAC（太打扰），
    /// 失败只记一条日志，用户可以自己在设置里授权。
    /// </summary>
    private void ApplyStartupOptions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            // 开机自启动：把注册表里的启动项重新指向「当前这份 exe」。装了新版 / 换了安装目录之后，
            // 旧版留下的启动项会指向已经不存在的旧宿主 exe，表现就是「勾了开机自启动但开机后没反应」；
            // SetEnabled 同时会清掉安装包写的旧值名，避免同一个程序开机被拉起两次。
            // 只在用户开着自启时对齐，不动「设置里没开自启」的情况，免得把安装包勾的自启悄悄删掉。
            if (_config.AutoStart)
            {
                AutoStartService.SetEnabled(true);
            }

            if (!_config.HighPriorityStartup)
            {
                return;
            }

            HighPriorityStartupService.ApplyProcessPriority(true);

            var result = EnableHighPriorityTask();
            if (!result.TaskRegistered)
            {
                AppLog.Error(null, "[STARTUP] 高优先级开机任务未登记：" + result.Message);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "MainWindow.ApplyStartupOptions");
        }
    }

    /// <summary>补登记高优先级开机任务（Windows 专属，调用点必须已判断平台）。</summary>
    [SupportedOSPlatform("windows")]
    private static HighPriorityStartupService.SetupResult EnableHighPriorityTask()
        => HighPriorityStartupService.Enable(allowElevation: false);

    private void StartServices()
    {
        if (_viewModel is null)
        {
            return;
        }

        // 关键：先确保 Token 已生成，再启动任何 HTTP 服务，否则单独开启 MCP 会让端点裸奔。
        EnsureAuthToken();

        TryStart(StartApiServer, "Api");
        TryStart(StartMcpServer, "Mcp");
        TryStart(StartReminderService, "Reminder");
        TryStart(StartBackupService, "Backup");
        TryStart(StartReportService, "Report");
        TryStart(StartMindMapSyncService, "MindMapSync");
    }

    private void TryStart(Action start, string name)
    {
        try
        {
            start();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, $"StartServices.{name}");
        }
    }

    private void EnsureAuthToken()
    {
        if (string.IsNullOrWhiteSpace(_config.ApiToken))
        {
            _config.ApiToken = Guid.NewGuid().ToString("N");
            _ = _configStore.SaveAsync(_config);
        }
    }

    private void StartApiServer()
    {
        if (!_config.ApiEnabled || _viewModel is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_config.ApiToken))
        {
            _config.ApiToken = Guid.NewGuid().ToString("N");
            _ = _configStore.SaveAsync(_config);
        }

        _apiServer = new TaskApiServer(
            _viewModel.Data,
            _syncRoot,
            _config.ApiToken,
            OnDataChangedFromApi,
            NotifyReviewDeletion);
        _apiServer.Start(_config.ApiPort);
    }

    private void StartMcpServer()
    {
        if (!_config.McpEnabled || _viewModel is null)
        {
            return;
        }

        _mcpServer = new McpServer(
            _viewModel.Data,
            _syncRoot,
            OnDataChangedFromApi,
            _config.ApiToken,
            NotifyReviewDeletion);
        _mcpServer.Start(_config.McpPort);
    }

    private void StartReminderService()
    {
        if (_viewModel is null)
        {
            return;
        }

        _reminderService = new ReminderService(_viewModel.Data, _syncRoot, () => _config);
    }

    private void StartBackupService()
    {
        if (_viewModel is null)
        {
            return;
        }

        _backupService = new BackupService(_viewModel.Data, _syncRoot, () => _config);
    }

    private void StartReportService()
    {
        if (_viewModel is null || _reportService is not null)
        {
            return;
        }

        _reportService = new ReportService(_viewModel.Data, _syncRoot, () => _config);
        // WPF 版在此订阅 ReportSent 弹 Toast「报告已推送」；Avalonia 宿主暂无 Toast 设施，先不订阅。
    }

    private void StartMindMapSyncService()
    {
        if (_viewModel is null || _mindMapSyncService is not null)
        {
            return;
        }

        _mindMapSyncService = new MindMapReviewSyncService(_viewModel.Data, _syncRoot, () => _config, OnDataChangedFromApi);
    }

    /// <summary>
    /// 设置窗口「立即同步」入口：优先用面板里当前填写的地址 / Token（不必先保存、
    /// 也不受「启用同步」开关限制 —— 这是用户的显式动作），返回给面板展示的结果文本。
    /// </summary>
    internal async System.Threading.Tasks.Task<string> SyncMindMapNowAsync(string? baseUrl, string? token)
    {
        if (_mindMapSyncService is null)
        {
            TryStart(StartMindMapSyncService, "MindMapSync");
        }

        return _mindMapSyncService is null
            ? "同步服务不可用"
            : await _mindMapSyncService.SyncNowAsync(baseUrl, token);
    }

    /// <summary>其它入口（托盘菜单等）：用已保存的配置同步一次。</summary>
    internal System.Threading.Tasks.Task<string> SyncMindMapNowAsync() => SyncMindMapNowAsync(null, null);

    /// <summary>
    /// 复习任务在日历里被勾选 / 取消后，立刻把状态与「状态最后变更时间」推给 my-mindmap agent
    /// （对端按时间戳仲裁，谁新听谁的）。对端的定时轮询是每小时一次，只靠它的话状态变化要等很久；
    /// 这里主动推一次。非复习任务 / 未开启同步 / 没填 Token 都直接跳过，失败也不影响日历本机操作。
    /// </summary>
    private async System.Threading.Tasks.Task PushCompletionToMindMapAsync(TaskItemViewModel task)
    {
        if (!_config.SyncMyMindMapEnabled || string.IsNullOrWhiteSpace(_config.MyMindMapToken))
        {
            return;
        }

        if (!task.Model.IsReviewTask)
        {
            return;
        }

        try
        {
            if (_mindMapSyncService is null)
            {
                TryStart(StartMindMapSyncService, "MindMapSync");
            }

            if (_mindMapSyncService is not null)
            {
                await _mindMapSyncService.PushStatusAsync(_config.MyMindMapToken, task.Model);
            }
        }
        catch (Exception ex)
        {
            // 静默：对端未启动 / Token 失效时不影响日历本机操作，下一次定时同步会再对齐
            AppLog.Error(ex, "MainWindow.PushCompletionToMindMap");
        }
    }
    /// <summary>
    /// 复习任务被删除：通知 my-mindmap agent 一起删掉对应的复习周期。
    ///
    /// 界面右键删除走 <see cref="MainViewModel.ReviewTaskDeleted"/>，HTTP API 与 MCP 走构造时注入的
    /// 回调 —— 三条入口都不能漏，否则下一次同步会按对端复习计划把它重新建回来，
    /// 用户看到的就是"删了又回来"。未开启同步 / 没填 Token 时由服务内部直接跳过。
    /// 推送要走网络，这里绝不能占着调用线程（可能是 HttpListener 后台线程），丢给线程池。
    /// </summary>
    private void NotifyReviewDeletion(CalendarTask task)
    {
        if (!task.IsReviewTask)
        {
            return;
        }

        if (_mindMapSyncService is null)
        {
            TryStart(StartMindMapSyncService, "MindMapSync");
        }

        var service = _mindMapSyncService;
        if (service is null)
        {
            return;
        }

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await service.RegisterReviewDeletionAsync(task);
            }
            catch (Exception ex)
            {
                // 尽力而为：失败只影响对端清理，本机删除照旧
                AppLog.Error(ex, "MainWindow.NotifyReviewDeletion");
            }
        });
    }

    private void OnDataChangedFromApi()
    {
        // 批量接口会在极短时间内连续写入，先合并再刷新，避免高频重建 UI 与重复落盘。
        lock (_apiRefreshLock)
        {
            if (_apiRefreshPending)
            {
                return;
            }

            _apiRefreshPending = true;
        }

        // 后台线程改数据，调度回 UI 线程刷新并保存。
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(250);
                lock (_apiRefreshLock)
                {
                    _apiRefreshPending = false;
                }

                if (_viewModel is null)
                {
                    return;
                }

                _viewModel.RebuildCalendar();
                _viewModel.MarkDirty();
                await SaveAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "OnDataChangedFromApi");
            }
        });
    }

    private void DisposeServices()
    {
        // 逐个兜底释放：任一服务 Dispose 抛异常都不能影响其余资源清理。
        TryDispose(() => _apiServer?.Dispose(), "ApiServer");
        _apiServer = null;
        TryDispose(() => _mcpServer?.Dispose(), "McpServer");
        _mcpServer = null;
        TryDispose(() => _reminderService?.Dispose(), "Reminder");
        _reminderService = null;
        TryDispose(() => _backupService?.Dispose(), "Backup");
        _backupService = null;
        TryDispose(() => _reportService?.Dispose(), "Report");
        _reportService = null;
        TryDispose(() => _mindMapSyncService?.Dispose(), "MindMapSync");
        _mindMapSyncService = null;
    }

    private static void TryDispose(Action dispose, string name)
    {
        try
        {
            dispose();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, $"DisposeServices.{name}");
        }
    }

    // ===== 视图切换 =====

    private void Today_Click(object? sender, RoutedEventArgs e) => _viewModel?.GoToday();
    private void MonthView_Click(object? sender, RoutedEventArgs e) => SwitchView(CalendarViewMode.Month);
    private void WeekView_Click(object? sender, RoutedEventArgs e) => SwitchView(CalendarViewMode.Week);
    private void YearView_Click(object? sender, RoutedEventArgs e) => SwitchView(CalendarViewMode.Year);
    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings();

    // ===== 视觉对等：背景模式 / 透明度 / 按视图记忆窗口位置 =====

    private bool _applyingSettings;
    private bool _applyingBounds;

    private void SwitchView(CalendarViewMode mode)
    {
        if (_viewModel is null)
        {
            return;
        }

        SaveCurrentViewBounds();
        _viewModel.SetViewMode(mode);
        ApplyWindowBounds(GetBoundsForView(mode));
        SaveCurrentViewBounds();
    }

    /// <summary>数据就位后：把工具栏控件、背景、窗口尺寸同步到当前设置。</summary>
    private void ApplySettingsToWindow()
    {
        if (_viewModel is null)
        {
            return;
        }

        var s = _viewModel.Settings;
        _applyingSettings = true;
        try
        {
            SelectBackgroundMode(s.BackgroundMode);
            OpacitySlider.Value = Math.Clamp(s.Opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        }
        finally
        {
            _applyingSettings = false;
        }

        ApplyBackground();
        ApplyWindowBounds(GetBoundsForView(s.ViewMode));
    }

    private void SelectBackgroundMode(CalendarBackgroundMode mode)
    {
        if (mode == CalendarBackgroundMode.ClearBorder)
        {
            mode = CalendarBackgroundMode.None;
        }

        foreach (var item in BackgroundModeBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag?.ToString() == mode.ToString())
            {
                BackgroundModeBox.SelectedItem = item;
                return;
            }
        }

        BackgroundModeBox.SelectedIndex = 0;
    }

    private void BackgroundMode_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (_applyingSettings || _viewModel is null)
        {
            return;
        }

        if (BackgroundModeBox.SelectedItem is not ComboBoxItem item
            || !Enum.TryParse<CalendarBackgroundMode>(item.Tag?.ToString(), out var mode))
        {
            return;
        }

        _viewModel.Settings.BackgroundMode = mode;
        ApplyBackground();
        _viewModel.MarkDirty();
    }

    private void Opacity_Changed(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_applyingSettings || _viewModel is null)
        {
            return;
        }

        _viewModel.Settings.Opacity = Math.Round(e.NewValue, 2);
        ApplyBackground();
        _viewModel.MarkDirty();
    }

    /// <summary>
    /// 把背景模式映射为 Avalonia 的 TransparencyLevelHint + 着色 Background。
    /// WPF 用 Windows DWM（Mica/亚克力）；这里改用 Avalonia 跨平台透明度等级 + 与 WPF 一致的 ARGB 着色，
    /// 由 OS 决定实际可用的背景材质（Windows=Mica/Acrylic，macOS=Blur，Linux=透明/纯色兜底）。
    /// </summary>
    private void ApplyBackground()
    {
        if (_viewModel is null)
        {
            return;
        }

        var s = _viewModel.Settings;
        var mode = s.BackgroundMode == CalendarBackgroundMode.ClearBorder ? CalendarBackgroundMode.None : s.BackgroundMode;
        var alpha = (byte)Math.Clamp(s.Opacity * 255, 6, 255);

        TransparencyLevelHint = mode switch
        {
            CalendarBackgroundMode.Glass => new[]
            {
                WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur,
                WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent
            },
            CalendarBackgroundMode.FrostedWhite or CalendarBackgroundMode.FrostedGray or CalendarBackgroundMode.FrostedDark
                or CalendarBackgroundMode.AcrylicBlue or CalendarBackgroundMode.AcrylicMint => new[]
            {
                WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.Transparent
            },
            CalendarBackgroundMode.Transparent or CalendarBackgroundMode.None => new[] { WindowTransparencyLevel.Transparent },
            _ => new[] { WindowTransparencyLevel.None },
        };

        Background = mode switch
        {
            CalendarBackgroundMode.None => Brushes.Transparent,
            CalendarBackgroundMode.Transparent => Argb((byte)Math.Min((int)alpha, 150), 255, 255, 255),
            CalendarBackgroundMode.FrostedWhite => Argb(alpha, 248, 250, 252),
            CalendarBackgroundMode.FrostedGray => Argb(alpha, 225, 229, 235),
            CalendarBackgroundMode.FrostedDark => Argb((byte)Math.Min((int)alpha, 210), 28, 31, 36),
            CalendarBackgroundMode.AcrylicBlue => Argb((byte)Math.Min((int)alpha, 210), 219, 234, 254),
            CalendarBackgroundMode.AcrylicMint => Argb((byte)Math.Min((int)alpha, 210), 209, 250, 229),
            CalendarBackgroundMode.PaperLight => Argb(alpha, 250, 248, 242),
            CalendarBackgroundMode.Graphite => Argb(alpha, 17, 24, 39),
            _ => Argb(alpha, 255, 255, 255),
        };
    }

    private static IBrush Argb(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));

    private WindowBounds GetBoundsForView(CalendarViewMode mode)
    {
        if (_viewModel is null)
        {
            return new WindowBounds(Position.X, Position.Y, Width, Height);
        }

        var s = _viewModel.Settings;
        var fallback = s.WindowBounds ?? new WindowBounds(120, 90, 980, 680);
        var sizeSource = mode switch
        {
            CalendarViewMode.Month => s.MonthWindowBounds ?? fallback,
            CalendarViewMode.Week => s.WeekWindowBounds ?? fallback,
            CalendarViewMode.Year => s.YearWindowBounds ?? fallback,
            _ => fallback
        };

        return new WindowBounds(fallback.Left, fallback.Top, sizeSource.Width, sizeSource.Height);
    }

    private void ApplyWindowBounds(WindowBounds b)
    {
        _applyingBounds = true;
        try
        {
            var w = Math.Max(320, b.Width);
            var h = Math.Max(240, b.Height);
            var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
            if (screen?.WorkingArea is PixelRect wa)
            {
                w = Math.Min(w, Math.Max(320, wa.Width));
                h = Math.Min(h, Math.Max(240, wa.Height));
                Width = w;
                Height = h;
                var x = Math.Clamp((int)b.Left, wa.X, Math.Max(wa.X, wa.X + wa.Width - (int)w));
                var y = Math.Clamp((int)b.Top, wa.Y, Math.Max(wa.Y, wa.Y + wa.Height - (int)h));
                Position = new PixelPoint(x, y);
            }
            else
            {
                Width = w;
                Height = h;
            }
        }
        finally
        {
            _applyingBounds = false;
        }
    }

    private void SaveCurrentViewBounds()
    {
        if (_viewModel is null || _applyingBounds)
        {
            return;
        }

        var b = new WindowBounds(Position.X, Position.Y, Width, Height);
        var s = _viewModel.Settings;
        s.WindowBounds = b;
        switch (s.ViewMode)
        {
            case CalendarViewMode.Month:
                s.MonthWindowBounds = b with { Left = 0, Top = 0 };
                break;
            case CalendarViewMode.Week:
                s.WeekWindowBounds = b with { Left = 0, Top = 0 };
                break;
            case CalendarViewMode.Year:
                s.YearWindowBounds = b with { Left = 0, Top = 0 };
                break;
        }

        _viewModel.MarkDirty();
    }

    private void DayCell_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: DayCellViewModel cell })
        {
            _viewModel?.SelectCell(cell.Date);
        }
    }

    // ===== 任务操作（右键菜单 / 复选框）=====

    private static TaskItemViewModel? TaskFrom(object? sender) => (sender as Control)?.DataContext as TaskItemViewModel;

    private void CompleteTask_Click(object? sender, RoutedEventArgs e)
    {
        if (TaskFrom(sender) is { IsCompleted: false } task)
        {
            _viewModel?.ToggleTaskCompletion(task.Id);
            _ = PushCompletionToMindMapAsync(task);
        }
    }

    private void IncompleteTask_Click(object? sender, RoutedEventArgs e)
    {
        if (TaskFrom(sender) is { IsCompleted: true } task)
        {
            _viewModel?.ToggleTaskCompletion(task.Id);
            _ = PushCompletionToMindMapAsync(task);
        }
    }

    private void ImportantTask_Click(object? sender, RoutedEventArgs e)
    {
        if (TaskFrom(sender) is { IsImportant: false } task)
        {
            _viewModel?.ToggleTaskImportance(task.Id);
        }
    }

    private void UnimportantTask_Click(object? sender, RoutedEventArgs e)
    {
        if (TaskFrom(sender) is { IsImportant: true } task)
        {
            _viewModel?.ToggleTaskImportance(task.Id);
        }
    }

    private void EditTask_Click(object? sender, RoutedEventArgs e)
    {
        if (TaskFrom(sender) is { IsEditing: false } task)
        {
            task.BeginEdit();
            FocusInlineTextBox(() => FindVisibleTextBoxByDataContext(task));
        }
    }

    private void DeleteTask_Click(object? sender, RoutedEventArgs e)
    {
        if (TaskFrom(sender) is { } task)
        {
            _viewModel?.DeleteTask(task.Id);
        }
    }

    private void TaskCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || box.DataContext is not TaskItemViewModel task)
        {
            return;
        }

        // 防止绑定回写造成的回环：UI 与 model 一致时说明这次事件是程序性同步。
        if (box.IsChecked == task.IsCompleted)
        {
            return;
        }

        _viewModel?.ToggleTaskCompletion(task.Id);
        _ = PushCompletionToMindMapAsync(task);
    }

    // ===== 任务标题行内编辑 =====

    private void TaskEditTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: TaskItemViewModel task })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitEdit(task);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            task.CancelEdit();
            e.Handled = true;
        }
    }

    private void TaskEditTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: TaskItemViewModel task } && task.IsEditing)
        {
            CommitEdit(task);
        }
    }

    private void CommitEdit(TaskItemViewModel task)
    {
        var title = task.EditTitle?.Trim();
        task.IsEditing = false;
        if (!string.IsNullOrWhiteSpace(title))
        {
            _viewModel?.RenameTask(task.Id, title);
        }
    }

    // ===== 日格行内新增 =====

    private void AddTaskOnSelectedCell_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: DayCellViewModel cell })
        {
            return;
        }

        cell.BeginAdd();

        // 等新输入框进入可视树后再抢焦点（按 DataContext 精确匹配本格，避免抢到任务条目的编辑框）
        FocusInlineTextBox(() => FindVisibleTextBoxByDataContext(cell));
    }

    private void InlineTaskTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: DayCellViewModel cell })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitAdd(cell);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            cell.CancelAdd();
            e.Handled = true;
        }
    }

    private void InlineTaskTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: DayCellViewModel cell } && cell.IsAddingTask)
        {
            CommitAdd(cell);
        }
    }

    private void CommitAdd(DayCellViewModel cell)
    {
        var title = cell.DraftTitle?.Trim();
        cell.CancelAdd();
        if (!string.IsNullOrWhiteSpace(title))
        {
            _viewModel?.AddTask(cell.Date, title);
        }
    }

    // ===== 右侧面板：今日任务快速添加 =====

    private void AddTodayTask_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel?.BeginAddTodayTask();

        // 等输入框进入可视树后再抢焦点（按 Tag 精确定位，避开条目内的编辑框）
        FocusInlineTextBox(() => this.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(box => box.IsVisible && Equals(box.Tag, "TodayTaskDraftBox")));
    }

    private void TodayTaskTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitTodayAdd();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _viewModel?.CancelTodayTask();
            e.Handled = true;
        }
    }

    private void TodayTaskTextBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (_viewModel?.IsAddingTodayTask == true)
        {
            CommitTodayAdd();
        }
    }

    private void CommitTodayAdd()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.CommitTodayTask();
        _ = SaveAsync();
    }

    // ===== 右侧面板：条目交互（双击编辑 / 本周行单击定位日期）=====

    private void TodayTaskItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsLeftButton(e, sender))
        {
            return;
        }

        if (e.ClickCount >= 2 && TaskFrom(sender) is { IsEditing: false } task)
        {
            task.BeginEdit();
            FocusInlineTextBox(() => FindVisibleTextBoxByDataContext(task));
            e.Handled = true;
        }
    }

    private void WeekTaskRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 只响应左键：右键要留给 ContextMenu，不能被 Handled 掉。
        if (!IsLeftButton(e, sender) || TaskFrom(sender) is not { } task)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            if (!task.IsEditing)
            {
                task.BeginEdit();
                FocusInlineTextBox(() => FindVisibleTextBoxByDataContext(task));
            }

            e.Handled = true;
        }
        else
        {
            // 单击：把右侧面板定位到该任务所在日期（完成态切换交给复选框，避免误触）
            _viewModel?.SelectCell(task.Date);
        }
    }

    private static bool IsLeftButton(PointerPressedEventArgs e, object? sender)
        => e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed;

    // ===== 右侧面板：批量操作（破坏性，统一二次确认）=====

    private async void ClearPanelDate_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || _viewModel.TodayTasks.Count <= 0)
        {
            return;
        }

        var date = _viewModel.PanelDate;
        var count = _viewModel.TodayTasks.Count;
        if (!await ConfirmAsync("清空当天任务", $"确定要删除 {date:yyyy年M月d日} 的全部 {count} 条任务吗？此操作不可撤销。"))
        {
            return;
        }

        _viewModel.ClearPanelDateTasks();
        _ = SaveAsync();
    }

    private void ClearOverdueTasks_Click(object? sender, RoutedEventArgs e)
        => BulkWeekAction("删除逾期任务", "确定删除全部「逾期未完成」的任务吗？此操作不可撤销。", () => _viewModel!.ClearOverdueTasks());

    private void MarkOverdueCompleted_Click(object? sender, RoutedEventArgs e)
        => BulkWeekAction("标记逾期完成", "把全部「逾期未完成」的任务标记为已完成？", () => _viewModel!.MarkOverdueCompleted());

    private void ClearOpenTasks_Click(object? sender, RoutedEventArgs e)
        => BulkWeekAction("删除本周未完成任务", "确定删除全部本周「未完成」的任务吗？此操作不可撤销。", () => _viewModel!.ClearOpenTasks());

    private void MarkOpenCompleted_Click(object? sender, RoutedEventArgs e)
        => BulkWeekAction("标记本周完成", "把全部本周「未完成」的任务标记为已完成？", () => _viewModel!.MarkOpenCompleted());

    private void ClearCompletedTasks_Click(object? sender, RoutedEventArgs e)
        => BulkWeekAction("删除本周已完成任务", "确定删除全部本周「已完成」的任务吗？此操作不可撤销。", () => _viewModel!.ClearCompletedTasks());

    private void MarkCompletedIncomplete_Click(object? sender, RoutedEventArgs e)
        => BulkWeekAction("还原为未完成", "把全部本周「已完成」的任务还原为未完成？", () => _viewModel!.MarkCompletedIncomplete());

    private async void BulkWeekAction(string title, string prompt, Func<int> action)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (!await ConfirmAsync(title, prompt))
        {
            return;
        }

        action();
        _ = SaveAsync();
    }

    /// <summary>轻量确认对话框（Avalonia 无内置 MessageBox）：模态，点「确定」返回 true。</summary>
    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var result = false;
        var yes = new Button { Content = "确定", MinWidth = 72 };
        var no = new Button { Content = "取消", MinWidth = 72 };

        var win = new Window
        {
            Title = title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { yes, no }
                    }
                }
            }
        };

        yes.Click += (_, _) => { result = true; win.Close(); };
        no.Click += (_, _) => { result = false; win.Close(); };

        await win.ShowDialog(this);
        return result;
    }

    // ===== 时间轴无限滚动（含向上扩展的偏移补偿）=====

    private void MonthScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv || _viewModel is null)
        {
            return;
        }

        if (DateTime.UtcNow - _lastExtend < TimeSpan.FromMilliseconds(350))
        {
            return;
        }

        var offset = sv.Offset.Y;
        var viewport = sv.Viewport.Height;
        var extent = sv.Extent.Height;

        if (offset < 60)
        {
            _lastExtend = DateTime.UtcNow;

            // 向上扩展会在视口上方插入整月内容：若不管，offset 数值不变 → 视口"跳"到新插入的月份，
            // 且 offset 仍 < 60 会连锁触发。记录扩展前的 extent/offset，布局完成后按增量回补 offset，
            // 让视口停留在原来那个月。Loaded 优先级排在 Layout 之后，此时 Extent 已更新。
            var extentBefore = extent;
            var offsetBefore = offset;
            _viewModel.ExtendTimelineBack();
            Dispatcher.UIThread.Post(() =>
            {
                var delta = sv.Extent.Height - extentBefore;
                if (delta > 0.5)
                {
                    sv.Offset = new Vector(sv.Offset.X, offsetBefore + delta);
                }
            }, DispatcherPriority.Loaded);
        }
        else if (extent - viewport - offset < 60)
        {
            _lastExtend = DateTime.UtcNow;
            _viewModel.ExtendTimelineForward();
        }
    }
}