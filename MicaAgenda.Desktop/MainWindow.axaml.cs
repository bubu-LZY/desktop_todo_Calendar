using System;
using System.Collections;
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
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
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

    /// <summary>
    /// 窄窗阈值：窗口窄到放不下日历格子时，主体切换成「只有今日任务」的视图。
    /// 与 WPF 宿主 MicaAgenda.App 的 NarrowLayoutThreshold 取同一个值（460dp）。
    /// </summary>
    private const double NarrowLayoutThreshold = 460.0;

    /// <summary>正在用格子里的悬浮表单新增任务的那个格子（用于"点别处自动保存"）。</summary>
    private DayCellViewModel? _pendingAddCell;

    /// <summary>正在行内改标题的那条任务（同上：点别处要自动落盘）。</summary>
    private TaskItemViewModel? _editingTask;

    /// <summary>
    /// 有模态提示窗开着（例如"还没配飞书"）。
    /// 弹窗会把焦点从表单上抢走，若不挡住，「焦点离开表单就提交」的逻辑会当场把草稿提交、表单收起来，
    /// 用户点完"确定"回来发现表单已经没了。
    /// </summary>
    private bool _modalDialogOpen;

    // 布局 pass 里改可见性会再触发一轮布局；用标记 + Post 推到下一帧，挡掉重入。
    private bool _responsiveLayoutPending;

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
    private UpdateService? _updateService;
    // 更新流程一次只跑一条：自动检查与设置面板的手动「检查更新」可能同时按下，
    // 撞在一起会弹两个窗、下两份包。
    private bool _updateFlowRunning;
    private TaskApiServer? _apiServer;

    // API/MCP/同步在后台线程改数据后，合并刷新（防高频重建 UI 与重复落盘）。
    private bool _apiRefreshPending;
    private readonly object _apiRefreshLock = new();

    /// <summary>窗口最外圈那四条不可见的缩放热区（见 MainWindow.axaml），光标要逐个写上去。</summary>
    private readonly Control[] _resizeGrips;

    public MainWindow()
    {
        InitializeComponent();
        _resizeGrips = [ResizeEdgeTop, ResizeEdgeBottom, ResizeEdgeLeft, ResizeEdgeRight];
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
            _viewModel.ReviewTaskStatusChanged += OnReviewTaskStatusChanged;
            DataContext = _viewModel;

            // 先把开机启动相关的诉求落实到本次运行（Windows 专属）。
            ApplyStartupOptions();

            // 数据就位后再启动后台服务（与 WPF 宿主同序）：API/MCP/提醒/备份/报告/思维导图同步。
            StartServices();

            // 应用已保存的视觉设置：背景模式 / 透明度 / 按视图记忆的窗口位置尺寸 + 同步工具栏控件。
            ApplySettingsToWindow();

            // 启动后自动查一次新版本（默认开启）。延迟几秒：先让首屏和「嵌入桌面」的置底
            // 稳定下来，再谈弹窗，免得开机瞬间就被一个对话框糊住。
            if (_config.AutoCheckUpdate)
            {
                _ = CheckForUpdatesAfterStartupAsync();
            }
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

            // 首帧的 SizeChanged 未必赶在 Opened 之前到（或不触发），这里按当前宽度定一次版式。
            UpdateResponsiveLayout();
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

    /// <summary>
    /// 又有人来启动程序了：把已经在跑的这个窗口叫到前面来，别让用户以为点了没反应。
    ///
    /// 先走一遍 Avalonia 的常规激活，再补一次带「借前台锁」的 RequestForeground ——
    /// 嵌入桌面时窗口本来就压在底下，单靠 Activate 通常抢不到前台。
    /// </summary>
    internal void BringToFront()
    {
        try
        {
            Activate();

            var hwnd = NativeHandle();
            if (hwnd != IntPtr.Zero)
            {
                DesktopEmbedService.RequestForeground(hwnd);
            }
        }
        catch (Exception ex)
        {
            // 抢不到前台只是少了点贴心，不影响主流程。
            AppLog.Error(ex, "MainWindow.BringToFront");
        }
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
            // 嵌入桌面模式需要高频压底 + 文本输入宽限期的 200ms 自愈；非嵌入模式只需偶尔把
            // 偶发的「× 按钮自己回来」纠正一下，没必要 5 次/秒空转。之前非嵌入也以 200ms 轮询，
            // 且每个 tick 都走窗口样式 / Z 序系统调用，与无边框透明分层窗口在 Win10 上的重绘叠加，
            // 放大了整窗闪烁。
            var interval = _config.EmbedDesktop
                ? TimeSpan.FromMilliseconds(200)
                : TimeSpan.FromMilliseconds(3000);
            _embedWatchdog = new DispatcherTimer { Interval = interval };
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
            UpdateCheckRequested = () => RunUpdateFlowAsync(manual: true),
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
            var taskRegistered = false;
            if (_config.HighPriorityStartup)
            {
                HighPriorityStartupService.ApplyProcessPriority(true);

                var result = EnableHighPriorityTask();
                taskRegistered = result.TaskRegistered;
                if (!taskRegistered)
                {
                    AppLog.Error(null, "[STARTUP] 高优先级开机任务未登记：" + result.Message);
                }
            }

            // 开机启动只保留一条路径（见 StartupPlan）：计划任务已经包办开机启动时，
            // 注册表那条必须收掉 —— 旧版本两条都写，用户机器上留下来的结果就是
            // 「一开机冒出两个程序」。这里顺手替老用户修掉，不用再进一次设置。
            if (StartupPlan.ShouldWriteRunKey(_config.AutoStart, taskRegistered))
            {
                // 把注册表里的启动项重新指向「当前这份 exe」。装了新版 / 换了安装目录之后，
                // 旧版留下的启动项会指向已经不存在的旧宿主 exe，表现就是「勾了开机自启动但开机后没反应」；
                // SetEnabled 同时会清掉安装包写的旧值名。
                AutoStartService.SetEnabled(true);
            }
            else if (taskRegistered)
            {
                AutoStartService.SetEnabled(false);
            }

            // 其余情况（没开自启、也没有计划任务）不动注册表：那多半是安装包勾的自启，不该被悄悄删掉。
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
        TryStart(StartUpdateService, "Update");
    }

    private void StartUpdateService() => _updateService ??= new UpdateService();

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
            NotifyReviewDeletion,
            OnReviewTaskStatusChanged);
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
            NotifyReviewDeletion,
            OnReviewTaskStatusChanged);
        _mcpServer.Start(_config.McpPort);
    }

    private void StartReminderService()
    {
        if (_viewModel is null)
        {
            return;
        }

        // 逐条任务的到点提醒推完后要在任务上落一个「已推过」标记，
        // 复用 API 那条「数据变了 → 刷新 + 标记脏 + 落盘」的通路。
        _reminderService = new ReminderService(_viewModel.Data, _syncRoot, () => _config, OnDataChangedFromApi);
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

    // ===== 版本更新 =====

    private async System.Threading.Tasks.Task CheckForUpdatesAfterStartupAsync()
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(4));
            if (_updateFlowRunning)
            {
                return;
            }

            await RunUpdateFlowAsync(manual: false);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "MainWindow.UpdateAutoCheck");
        }
    }

    /// <summary>用户是不是选了「今日内不再提示更新」。</summary>
    private bool IsUpdateSkippedToday() =>
        string.Equals(_config.UpdateSkipDate, DateTime.Today.ToString("yyyy-MM-dd"), StringComparison.Ordinal);

    private void SetUpdateSkipToday(bool skip)
    {
        _config.UpdateSkipDate = skip ? DateTime.Today.ToString("yyyy-MM-dd") : string.Empty;
        _ = _configStore.SaveAsync(_config);
    }

    /// <summary>
    /// 「检查更新 → 询问 → 后台下载 → 提示重启」全流程。设置面板的手动检查与启动时的自动检查
    /// 共用这一段，两条路的文案与行为才不会跑偏。
    ///
    /// <paramref name="manual"/> = true 表示用户主动点了「检查更新」：任何结果都要有回执；
    /// false 表示启动时的自动检查：已是最新 / 检查失败 / 用户选了「今日内不再提示」都安静收场。
    /// </summary>
    private async System.Threading.Tasks.Task<string> RunUpdateFlowAsync(bool manual)
    {
        if (_updateFlowRunning)
        {
            return "正在检查更新…";
        }

        if (_updateService is null)
        {
            TryStart(StartUpdateService, "Update");
        }

        var service = _updateService;
        if (service is null)
        {
            return "更新服务不可用";
        }

        _updateFlowRunning = true;
        try
        {
            // 自动检查时先看「今日内不再提示」：连这次网络请求都省掉。
            if (!manual && IsUpdateSkippedToday())
            {
                return "今日内不再提示更新";
            }

            var result = await service.CheckAsync();
            if (!result.Succeeded || !result.UpdateAvailable || result.Asset is null)
            {
                if (manual)
                {
                    await ShowMessageAsync("检查更新", result.Message);
                }

                return result.Message;
            }

            var latestText = string.IsNullOrWhiteSpace(result.TagName)
                ? "v" + UpdateService.Normalize(result.LatestVersion!)
                : result.TagName!;
            var question =
                $"检测到新版本 {latestText}（当前 {service.CurrentVersionText}）。\n\n" +
                $"要现在下载更新吗？安装包约 {FormatSize(result.Asset.SizeBytes)}，会在后台下载，\n" +
                "下载完成后再问你一次要不要重启安装。";

            var (accepted, skipToday) = await AskUpdateAsync(question);
            if (skipToday)
            {
                SetUpdateSkipToday(true);
            }

            if (!accepted)
            {
                return $"已跳过 {latestText}";
            }

            // 后台下载：不挡界面、不占模态。用户可以在下载期间继续用日历。
            string installerPath;
            try
            {
                installerPath = await service.DownloadAsync(result.Asset);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "MainWindow.UpdateDownload");
                await ShowMessageAsync("下载失败", "更新包下载失败：" + ex.Message + "\n\n可以稍后再试一次。");
                return "下载失败：" + ex.Message;
            }

            var restart = await ConfirmAsync(
                "下载完成",
                $"{latestText} 已经下载完成。\n\n要现在重启并完成更新吗？程序会自动关闭、静默安装，装好后自己重新打开。");
            if (!restart)
            {
                return "更新包已下载：" + installerPath;
            }

            await ApplyUpdateAndExitAsync(installerPath);
            return "正在重启完成更新…";
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "MainWindow.UpdateFlow");
            if (manual)
            {
                await ShowMessageAsync("检查更新", "检查更新失败：" + ex.Message);
            }

            return "检查更新失败：" + ex.Message;
        }
        finally
        {
            _updateFlowRunning = false;
        }
    }

    /// <summary>
    /// 交接给安装包，然后把程序关掉。
    ///
    /// 关之前必须先把数据落盘：安装程序会 taskkill 掉本进程，防抖保存里的脏数据没机会再写。
    /// 重启交给 UpdateService 里的 cmd 助手负责（它 wait 安装程序结束，再 start 本程序）。
    /// </summary>
    private async System.Threading.Tasks.Task ApplyUpdateAndExitAsync(string installerPath)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            await ShowMessageAsync("无法自动更新", "找不到当前程序路径，请手动运行下载好的安装包：\n" + installerPath);
            return;
        }

        try
        {
            UpdateService.LaunchInstallerAndRestart(installerPath, exe);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "MainWindow.UpdateLaunchInstaller");
            await ShowMessageAsync("无法自动更新", "启动安装程序失败：" + ex.Message + "\n\n请手动运行：" + installerPath);
            return;
        }

        // 走正常退出流程（落盘 + 释放服务 + 收掉托盘图标），别留下一个点不动的托盘残影。
        ExitApplication();
    }

    /// <summary>「发现新版本」询问框：是 / 否 + 「今日内不再提示更新」。</summary>
    private async System.Threading.Tasks.Task<(bool Accepted, bool SkipToday)> AskUpdateAsync(string message)
    {
        var accepted = false;
        var skipBox = new CheckBox
        {
            Content = "今日内不再提示更新",
            IsChecked = false,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var yes = new Button { Content = "更新", MinWidth = 72 };
        var no = new Button { Content = "稍后", MinWidth = 72 };

        var win = new Window
        {
            Title = "发现新版本",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            // 这条询问可能是启动后自动弹的（用户没点任何东西）：必须浮在最上面，
            // 否则「嵌入桌面」把主窗口压到最底层时，用户根本看不到这个框。
            Topmost = true,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 14,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    skipBox,
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

        yes.Click += (_, _) => { accepted = true; win.Close(); };
        no.Click += (_, _) => { accepted = false; win.Close(); };

        await win.ShowDialog(this);
        return (accepted, skipBox.IsChecked == true);
    }

    /// <summary>单个「确定」的消息框（Avalonia 无内置 MessageBox）。</summary>
    private async System.Threading.Tasks.Task ShowMessageAsync(string title, string message)
    {
        var ok = new Button { Content = "确定", MinWidth = 72 };
        var win = new Window
        {
            Title = title,
            Width = 420,
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
                        Children = { ok }
                    }
                }
            }
        };

        ok.Click += (_, _) => win.Close();
        _modalDialogOpen = true;
        try
        {
            await win.ShowDialog(this);
        }
        finally
        {
            _modalDialogOpen = false;
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "未知大小";
        }

        var mb = bytes / 1024d / 1024d;
        return mb >= 1 ? $"{mb:0.#} MB" : $"{bytes / 1024d:0} KB";
    }

    /// <summary>
    /// 复习任务在日历里被勾选 / 取消后，立刻把状态与「状态最后变更时间」推给 my-mindmap agent
    /// （对端按时间戳仲裁，谁新听谁的）。对端的定时轮询是每小时一次，只靠它的话状态变化要等很久；
    /// 这里主动推一次。非复习任务 / 未开启同步 / 没填 Token 都直接跳过，失败也不影响日历本机操作。
    /// </summary>
    private async System.Threading.Tasks.Task PushCompletionToMindMapAsync(CalendarTask task)
    {
        if (!_config.SyncMyMindMapEnabled || string.IsNullOrWhiteSpace(_config.MyMindMapToken))
        {
            return;
        }

        if (!task.IsReviewTask)
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
                await _mindMapSyncService.PushStatusAsync(_config.MyMindMapToken, task);
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
    /// <summary>
    /// 复习任务的完成状态变了（单条勾选、批量「标记完成 / 还原未完成」、HTTP API、MCP 都会走到这里的
    /// 前两类；后两类由服务构造时注入的回调直接点名本方法）：立刻把新状态与「状态最后变更时间」
    /// 推给 my-mindmap agent。推送要走网络，绝不能占着调用线程（可能是 HttpListener 后台线程）。
    /// </summary>
    private void OnReviewTaskStatusChanged(CalendarTask task)
    {
        if (!task.IsReviewTask)
        {
            return;
        }

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                // 未开启同步 / 没填 Token 时方法内部直接返回，失败也不影响日历本机操作
                await PushCompletionToMindMapAsync(task);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "MainWindow.OnReviewTaskStatusChanged");
            }
        });
    }

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
        TryDispose(() => _updateService?.Dispose(), "Update");
        _updateService = null;
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
    /// 把背景模式映射成「外壳 Shell 上的 ARGB 着色」。窗口本身固定逐像素透明，由外壳画出
    /// 底色 / 22px 圆角 / 1px 细边框 —— 与 WPF 宿主（WindowStyle=None + AllowsTransparency）同一套观感，
    /// 各模式的差异只体现在这层刷子的颜色与不透明度上（见下面的 Shell.Background switch）。
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

        // 固定请求「逐像素透明」：窗口整块透明，底色 / 圆角 / 细边框全部由外壳 Shell 自己画。
        //
        // 这里**不能**再请求 Mica / AcrylicBlur / Blur 这类系统材质：它们是 OS 在窗口底层画的一层
        // 不透明材质，会把外壳盖住 —— 表现就是「四角变直角（圆角被压在下面看不见）」「透明度滑杆没反应」
        // 「底下多一层白底」。v3.2.0 起宿主换成 Avalonia 且窗口不再带系统标题栏，Mica 才会真的被应用，
        // 所以这个毛病是「无边框 + 请求系统材质」凑一起才出现的；与 WPF 宿主的 AllowsTransparency 对齐即可。
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        // 底色画在外壳 Border 上而不是窗口上：窗口整块透明，圆角外沿才能透出桌面。
        Shell.Background = mode switch
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

        ApplyBackgroundResources(mode);
    }

    /// <summary>
    /// 按背景模式整体替换调色板资源 —— 与 WPF 宿主 MicaAgenda.App.MainWindow.ApplyBackgroundResources
    /// 是同一套键名与同一套取值。XAML 侧所有颜色都写成 {DynamicResource ...}，
    /// 所以这里换掉资源就等于整窗换肤：日期格子、任务胶囊、右侧面板、本周分组卡片一起变。
    ///
    /// 之前的 Avalonia 宿主把颜色写死在 XAML / CellPalette 里，导致不管选哪种背景模式，
    /// 日期格子和右侧任务列表永远是纯白底（用户上报的「不管什么模式都是纯白」）。
    /// </summary>
    private void ApplyBackgroundResources(CalendarBackgroundMode mode)
    {
        var noBackground = mode is CalendarBackgroundMode.None or CalendarBackgroundMode.ClearBorder;
        var dark = IsDarkMode(mode);

        // 深色模式同时把 FluentTheme 的控件主题切到 Dark：否则下拉框 / 滑杆 / 输入框 / 勾选框
        // 会在深色底上露出一排浅色控件。浅色模式显式钉死 Light，不再跟随系统主题。
        RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;

        SetBrush("PrimaryTextBrush", dark ? Argb(255, 243, 244, 246) : Argb(255, 17, 24, 39));
        SetBrush("MutedTextBrush", dark ? Argb(255, 209, 213, 219) : Argb(255, 107, 114, 128));
        SetBrush("CellBorderBrush", dark ? Argb(70, 255, 255, 255) : Argb(24, 0, 0, 0));
        SetBrush("TaskBorderBrush", dark ? Argb(90, 255, 255, 255) : Argb(51, 0, 0, 0));
        SetBrush("TodayCellBackgroundBrush", dark ? Argb(125, 30, 64, 175) : Argb(191, 220, 235, 254));
        SetBrush("TodayCellBorderBrush", dark ? Argb(190, 147, 197, 253) : Argb(153, 59, 130, 246));
        SetBrush("ImportantTaskBackgroundBrush", dark ? Argb(150, 127, 29, 29) : Argb(255, 254, 202, 202));
        SetBrush("ImportantTaskBorderBrush", dark ? Argb(210, 248, 113, 113) : Argb(255, 239, 68, 68));
        SetBrush("ImportantTaskTextBrush", dark ? Argb(255, 252, 165, 165) : Argb(255, 185, 28, 28));
        SetBrush("HolidayBreakBrush", dark ? Argb(130, 127, 29, 29) : Argb(255, 254, 226, 226));
        SetBrush("HolidayWorkBrush", dark ? Argb(130, 30, 64, 175) : Argb(255, 219, 234, 254));
        SetBrush("HolidayTextBrush", dark ? Argb(255, 254, 202, 202) : Argb(255, 153, 27, 27));
        SetBrush("HolidayWorkTextBrush", dark ? Argb(255, 191, 219, 254) : Argb(255, 29, 78, 216));
        SetBrush("WeekDoneCheckBrush", dark ? Argb(255, 134, 239, 172) : Argb(255, 21, 115, 71));
        SetBrush("WeekTaskRowHoverBrush", dark ? Argb(60, 255, 255, 255) : Argb(20, 0, 0, 0));
        SetBrush("WeekGroupBackgroundBrush", dark ? Argb(110, 31, 41, 55) : Argb(143, 255, 255, 255));
        SetBrush("WeekGroupHoverBrush", dark ? Argb(150, 55, 65, 81) : Argb(191, 255, 255, 255));
        SetBrush("TodayPanelBorderBrush", dark ? Argb(140, 255, 255, 255) : Argb(34, 0, 0, 0));
        SetBrush("WindowEdgeBrush", mode switch
        {
            CalendarBackgroundMode.None => Argb(34, 255, 255, 255),
            CalendarBackgroundMode.ClearBorder => Argb(38, 255, 255, 255),
            CalendarBackgroundMode.FrostedDark or CalendarBackgroundMode.Graphite => Argb(42, 255, 255, 255),
            _ => Argb(30, 17, 24, 39)
        });
        SetBrush("ToolbarControlBackgroundBrush", mode switch
        {
            CalendarBackgroundMode.None or CalendarBackgroundMode.ClearBorder => Argb(34, 255, 255, 255),
            CalendarBackgroundMode.FrostedDark or CalendarBackgroundMode.Graphite => Argb(95, 17, 24, 39),
            CalendarBackgroundMode.AcrylicBlue => Argb(125, 219, 234, 254),
            CalendarBackgroundMode.AcrylicMint => Argb(125, 209, 250, 229),
            CalendarBackgroundMode.PaperLight => Argb(155, 255, 251, 235),
            _ => Argb(125, 255, 255, 255)
        });
        SetBrush("ToolbarControlBorderBrush", dark ? Argb(70, 255, 255, 255) : Argb(42, 17, 24, 39));
        SetBrush("ToolbarControlHoverBrush", dark ? Argb(130, 31, 41, 55) : Argb(170, 255, 255, 255));
        SetBrush("ToolbarControlPressedBrush", dark ? Argb(160, 55, 65, 81) : Argb(185, 229, 231, 235));

        if (noBackground)
        {
            // 无背景：格子整块透明，只留文字与任务胶囊（与 WPF 宿主一致）
            SetBrush("DayCellBackgroundBrush", Brushes.Transparent);
            SetBrush("DayCellOutMonthBackgroundBrush", Brushes.Transparent);
            SetBrush("YearMonthBackgroundBrush", Brushes.Transparent);
            SetBrush("TaskBackgroundBrush", dark ? Argb(120, 31, 41, 55) : Argb(130, 236, 253, 245));
            SetBrush("TodayPanelBackgroundBrush", dark ? Argb(215, 36, 42, 52) : Argb(225, 255, 255, 255));
            SetBrush("SelectedCellBackgroundBrush", dark ? Argb(120, 59, 130, 246) : Argb(102, 37, 99, 235));
            return;
        }

        if (dark)
        {
            SetBrush("DayCellBackgroundBrush", Argb(95, 31, 41, 55));
            SetBrush("DayCellOutMonthBackgroundBrush", Argb(45, 31, 41, 55));
            SetBrush("YearMonthBackgroundBrush", Argb(105, 31, 41, 55));
            SetBrush("TaskBackgroundBrush", Argb(135, 55, 65, 81));
            // 深色下右侧面板要更实一点，否则和深色壁纸糊在一起看不清
            SetBrush("TodayPanelBackgroundBrush", Argb(225, 30, 36, 46));
            SetBrush("SelectedCellBackgroundBrush", Argb(150, 59, 130, 246));
            return;
        }

        SetBrush("DayCellBackgroundBrush", mode switch
        {
            CalendarBackgroundMode.AcrylicBlue => Argb(165, 239, 246, 255),
            CalendarBackgroundMode.AcrylicMint => Argb(165, 236, 253, 245),
            CalendarBackgroundMode.FrostedGray => Argb(155, 243, 244, 246),
            CalendarBackgroundMode.PaperLight => Argb(180, 255, 251, 235),
            _ => Argb(175, 255, 255, 255)
        });
        SetBrush("DayCellOutMonthBackgroundBrush", Argb(111, 255, 255, 255));
        SetBrush("YearMonthBackgroundBrush", Argb(175, 255, 255, 255));
        SetBrush("TaskBackgroundBrush", mode switch
        {
            CalendarBackgroundMode.AcrylicBlue => Argb(190, 219, 234, 254),
            CalendarBackgroundMode.AcrylicMint => Argb(190, 209, 250, 229),
            CalendarBackgroundMode.PaperLight => Argb(190, 254, 243, 199),
            _ => Argb(186, 233, 247, 239)
        });
        SetBrush("TodayPanelBackgroundBrush", mode switch
        {
            CalendarBackgroundMode.AcrylicBlue => Argb(190, 239, 246, 255),
            CalendarBackgroundMode.AcrylicMint => Argb(190, 236, 253, 245),
            CalendarBackgroundMode.FrostedGray => Argb(180, 243, 244, 246),
            CalendarBackgroundMode.PaperLight => Argb(200, 255, 251, 235),
            _ => Argb(175, 255, 255, 255)
        });
        SetBrush("SelectedCellBackgroundBrush", Argb(102, 37, 99, 235));
    }

    private void SetBrush(string key, IBrush brush) => Resources[key] = brush;

    private static bool IsDarkMode(CalendarBackgroundMode mode)
        => mode is CalendarBackgroundMode.FrostedDark or CalendarBackgroundMode.Graphite;

    private static IBrush Argb(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));

    /// <summary>
    /// 周视图窗口的高度下限。
    ///
    /// 周视图是竖排的：顶部 7 个日期格子 + 下方任务面板。窗口太矮的话面板只剩一条缝，
    /// 看起来就像"任务面板没了"。所以切到周视图时给个下限，装不下就自动长高 ——
    /// 用户之后照旧可以拖右下角随便改，改完会被记下来。
    /// </summary>
    private const double WeekMinHeight = 500;

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

        var height = mode == CalendarViewMode.Week
            ? Math.Max(sizeSource.Height, WeekMinHeight)
            : sizeSource.Height;

        return new WindowBounds(fallback.Left, fallback.Top, sizeSource.Width, height);
    }

    private void Window_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_responsiveLayoutPending)
        {
            return;
        }

        _responsiveLayoutPending = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _responsiveLayoutPending = false;
                UpdateResponsiveLayout();
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// 窄窗版式：窗口窄到放不下日历格子时，主体只留「今日任务」这一块
    /// （今日任务 + 本周任务完成情况 + 逾期未完成 / 未完成 / 已完成）。
    /// 顶栏的视图切换 / 背景 / 透明度一起收起 —— 否则按了按钮画面没反应，反而更迷惑。
    /// 行为与 WPF 宿主的 UpdateResponsiveLayout 完全一致。
    /// </summary>
    private void UpdateResponsiveLayout()
    {
        if (NormalViewHost is null || NarrowTaskOnlyView is null
            || ViewControlsPanel is null || ViewSwitchPanel is null)
        {
            return;
        }

        var narrow = Bounds.Width > 0 && Bounds.Width <= NarrowLayoutThreshold;
        ViewControlsPanel.IsVisible = !narrow;
        ViewSwitchPanel.IsVisible = !narrow;
        NormalViewHost.IsVisible = !narrow;
        NarrowTaskOnlyView.IsVisible = narrow;
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
            // 点到另一个格子前，先把上一个格子的草稿 / 行内标题落盘（不能只等 LostFocus）
            CommitPendingEditsForPointer(e);
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
            _ = PushCompletionToMindMapAsync(task.Model);
        }
    }

    private void IncompleteTask_Click(object? sender, RoutedEventArgs e)
    {
        if (TaskFrom(sender) is { IsCompleted: true } task)
        {
            _viewModel?.ToggleTaskCompletion(task.Id);
            _ = PushCompletionToMindMapAsync(task.Model);
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
            CommitPendingEdits();
            task.BeginEdit();
            _editingTask = task;
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
        _ = PushCompletionToMindMapAsync(task.Model);
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
        if (ReferenceEquals(_editingTask, task))
        {
            _editingTask = null;
        }

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

        // 换格子加任务前，先把上一张悬浮表单的草稿落盘（指针事件通常已经收过尾，这里兜底）。
        CommitPendingEdits();

        cell.BeginAdd();
        _pendingAddCell = cell;

        // 等悬浮表单进入可视树后再抢焦点（按 Tag 精确匹配表单里的标题框）
        FocusInlineTextBox(() => this.GetVisualDescendants()
            .OfType<TextBox>()
            .FirstOrDefault(box => box.IsVisible && Equals(box.Tag, "CellDraftBox")));
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

    private void CommitAdd(DayCellViewModel cell)
    {
        var title = cell.DraftTitle?.Trim();
        var lead = cell.DraftReminderLead;
        var time = cell.DraftTimeOnly;   // CancelAdd 会把草稿清掉，先取值
        cell.CancelAdd();
        if (ReferenceEquals(_pendingAddCell, cell))
        {
            _pendingAddCell = null;
        }

        // 以前这里没有落盘：格子里加完任务要等下一次别的改动才写文件。
        if (!string.IsNullOrWhiteSpace(title))
        {
            _viewModel?.AddTask(cell.Date, title, lead, time);
            _ = SaveAsync();
        }
    }

    private void CellCommitAdd_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DayCellViewModel cell)
        {
            CommitAdd(cell);
        }
    }

    private void CellCancelAdd_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is DayCellViewModel cell)
        {
            cell.CancelAdd();
            if (ReferenceEquals(_pendingAddCell, cell))
            {
                _pendingAddCell = null;
            }
        }
    }

    /// <summary>
    /// 点别处时把「格子里的新增草稿」和「行内改的标题」一起收尾。
    ///
    /// 为什么不能只靠 LostFocus：日期格子是 Border、任务条是 Border，
    /// 它们都不可获得焦点，点上去并不会让 TextBox 失焦，
    /// 于是用户必须按回车才能保存（历史 bug）。
    ///
    /// <paramref name="source"/> 是引发这次收尾的指针事件源头：
    /// 点的是悬浮表单自己（标题框 / 提醒下拉 / 取消 / 确定 / 展开的下拉项）时**不能**收尾，
    /// 否则一点「提醒时间」下拉就会把草稿先提交掉、表单当场收起。
    /// </summary>
    private void CommitPendingEdits()
    {
        if (_pendingAddCell is { IsAddingTask: true } cell)
        {
            CommitAdd(cell);
        }

        if (_editingTask is { IsEditing: true } editing)
        {
            CommitEdit(editing);
        }
    }

    /// <summary>
    /// 指针按下引起的收尾：指针落在悬浮表单自己身上就什么都不做。
    /// 弹层另开宿主时，任何能到达主窗口的点击天然就在表单之外；
    /// 只有当表单确实挂在本窗口的可视树里（OverlayPopupHost）才需要按坐标排除。
    /// </summary>
    private void CommitPendingEditsForPointer(PointerPressedEventArgs e)
    {
        if (!IsInsideCellAddForm(e) && _pendingAddCell is { IsAddingTask: true } cell)
        {
            CommitAdd(cell);
        }

        if (_editingTask is { IsEditing: true } editing)
        {
            CommitEdit(editing);
        }
    }

    /// <summary>指针是不是落在「格子里那张悬浮新增表单」上。</summary>
    private bool IsInsideCellAddForm(PointerPressedEventArgs e)
    {
        var point = e.GetPosition(this);
        foreach (var visual in this.GetVisualDescendants())
        {
            if (visual is Control { Tag: "CellAddForm" } form
                && TryBounds(form, out var rect)
                && rect.Contains(point))
            {
                return true;
            }
        }

        // TimePicker 的弹出层（时钟面板）挂在它自己的宿主里，但逻辑树能追回 TimePicker，
        // 点它同样不能把草稿提交掉。
        return e.Source is Visual source
               && (source.GetSelfAndVisualAncestors().Any(a => a is Popup)
                   || source.GetSelfAndLogicalAncestors()
                       .Any(a => a is Control { Tag: "CellAddForm" } or Popup or TimePicker));
    }

    private bool TryGetCellAddFormBounds(out Rect rect)
    {
        foreach (var visual in this.GetVisualDescendants())
        {
            if (visual is Control { Tag: "CellAddForm" } form && TryBounds(form, out rect))
            {
                return true;
            }
        }

        rect = default;
        return false;
    }

    private bool TryBounds(Visual visual, out Rect rect)
    {
        rect = default;
        if (visual.Bounds.Width <= 0 || visual.Bounds.Height <= 0)
        {
            return false;
        }

        if (visual.TranslatePoint(new Point(0, 0), this) is not { } origin)
        {
            return false;
        }

        rect = new Rect(origin, visual.Bounds.Size);
        return true;
    }

    // ===== 右侧面板：今日任务快速添加 =====

    private void AddTodayTask_Click(object? sender, RoutedEventArgs e)
    {
        CommitPendingEdits();
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
        if (_viewModel?.IsAddingTodayTask != true)
        {
            return;
        }

        // 焦点很可能只是在表单内部移动（标题 → 时间 → 提前提醒三个下拉），这时绝不能提交：
        // 一提交表单就收起，用户根本没机会填时间和提醒。等焦点落定后再判断一次。
        Dispatcher.UIThread.Post(CommitTodayAddIfFocusLeftForm, DispatcherPriority.Background);
    }

    /// <summary>表单失去全部焦点才提交；焦点还在表单里（点下拉、切换输入框）就保持展开。</summary>
    private void CommitTodayAddIfFocusLeftForm()
    {
        if (_viewModel?.IsAddingTodayTask != true || IsFocusInsideAddTaskForm() || _modalDialogOpen)
        {
            return;
        }

        CommitTodayAdd();
    }

    /// <summary>
    /// 当前键盘焦点是否还在快速添加表单里。
    /// 表单在 DataTemplate 内部，x:Name 生成不了字段，只能用 Tag 认。
    /// </summary>
    private bool IsFocusInsideAddTaskForm()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual focused)
        {
            return false;
        }

        return focused.GetVisualAncestors().Any(ancestor => ancestor is Control { Tag: "AddTaskForm" });
    }

    private void CommitTodayTask_Click(object? sender, RoutedEventArgs e) => CommitTodayAdd();

    /// <summary>
    /// 右侧面板的「提醒时间」换档：选了真的提醒档、但飞书还没配好 → 说清楚这条提醒送不出去。
    ///
    /// 只认「从『不提醒』切到某个提醒档」这一次（见 <see cref="ReminderGate.ShouldWarnOnLeadChange"/>），
    /// 档位之间来回换不重复弹。
    /// IsEffectivelyVisible 这层判断是因为面板模板在宿主里有两份实例（右侧面板 + 窄窗视图），
    /// 两边绑的是同一个 TodayTaskLead，改一次会同时回调 —— 不挡一下会弹出两个一模一样的窗。
    /// </summary>
    private void TaskLeadBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel?.IsAddingTodayTask != true || sender is not Control { IsEffectivelyVisible: true })
        {
            return;
        }

        WarnIfFeishuNotConfigured(FirstLeadLabel(e.RemovedItems), FirstLeadLabel(e.AddedItems));
    }

    /// <summary>日期格子里那张悬浮表单的「提醒时间」换档，判断与提示同上。</summary>
    private void CellLeadBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not Control { DataContext: DayCellViewModel cell } || !cell.IsAddingTask)
        {
            return;
        }

        WarnIfFeishuNotConfigured(FirstLeadLabel(e.RemovedItems), FirstLeadLabel(e.AddedItems));
    }

    /// <summary>下拉的变更项里取出档位文案（Avalonia 给的 AddedItems/RemovedItems 是 IList）。</summary>
    private static string? FirstLeadLabel(IList items)
        => items.Count > 0 ? items[0] as string : null;

    /// <summary>提醒是走 webhook 推的：飞书没配好就提醒用户"这条提醒到点也不会响"。</summary>
    private void WarnIfFeishuNotConfigured(string? previousLabel, string? newLabel)
    {
        // 飞书 webhook 在 AppConfig 里（日历数据那份 CalendarSettings 不管提醒）。
        if (!ReminderGate.ShouldWarnOnLeadChange(_config, previousLabel, newLabel))
        {
            return;
        }

        _ = ShowMessageAsync("提醒无法送达", ReminderGate.FeishuMissingMessage);
    }

    private void CancelTodayTask_Click(object? sender, RoutedEventArgs e) => _viewModel?.CancelTodayTask();

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
            CommitPendingEdits();
            task.BeginEdit();
            _editingTask = task;
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
                CommitPendingEdits();
                task.BeginEdit();
                _editingTask = task;
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

    // ===== 无标题栏窗口的拖动 / 边缘缩放 =====
    //
    // 窗口用了 SystemDecorations=None（桌面挂件不该带系统标题栏），代价是系统不再提供
    // 「拖标题栏移动窗口」和「拖边缘缩放」。这里在外壳上自己补回来，规则与 WPF 宿主一致：
    //   · 锁定窗口（LockWindow）时两者都禁用；
    //   · 落在按钮 / 输入框 / 下拉框 / 滑块 / 滚动条、或日历条目上时不拖窗口（否则点它们会变成拖窗口）；
    //   · 距边缘 8px 内开始缩放，其余位置开始移动。

    /// <summary>窗口边缘的命中宽度（与 WPF 宿主一致）。</summary>
    private const double ResizeEdgeSize = 8;

    private void Shell_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_config.LockWindow || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // 点窗口空白处也算「点别处」：把没提交的草稿收尾，别让用户以为已经保存了。
        CommitPendingEditsForPointer(e);

        if (IsInteractiveDragSource(e.Source))
        {
            return;
        }

        var edge = GetEdgeAtPosition(e.GetPosition(this));
        if (edge is not null)
        {
            BeginResizeDrag(edge.Value, e);
            e.Handled = true;
            return;
        }

        try
        {
            BeginMoveDrag(e);
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // 桌面嵌入 / 子窗口等状态下系统可能中断拖动，忽略即可（与 WPF 宿主同样兜底）。
        }
    }

    private void Shell_PointerMoved(object? sender, PointerEventArgs e)
    {
        ApplyResizeCursor(_config.LockWindow
            ? ArrowCursor
            : GetEdgeAtPosition(e.GetPosition(this)) switch
            {
                WindowEdge.West or WindowEdge.East => new Cursor(StandardCursorType.SizeWestEast),
                WindowEdge.North or WindowEdge.South => new Cursor(StandardCursorType.SizeNorthSouth),
                WindowEdge.NorthWest or WindowEdge.SouthEast => new Cursor(StandardCursorType.TopLeftCorner),
                WindowEdge.NorthEast or WindowEdge.SouthWest => new Cursor(StandardCursorType.TopRightCorner),
                _ => ArrowCursor,
            });
    }

    /// <summary>
    /// 把光标写到「指针底下那一圈」的每个元素上。
    ///
    /// 光标必须逐元素设置：它不会从窗口往下继承，而外壳 Border 与四条缩放热区（见 MainWindow.axaml）
    /// 是同级节点、各自都是独立的命中目标 —— 只写在其中一个上，指针落到另一个上面时图标就不会变，
    /// 用户看到的就是"角落里没有斜着的缩放图标"。
    /// </summary>
    private void ApplyResizeCursor(Cursor cursor)
    {
        Shell.Cursor = cursor;
        foreach (var grip in _resizeGrips)
        {
            grip.Cursor = cursor;
        }
    }

    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);

    /// <summary>光标位置命中的窗口边缘（用于缩放）；不在边缘上时返回 null。</summary>
    private WindowEdge? GetEdgeAtPosition(Point position)
    {
        var left = position.X < ResizeEdgeSize;
        var right = position.X > ClientSize.Width - ResizeEdgeSize;
        var top = position.Y < ResizeEdgeSize;
        var bottom = position.Y > ClientSize.Height - ResizeEdgeSize;

        if (top && left)
        {
            return WindowEdge.NorthWest;
        }

        if (top && right)
        {
            return WindowEdge.NorthEast;
        }

        if (bottom && left)
        {
            return WindowEdge.SouthWest;
        }

        if (bottom && right)
        {
            return WindowEdge.SouthEast;
        }

        if (left)
        {
            return WindowEdge.West;
        }

        if (right)
        {
            return WindowEdge.East;
        }

        if (top)
        {
            return WindowEdge.North;
        }

        return bottom ? WindowEdge.South : null;
    }

    /// <summary>
    /// 事件源是否落在「可交互的东西」上：按钮 / 输入框 / 下拉框 / 滑块 / 滚动条，
    /// 或日历的格子、任务条目、月份卡片。这些位置不能触发窗口拖动。
    /// </summary>
    private static bool IsInteractiveDragSource(object? source)
    {
        if (source is not Visual visual)
        {
            return false;
        }

        foreach (var ancestor in visual.GetSelfAndVisualAncestors())
        {
            if (ancestor is Button or ToggleButton or TextBox or ComboBox or Slider or ScrollBar or Thumb
                or ListBoxItem or MenuItem or TabItem or CalendarDatePicker)
            {
                return true;
            }

            if (ancestor is Control { DataContext: DayCellViewModel or TaskItemViewModel or MonthSummaryViewModel })
            {
                return true;
            }
        }

        return false;
    }

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