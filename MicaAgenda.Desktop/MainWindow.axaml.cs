using System;
using System.Collections;
using System.Linq;
using System.Runtime.InteropServices;
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
using Avalonia.Media.Imaging;
using Avalonia.Platform;
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
    /// 正在以程序方式把视口滚到锚点月（点「今天」、换月、首屏定位）。
    /// 这种滚动不是用户在浏览，不能拿去判断"滚到尽头了，该扩展时间轴了"。
    /// </summary>
    private bool _programmaticScroll;

    /// <summary>
    /// 顶栏在放不下时的兜底宽度：把窗口缩到最小也不能再小，低于它就直接进「换行」这一档。
    /// 2026-09 之前这里还兼作「窄窗只留今日任务」的判定；现在窄窗视图单独用
    /// <see cref="NarrowLayoutThreshold"/>，两者分开。
    /// </summary>
    private const double NarrowLayoutThreshold = 380.0;

    /// <summary>
    /// <summary>
    /// 顶栏单行能容下「日期信息 + 常驻按钮」的最小宽度。再窄就换行：
    /// 第一行只留日期信息，第二行放「今天 / 视图 / 设置 / 锁定」。
    /// </summary>
    private const double TopBarWrapThreshold = 340.0;

    /// <summary>
    /// 窗口可被拖到的最小宽度 —— 也就是「所有设置按钮都还能看见、不被遮住」的那条线。
    ///
    /// 用户的要求是：缩到最小就停在这儿，不能再缩。取值 = 换行档下第二行整组按钮的自然宽度
    /// （今天 + 视图下拉 + 设置 + 锁定，含间距与两侧内边距）再留一点余量，保证换行后
    /// 第二行不会自己再溢出。比它更窄时按钮组会开始互相挤压 / 被裁掉，所以不能再小。
    ///
    /// 顶栏换行阈值 <see cref="TopBarWrapThreshold"/> 比它小，两者顺序是：
    /// 先收起「背景 + 透明度」→ 再换行（第二行放按钮）→ 最后停在这个最小宽度上。
    /// </summary>
    private const double MinWindowWidth = 300.0;

    /// <summary>窗口可被拖到的最小高度：顶栏两行 + 主体至少露出一行内容。</summary>
    private const double MinWindowHeight = 240.0;

    /// <summary>外壳 Shell 的圆角半径（与 MainWindow.axaml 里 Shell 的 CornerRadius 一致，单位 dp）。</summary>
    private const double ShellCornerRadius = 22.0;

    // ===== Windows 10 亚克力材质 + 窗口外形裁剪 =====
    //
    // Windows 10 上「无边框 + 逐像素透明」是分层窗口（WS_EX_LAYERED），没有 DWM 合成缓冲，
    // 每次重绘都要整窗重新合成 —— 这就是用户反复上报的闪烁。Win10 改用 DWM 亚克力材质后窗口
    // 不再是分层窗口，闪烁消失；代价是圆角要靠 SetWindowRgn 裁出来（见 UpdateWindowShape）。
    //
    // 用可空类型记「当前实际请求的是哪种材质」：null = 还没请求过。ApplyBackground 会被透明度
    // 滑杆高频调用，靠这个值去重，避免每动一下滑杆都重新协商一次窗口材质。
    private bool? _acrylicBackdrop;

    // 上次裁剪出来的外形：只在「材质切换」或「窗口像素尺寸变化」时重裁。透明度滑杆每动一下都会
    // 走到 ApplyBackground，若无条件 SetWindowRgn，滑杆本身就会变成新的闪烁源。
    private bool _windowShapeIsRounded;
    private int _windowShapeWidth;
    private int _windowShapeHeight;

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
    private System.Threading.Timer? _gcTrimTimer;
    private readonly Services.MouseDismissHook _aiDismissHook = new();

    // ===== 窗口几何（位置 / 尺寸）记忆 =====
    //
    // 用户反馈「每次重启都回到默认位置」，根因有两层：
    //   1) 启动时读的是 GetBoundsForView（位置取当前默认值），存下来的位置从没被用过 —— 已修；
    //   2) 位置只在「托盘退出」和「切视图」两条路径上写盘，而重启 / 关机走的是 WM_ENDSESSION，
    //      那条路不经过 ExitApplication —— 位置根本没来得及存。
    //
    // 所以记忆要挂在「几何真的稳定下来」这件事上，而不是挂在退出路径上：
    // 拖动 / 缩放停下 ~1.2s 就落一次盘，进程无论怎么死，磁盘上总有一份不那么旧的位置。
    private DispatcherTimer? _boundsSaveTimer;

    /// <summary>几何变化计数：每次 Position/Size 变化 +1，用于判断"稳定了没有"。</summary>
    private long _boundsRevision;

    /// <summary>已落盘的几何版本号；与 <see cref="_boundsRevision"/> 相等说明没有待存的改动。</summary>
    private long _boundsSavedRevision;

    /// <summary>窗口几何（位置 / 尺寸）稳定多久之后落盘。</summary>
    private static readonly TimeSpan BoundsSaveQuietPeriod = TimeSpan.FromMilliseconds(1200);

    /// <summary>
    /// 订阅窗口几何变化（拖 / 缩）的入口装了没有。装钩子的时机是首次 <c>Opened</c> ——
    /// 此时原生窗口才真正存在，Win32 侧的事件钩子才挂得上。
    /// </summary>
    private bool _boundsWatcherHooked;

    /// <summary>
    /// 数据文件加载失败时置 true，全程关闭自动保存。
    /// 这是数据安全闸门：加载失败意味着原文件已损坏并被隔离改名，若照常自动保存，
    /// 空数据会立刻在原路径新建一个空文件，用户会误以为数据被清空且无法找回。
    /// </summary>
    private bool _suppressAutoSave;

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

    /// <summary>
    /// 当前开着的设置面板（同一时刻最多一个）。
    /// 「检查更新」的结果窗与下载浮窗都从设置面板里触发，若把它们挂到主窗体名下，
    /// 在「嵌入桌面」模式下主窗体被压到最底层，这些窗就会**被设置面板挡住点不到**
    /// （用户上报过）。所以它们统一挂到设置面板名下，见 <see cref="ResolveDialogOwner"/>。
    /// </summary>
    private Window? _settingsWindow;

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
        _opacityTintTimer.Tick += OpacityTintTimer_Tick;

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

        // 自动保存改成「脏了才启动的一次性防抖」：MarkDirty 会触发 IsDirty 的 PropertyChanged，
        // 宿主订阅它并启动 2 秒单次定时器（见 SubscribeAutoSave），干净状态下零定时器唤醒。
        // 不再用 800ms 常驻轮询 —— 那会每天空转约 10.8 万次去读一个 bool。
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            if (_viewModel?.IsDirty == true)
            {
                _ = SaveAsync();
            }
        };

        // 几何稳定定时器：拖动 / 缩放期间被不断顺延，停手 1.2s 才真正落一次盘。
        // 和上面那个"数据脏了才存"的定时器是两件事，不能共用一个：窗口几何不算 CalendarData 的脏，
        // 它只在 MarkDirty 之后才会被写出去，因此这里必须自己判断"有没有待存的几何改动"。
        _boundsSaveTimer = new DispatcherTimer { Interval = BoundsSaveQuietPeriod };
        _boundsSaveTimer.Tick += (_, _) =>
        {
            _boundsSaveTimer.Stop();
            PersistBoundsIfChanged();
        };
    }

    /// <summary>
    /// 窗口几何变了（拖动或缩放）。只登记"有改动 + 顺延稳定定时器"，真正的写盘交给
    /// <see cref="PersistBoundsIfChanged"/> —— 拖动过程中每帧都写盘既浪费又可能写到一半状态。
    /// </summary>
    private void OnWindowGeometryChanged()
    {
        _boundsRevision++;

        // 程序自身在应用记忆位置（启动恢复 / 切视图）时也会走到这里，但那些场景
        // 不需要"防抖落盘"：切视图路径自己会调 SaveCurrentViewBounds，启动路径刚读完盘。
        if (_applyingBounds || _boundsSaveTimer is null)
        {
            return;
        }

        // 锁定位置时不记 —— 用户已经把窗口钉住了，此时的位置变化（如果有）不是他的意图。
        if (_config.LockWindow)
        {
            return;
        }

        _boundsSaveTimer.Stop();
        _boundsSaveTimer.Start();
    }

    /// <summary>
    /// 几何确实变过才写盘。写的是 <see cref="SaveCurrentViewBounds"/> 那套（通用 + 按视图），
    /// 但多一步：写盘成功后才推进 <see cref="_boundsSavedRevision"/>，失败了下次还会再试。
    /// </summary>
    private void PersistBoundsIfChanged()
    {
        if (_boundsRevision == _boundsSavedRevision)
        {
            return;
        }

        if (_viewModel is null || _applyingBounds)
        {
            return;
        }

        // SaveCurrentViewBounds 内部只在 _applyingBounds 时早退，这里已经挡掉了。
        SaveCurrentViewBounds();

        // 记下这次落盘对应的几何版本。注意 SaveCurrentViewBounds 自己会 MarkDirty，
        // 之后由 _saveTimer 把数据真正写到磁盘上；那一步失败不影响这里的版本推进 ——
        // 最坏情况是"应用崩了、位置没写进去"，用户下次拖动会再存一次。
        _boundsSavedRevision = _boundsRevision;
    }

    private async System.Threading.Tasks.Task InitializeAsync()
    {
        try
        {
            CalendarData data;
            try
            {
                data = await _store.LoadAsync();
            }
            catch (CalendarDataLoadException ex)
            {
                // 数据文件损坏，已隔离改名留存。以空数据继续运行（挂件不能因为读不出文件就起不来），
                // 但全程关闭自动保存 —— 否则空数据会立刻在原路径新建一个空文件，用户会误以为数据没了。
                AppLog.Error(ex, "MainWindow.Initialize.Load");
                _suppressAutoSave = true;
                data = new CalendarData();
            }

            var year = DateOnly.FromDateTime(DateTime.Now).Year;
            var holidays = await _holidayService.LoadCachedOrEmbeddedAsync(year);
            _viewModel = new MainViewModel(data, holidays: holidays, syncRoot: _syncRoot);
            _viewModel.ReviewTaskDeleted += NotifyReviewDeletion;
            _viewModel.ReviewTaskStatusChanged += OnReviewTaskStatusChanged;
            // 时间轴 / 年视图结构每次重建（换月、点「今天」）后都要把视口拉回锚点月。
            // 桌面端以前完全没订阅这个事件，而月/年视图的"正在看哪个月"是由滚动位置表达的，
            // 于是点「今天」只改了 ViewModel 状态、画面一动不动。
            _viewModel.TimelineRebuilt += OnTimelineRebuilt;
            _viewModel.WeekScrollHeadTrimmed += OnWeekScrollHeadTrimmed;
            _viewModel.WeekScrollHeadPrepended += OnWeekScrollHeadPrepended;
            SubscribeAutoSave();

            // 构造期就可能已经变脏（周期地平线补实例），而 SubscribeAutoSave 只对**之后**的
            // 变化生效 —— 不补这一下，新补出来的实例要等用户下次改动才会落盘。
            // 落了盘，数据文件才是"自洽"的（否则每次启动都要重新算一遍地平线）。
            if (_viewModel.IsDirty && _saveTimer is not null)
            {
                _saveTimer.Start();
            }

            DataContext = _viewModel;

            // 右上角迷你 AI 对话卡片：注入数据与配置，与 MCP 的 AI 工具共用同一套任务执行逻辑。
            AiPanel.Initialize(_viewModel.Data, _syncRoot, () => _config, OnDataChangedFromApi, CreateHostActions());

            // 首屏也要定位一次：时间轴是以今天为中心往前多铺了一个月，
            // 不想办法滚一下，第一眼看到的是上个月。
            Dispatcher.UIThread.Post(() => ScrollViewportToAnchor(0), DispatcherPriority.Loaded);

            // 先把开机启动相关的诉求落实到本次运行（Windows 专属）。
            ApplyStartupOptions();

            // 数据就位后再启动后台服务（与 WPF 宿主同序）：API/MCP/提醒/备份/报告/思维导图同步。
            StartServices();

            // 应用已保存的视觉设置：背景模式 / 透明度 / 按视图记忆的窗口位置尺寸 + 同步工具栏控件。
            ApplySettingsToWindow();

            // 长驻挂件空闲时定期做一次「优化式」非阻塞 GC，把工作集还给系统。
            // 日历重建 / 任务增删改是高频短生命周期分配，默认并发 GC 会把已分配但未触达的内存
            // 留在进程里，导致常驻内存越堆越高；Optimized 模式只在确实有垃圾时收集，不打断交互。
            _gcTrimTimer = new System.Threading.Timer(
                _ => GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false),
                null,
                TimeSpan.FromMinutes(3),
                TimeSpan.FromMinutes(3));

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

            // 位置 / 尺寸变化的监听：必须在原生窗口就绪之后装（见方法注释）。
            InstallBoundsWatcher();

            // 关机 / 重启前把窗口位置同步落盘：这条路径不经过 ExitApplication，不在这里存就等于没存。
            InstallSessionEndHandlers();

            // 首帧的 SizeChanged 未必赶在 Opened 之前到（或不触发），这里按当前宽度定一次版式。
            UpdateResponsiveLayout();

            // 默认视图就是年视图时，月卡要等这次布局后才量得出实际列宽，
            // Loaded 优先级补一次字号回填（UpdateResponsiveLayout 里 Bounds 还是 0 会跳过）。
            Dispatcher.UIThread.Post(UpdateYearFontScale, DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "MainWindow.OnOpened");
        }
    }

    /// <summary>
    /// 监听窗口几何变化，交给防抖定时器落盘。
    ///
    /// <para><b>为什么不用 Avalonia 的 <c>PositionChanged</c>。</b>实测（1.11 系列）拖动过程中它
    /// <b>一次都不触发</b> —— 我们这条拖动路径是自定义标题栏里的 <c>BeginMoveDrag</c>，
    /// 走的是原生拖拽，Avalonia 侧的位置属性直到拖拽结束后才被同步，事件也就跟着不来。
    /// 而 <c>SizeChanged</c> 是可靠的（拖右下角缩放时会连续触发），所以两条一起用：
    /// 尺寸靠 Avalonia 事件，位置靠本类自己装的 Win32 事件钩子。</para>
    ///
    /// <para>Win32 钩子失败也不致命：尺寸那条路还能记，位置则退回"退出时兜底保存"。</para>
    /// </summary>
    private void InstallBoundsWatcher()
    {
        if (_boundsWatcherHooked)
        {
            return;
        }

        _boundsWatcherHooked = true;

        // 尺寸：Avalonia 自己的事件，拖右下角会连续触发。
        SizeChanged += (_, _) => OnWindowGeometryChanged();

        // 位置：原生窗口的 WM_WINDOWPOSCHANGED 事件钩子（拖动 / 缩放都会发）。
        // 只装一次；钩子内部会过滤掉「位置其实没变」的消息，避免布局期刷爆防抖定时器。
        if (OperatingSystem.IsWindows())
        {
            var hwnd = NativeHandle();
            if (hwnd != IntPtr.Zero)
            {
                DesktopEmbedService.InstallWindowMoveWatch(hwnd, (x, y) =>
                {
                    // 坐标与当前记录的完全一致 = 系统在重发消息，不算一次真实的移动。
                    if (_lastWatchedPosition is { } last && last.X == x && last.Y == y)
                    {
                        return;
                    }

                    _lastWatchedPosition = new PixelPoint(x, y);
                    OnWindowGeometryChanged();
                });
            }
        }
        else
        {
            // macOS / Linux：没有原生钩子，用 Avalonia 的事件兜底（这两条平台拖拽时多半也不触发，
            // 但至少不会漏掉全程 —— 兜底仍在退出路径上）。
            PositionChanged += (_, _) => OnWindowGeometryChanged();
        }
    }

    /// <summary>上一次被 Win32 钩子报告的位置，用来过滤重复的 WM_WINDOWPOSCHANGED。</summary>
    private PixelPoint? _lastWatchedPosition;

    /// <summary>
    /// 关机 / 重启 / 注销前把窗口位置（以及当前数据）落盘。
    ///
    /// <para><b>为什么要装两层。</b>关机前能给到的落盘机会不止一条，两条都装上才敢说
    /// "重启后位置还在"：</para>
    /// <list type="number">
    /// <item>Win32 <c>WM_QUERYENDSESSION</c> / <c>WM_ENDSESSION</c>：最标准的一条。窗口过程
    /// 已经被本程序子类化（原本只为吃掉最小化消息，见
    /// <see cref="DesktopEmbedService.GuardAgainstMinimize"/>），顺路把这两个消息也接出来。</item>
    /// <item><c>AppDomain.ProcessExit</c>：最后一道防线。进程无论怎么被收走都会走这里，
    /// 覆盖面最广 —— 包括上面那条钩子因为句柄重建而没挂上的情况。</item>
    /// </list>
    ///
    /// <para>没有用 .NET 的 <c>SystemEvents.SessionEnding</c>：它需要额外的
    /// <c>Microsoft.Win32.SystemEvents</c> 包，而在这个"关掉一个日历挂件"的场景里，
    /// 上面两层已经够用，不值得为此多引一个依赖。</para>
    ///
    /// <para>两层共享一个「只跑一次」的闸门（见 <see cref="SaveBoundsOnShutdown"/>），
    /// 不会重复写盘。</para>
    /// </summary>
    private void InstallSessionEndHandlers()
    {
        // ① Win32 消息钩子（Windows）：由 DesktopEmbedService 的窗口过程子类化转发过来。
        DesktopEmbedService.InstallSessionEndWatch(SaveBoundsOnShutdown);

        // ② 进程退出兜底：进程被收走的最后时刻一定会走这里。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => SaveBoundsOnShutdown();
    }

    /// <summary>关机 / 重启落盘的"只跑一次"闸门（两层通道会各来一次）。</summary>
    private bool _boundsSavedOnShutdown;

    /// <summary>
    /// 会话结束路径上的落盘：<b>同步</b>写。
    ///
    /// 这里绝不能用 <c>await SaveAsync()</c>：关机时留给进程的时间只有极短的几十毫秒到几百毫秒，
    /// 异步文件 I/O 很可能还没真正落到磁盘，进程就没了 —— 表现就是"位置存了但重启后没生效"。
    /// 所以窗口几何 + 当前数据一起走同步写。
    /// </summary>
    private void SaveBoundsOnShutdown()
    {
        if (_boundsSavedOnShutdown)
        {
            return;
        }

        _boundsSavedOnShutdown = true;

        try
        {
            // 几何：把当前视图的位置 + 尺寸记进设置。
            SaveCurrentViewBounds();

            // 数据：同步落到磁盘。异步的 _saveTimer 很可能已经来不及跑了。
            if (_viewModel is { IsDirty: true } viewModel && !_suppressAutoSave)
            {
                // 落盘失败不致命（下次启动最多是这一次的改动丢了），但还是留个痕迹。
                if (!_store.Save(viewModel.Data))
                {
                    AppLog.Error(null, "会话结束落盘被跳过：上一次保存仍持锁或写入失败");
                }
                else
                {
                    viewModel.MarkSaved();
                }
            }
        }
        catch (Exception ex)
        {
            // 关机路径上绝不能抛异常：抛出去会影响系统结束会话。
            AppLog.Error(ex, "MainWindow.SaveBoundsOnShutdown");
        }
    }

    /// <summary>
    /// 取当前的原生窗口句柄。
    ///
    /// <b>不能缓存。</b>Avalonia 会在若干时机重建底层原生窗口（托盘恢复、DPI 变化、
    /// 主题/渲染后端切换等），旧句柄随之失效。早期版本把句柄缓存在字段里只取一次，
    /// 结果看门狗之后一直在操作一个已经销毁的句柄：失效句柄上 <c>IsWindowVisible</c>
    /// 恒为 false，判定"窗口被藏了"→ 每 200ms 无意义地反复调 ShowWindow，
    /// 而真正的新窗口反而没人管（"显示桌面后拉不回来"的一种成因）。
    ///
    /// <c>TryGetPlatformHandle()</c> 本身只是读 Avalonia 内部字段、不涉及系统调用，
    /// 每 tick 取一次的代价可以忽略，用它换掉缓存是稳赚的。
    /// </summary>
    private IntPtr NativeHandle()
    {
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle != IntPtr.Zero)
        {
            _lastHandleFromAvalonia = true;
            return handle;
        }

        // Avalonia 交不出句柄（原生窗口被重建 / 销毁，典型是刚被「显示桌面」收走时），
        // 这时绝不能放弃 —— 否则看门狗就在空转，窗口再也回不来。
        // 退回 Win32 层按进程 ID 直接找：拿到的是此刻真实存在的窗口。
        _lastHandleFromAvalonia = false;
        return DesktopEmbedService.FindOwnMainWindow();
    }

    /// <summary>上一次句柄是不是来自 Avalonia（false = 走了 Win32 枚举兜底）。诊断用。</summary>
    private bool _lastHandleFromAvalonia = true;

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
            // 间隔 200ms。
            //
            // 为什么不能放宽到秒级：用户实测点「显示桌面」后要等 1~2 秒窗口才回来，
            // 说明前台事件钩子（InstallForegroundWatch）在「显示桌面」这条路径上并不可靠 ——
            // Win+D / 右下角细条把 WorkerW 提上来的同时，前台窗口可能根本没变（或事件没按预期送到），
            // 于是只能靠轮询兜底。200ms 是"体感无延迟"和"开销可控"的平衡点：
            // 一秒 5 次、每次几个只读调用（样式已是目标值就提前返回），远低于 UI 刷新本身的开销。
            //
            // 事件钩子仍然保留：它在前台真正切换时能立刻纠一次 Z 序（比如用户切到别的应用），
            // 把「显示桌面」这类前台不变的场景交给 200ms 轮询兜底。
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

            // 前台窗口一变（含「显示桌面」把桌面提为前台）就立刻纠一次 Z 序，
            // 这样窗口被桌面层盖住的瞬间就能回来，不用等 2s 的下一轮 tick。
            DesktopEmbedService.InstallForegroundWatch(() =>
            {
                try
                {
                    ReinforceWindowChrome();
                }
                catch (Exception ex)
                {
                    AppLog.Error(ex, "ForegroundWatch");
                }
            });
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

    /// <summary>上一次写看门狗体检日志的时刻（限流用）。</summary>
    private static DateTime _lastHealthLogAt = DateTime.MinValue;

    /// <summary>
    /// 每 tick 记录一次看门狗的"体检结果"，供真实环境复现时定位。
    ///
    /// 之所以需要它：窗口"消失"在开发环境里复现不出来（四条权威路径实测全都免疫），
    /// 只能靠现场日志反推。它回答三个关键问题：
    /// 句柄是 Avalonia 给的还是 Win32 枚举兜底的（暴露"句柄失效"这个根因）、
    /// 窗口此刻到底是什么形态（最小化 / 隐藏 / 正常）、还原指令发出去后有没有落地。
    /// </summary>
    private void LogWatchdogHealth(IntPtr hwnd)
    {
        var now = DateTime.UtcNow;
        if ((now - _lastHealthLogAt).TotalSeconds < 3)
        {
            return;
        }

        _lastHealthLogAt = now;

        var hidden = DesktopEmbedService.IsHiddenOrMinimized(hwnd);
        // 只在"窗口确实异常"时记，正常态不必刷屏。
        if (!hidden)
        {
            return;
        }

        DesktopEmbedService.LogRestoreEvent(
            hwnd,
            $"health src={(_lastHandleFromAvalonia ? "avalonia" : "win32-enum")}");
    }

    /// <summary>
    /// 同一个看门狗 tick 内允许的还原重试次数。
    ///
    /// 「显示桌面」施压是持续性的，一次 ShowWindow 可能刚好落在两次施压的缝里被立刻压回。
    /// 等下一个 200ms tick 太慢（用户能明显看到窗口消失一段时间），所以在 tick 内小步重试。
    /// 次数不能大：这里是 UI 线程，重试多了会反过来卡住界面。
    /// </summary>
    private const int RestoreRetryPerTick = 3;

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

        // ===== 免疫「显示桌面」：摘样式 + 还原，且必须"摘 → 还原 → 再摘" =====
        //
        // Win+D / 任务栏右键 / 右下角细条会把所有普通顶层窗口收走，本窗口没有任务栏按钮，
        // 被收走后用户无法找回。两道防线交替上：
        //
        //  ① 摘掉 WS_MINIMIZEBOX / WS_SYSMENU，让批量最小化从源头跳过本窗口；
        //  ② 万一还是被带走（最小化 或 WS_VISIBLE 被清零），无激活还原。
        //
        // 关键在于 ① 要做两次：实测（app-error.log 的 ShowDesktopWatchdog 记录）发现，
        // 还原用的 SW_RESTORE 会连带<b>重建非客户区</b>，把刚摘掉的 WS_MINIMIZEBOX 又写回来 ——
        // 于是下一轮 tick 里窗口又是"可最小化"的，系统随时能在还原的那条缝里再收一次，
        // 表现为"窗口一直反复消失"。所以还原之后必须立刻再摘一遍，把状态收敛干净。
        //
        // 这两步都不能只在启动时做一次：宿主在激活、改尺寸、DPI 变化、从托盘 Show() 等时机
        // 都会把样式写回去，所以必须周期性重放。托盘「隐藏」会先开 SuppressAutoRestore 闸门，
        // 不会被这里的还原立刻拉回来。
        // 看门狗每 tick 的体检。窗口"消失"却查不到原因时，这条是唯一的现场证据：
        // 它记下句柄来源（Avalonia 还是 Win32 枚举兜底）和窗口的真实形态。
        // 限流 3 秒一条，避免高频 tick 把日志刷爆。
        LogWatchdogHealth(hwnd);

        DesktopEmbedService.RemoveCaptionButtons(hwnd);
        DesktopEmbedService.HideFromTaskbar(hwnd);

        // 第三道（也是唯一真正闭环的）防线：把最小化消息在窗口过程里直接吃掉。
        //
        // 前两道 —— 摘 WS_MINIMIZEBOX + 事后还原 —— 都只能"减少被收走的概率"或
        // "事后补救"，而「显示桌面」的施压是连续的，补救永远慢半拍（实测最坏 200ms 窗口期，
        // 用户看到的就是闪一下没了）。装钩子之后窗口从未进入最小化态，问题从根上消失。
        //
        // 句柄会变（Avalonia 重建原生窗口），所以每 tick 调一次；方法内部幂等，句柄没变时
        // 只做一次句柄比较。
        DesktopEmbedService.GuardAgainstMinimize(hwnd);

        if (DesktopEmbedService.RestoreIfMinimized(hwnd))
        {
            // 真的发生了还原才记一条。这是「显示桌面把窗口带走了」的现场证据：
            // 日志里这条记录的数量和时刻就是还原被触发的铁证；
            // 若日志里一条都没有却仍然"窗口消失"，说明窗口是被本类判定之外的
            // 途径收走的，需要重新审视 IsIconic / IsWindowVisible 这对判据。
            DesktopEmbedService.LogRestoreEvent(hwnd, "before-restrip");

            // SW_RESTORE 重建非客户区时会把最小化样式写回来，这里再摘一次压住它。
            DesktopEmbedService.RemoveCaptionButtons(hwnd);

            // 还原指令发出去了，但「显示桌面」可能还在持续施压，窗口此刻未必真的可见。
            // 复查一次，没落地就立刻原 tick 重试 —— 否则要等 200ms 后的下一轮 tick，
            // 用户感知就是"窗口消失了一会儿才回来"。
            for (var retry = 0; retry < RestoreRetryPerTick; retry++)
            {
                if (!DesktopEmbedService.IsHiddenOrMinimized(hwnd))
                {
                    break;
                }

                DesktopEmbedService.RestoreIfMinimized(hwnd);
                DesktopEmbedService.RemoveCaptionButtons(hwnd);
            }
        }

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

    /// <summary>
    /// 隐藏到托盘。必须走这个代理而不是直接 <c>Hide()</c>：
    /// 看门狗会把「非最小化但不可见」的窗口当成被「显示桌面」收走了，随即无激活还原 ——
    /// 我们主动隐藏的窗口就会立刻自己弹回来，表现为"隐藏不掉"。
    /// 这里在 Hide 前后临时打开自动还原闸门，操作完成再放开。
    /// </summary>
    private void HideToTray()
    {
        DesktopEmbedService.SuppressAutoRestore = true;
        try
        {
            Hide();
        }
        finally
        {
            DesktopEmbedService.SuppressAutoRestore = false;
        }
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

        // 记录「现在开着的设置面板」：更新流程弹的窗要挂到它名下，否则会被它挡住。
        _settingsWindow = dialog;
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_settingsWindow, dialog))
            {
                _settingsWindow = null;
            }
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

    /// <summary>
    /// 更新流程里各提示窗的属主：设置面板开着就用设置面板，否则用主窗体。
    ///
    /// 设置面板在「嵌入桌面」模式下是非模态浮窗、且会盖在置底的主窗体之上；
    /// 从设置面板里点「检查更新」时，若把结果窗挂到主窗体名下，
    /// 窗口层级就落在设置面板**下面**——用户只看到设置面板，结果窗被挡得点不到。
    /// （下载进度窗自带 Topmost 不受影响，但结果窗/询问窗是模态，必须换属主。）
    /// </summary>
    private Window ResolveDialogOwner() =>
        _settingsWindow is { IsVisible: true } settings ? settings : this;

    private void OnConfigApplied(AppConfig config)
    {
        // 端口/开关可能变了：重启后台服务；嵌入/锁定按新配置尽力重应用（完全切换嵌入态需重启）。
        RestartServices();

        if (OperatingSystem.IsWindows())
        {
            CanResize = !config.LockWindow;
        }

        // 设置窗里也可能改了这一项，顶栏按钮文案 / 颜色跟着同步。
        UpdateLockButton();
        UpdateAiButton();

        ApplyDesktopEmbed();

        // 看门狗轮询频率统一 200ms（「显示桌面」路径前台事件不可靠，必须靠轮询保体感，
        // 见 StartChromeWatchdog 注释）。运行中切换嵌入开关不改变它。
        if (OperatingSystem.IsWindows() && _embedWatchdog is not null)
        {
            _embedWatchdog.Interval = TimeSpan.FromMilliseconds(200);
        }

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

    private bool _isExiting;

    private async void ExitApplication()
    {
        // 托盘「退出」可能被连点 / 重复触发，这里只放行一次，避免并发走两遍 Shutdown。
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;

        try
        {
            _saveTimer?.Stop();
            _allowClose = true;

            // 记住当前视图的窗口位置/尺寸（会 MarkDirty，确保随后落盘）。
            SaveCurrentViewBounds();

            // 退出前兜底落盘：防抖定时器可能还没轮到，这里确保脏数据不丢。
            if (_viewModel?.IsDirty == true)
            {
                await SaveAsync();
            }

            DisposeServices();

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                lifetime.Shutdown();
            }
        }
        catch (Exception ex)
        {
            // async void 的异常会直接终止进程，退出路径必须整体兜底，绝不能崩在退出瞬间。
            AppLog.Error(ex, "MainWindow.ExitApplication");
        }
    }

    /// <summary>把当前数据写回磁盘并清除脏标记；落盘失败不致命（保留 IsDirty 以便重试）。</summary>
    private async System.Threading.Tasks.Task SaveAsync()
    {
        if (_viewModel is null || _suppressAutoSave)
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

    /// <summary>
    /// 订阅 <see cref="MainViewModel.IsDirty"/> 的 PropertyChanged，脏了才启动 2 秒一次性落盘防抖。
    /// 连续改动会不断顺延定时器，不会高频写盘；干净状态下零定时器唤醒。
    /// </summary>
    private void SubscribeAutoSave()
    {
        var viewModel = _viewModel;
        if (viewModel is null)
        {
            return;
        }

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.IsDirty) || viewModel.IsDirty != true)
            {
                return;
            }

            // MarkDirty 可能在任意线程触发，DispatcherTimer 只能 UI 线程操作，统一 Post 回 UI 线程。
            Dispatcher.UIThread.Post(() =>
            {
                if (_saveTimer is null)
                {
                    return;
                }

                _saveTimer.Stop();
                _saveTimer.Start();
            });
        };
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
                // 启动瞬间提到 High 抢首屏，15 秒后自动回落到 AboveNormal 常驻 ——
                // 既保留「高优先级」诉求，又不让桌面挂件长期跟前台应用抢时间片。
                HighPriorityStartupService.ApplyProcessPriorityWithFallback(true);

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
        if (!_apiServer.Start(_config.ApiPort))
        {
            // 端口被占用 / 前缀注册失败时 Start 返回 false：必须让用户知道，否则以为 API 开着其实没开。
            AppLog.Error(null, $"HTTP API 启动失败：端口 {_config.ApiPort} 可能被占用。");
        }
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
            OnReviewTaskStatusChanged,
            () => _config,
            CreateHostActions());
        if (!_mcpServer.Start(_config.McpPort))
        {
            AppLog.Error(null, $"MCP 服务启动失败：端口 {_config.McpPort} 可能被占用。");
        }
    }

    /// <summary>
    /// 宿主能力桥：把「发送 / 预览报告」「立即备份」包成工具能力，供 MCP 与 AI 面板共用
    /// （AI 面板内部另建了一个不监听端口的工具服务，同样需要这份能力）。
    /// 委托是调用时才求值的，所以可以先建桥、后创建服务实例。
    /// </summary>
    private McpHostActions CreateHostActions() => new()
    {
        SendReportAsync = () => _reportService!.RunOnceAsync(),
        PreviewReport = () => _reportService!.Preview(),
        RunBackupAsync = async () => McpHostActions.DescribeBackup(await _backupService!.RunBackupAsync())
    };

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
                $"要现在下载更新吗？安装包约 {ByteText.FormatSize(result.Asset.SizeBytes)}，" +
                "会在后台下载并显示实时进度，中途可以取消。\n" +
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
            // 带进度条 + 失败原地重试；用户点「取消」或关掉进度窗时返回 null。
            var installerPath = await DownloadUpdateWithProgressAsync(service, result.Asset, latestText);
            if (installerPath is null)
            {
                return "已取消更新下载";
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
    /// 带进度条、可失败重试的下载。返回下载好的安装包路径；用户取消 / 关掉进度窗时返回 null。
    ///
    /// 更新包 50MB 上下，干等没有任何反馈是用户明确抱怨过的点，所以这里补齐三件事：
    /// 实时进度（百分比 + 已下载 / 总大小）、失败原地重试（不用再走一遍「检查更新」）、
    /// 以及一个屏幕居中置顶的非模态浮窗（设置面板开着时也不会被压到后面点不动）。
    /// </summary>
    private async Task<string?> DownloadUpdateWithProgressAsync(
        UpdateService service,
        UpdateAsset asset,
        string versionText)
    {
        var window = new UpdateProgressWindow(versionText, asset.SizeBytes);
        // 令牌先取出来：循环里每轮都要用，不必依赖窗口对象还活着
        var token = window.Token;
        window.Show();

        try
        {
            while (true)
            {
                try
                {
                    var path = await service.DownloadAsync(asset, window.Progress, token);
                    window.ShowCompleted();

                    // 让 100% 先画出来再关窗，否则进度条来不及出现就消失了
                    await Task.Delay(250);
                    return path;
                }
                catch (OperationCanceledException)
                {
                    // 用户取消 / 关窗：不算失败，安静退出
                    return null;
                }
                catch (Exception ex)
                {
                    AppLog.Error(ex, "MainWindow.UpdateDownload");

                    // 失败停在原地等用户选「重试」还是「关闭」，重试就是再下一遍，不重头检查版本
                    window.ShowFailed(ex.Message);
                    if (!await window.WaitForRetryAsync())
                    {
                        return null;
                    }
                }
            }
        }
        finally
        {
            // 用户自己关掉的窗不要再关一遍
            if (window.IsVisible)
            {
                window.Close();
            }
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

        // 和 ShowMessageAsync 一样打上"有模态框开着"：否则这框一抢焦点，
        // 「焦点离开表单就提交草稿」的逻辑会当场把行内输入收掉。
        _modalDialogOpen = true;
        try
        {
            // 属主取当前最上层的那个窗（设置面板开着时就是它）：
            // 挂主窗体名下会被非模态的设置面板挡住，用户看到的就是"点了检查更新没反应"。
            await win.ShowDialog(ResolveDialogOwner());
        }
        finally
        {
            _modalDialogOpen = false;
        }

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
            // 同 ConfirmAsync：设置面板会压在置底的主窗体上，不置顶就会被挡住点不到
            Topmost = true,
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
            // 同 AskUpdateAsync：属主随当前最上层窗口走，避免被设置面板挡住
            await win.ShowDialog(ResolveDialogOwner());
        }
        finally
        {
            _modalDialogOpen = false;
        }
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

                if (_viewModel is not null)
                {
                    _viewModel.RebuildCalendar();
                    _viewModel.MarkDirty();
                    await SaveAsync();
                }
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "OnDataChangedFromApi");
            }
            finally
            {
                // 复位必须放 finally：RebuildCalendar 一旦抛异常，标志位会永久卡 true，
                // 之后所有 API 刷新都会被静默跳过（数据改了 UI 却不更新）。
                lock (_apiRefreshLock)
                {
                    _apiRefreshPending = false;
                }
            }
        });
    }

    private void DisposeServices()
    {
        _gcTrimTimer?.Dispose();
        _gcTrimTimer = null;
        _aiDismissHook.Dispose();

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
    private void TaskView_Click(object? sender, RoutedEventArgs e) => SwitchView(CalendarViewMode.Tasks);
    private void Settings_Click(object? sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>点击 AI 按钮：切换右上角迷你对话覆盖层的显示状态。</summary>
    private void AiChat_Click(object? sender, RoutedEventArgs e) => SetAiPanelOpen(!AiPanel.IsVisible);

    /// <summary>点击透明背板（面板外的任意位置）：收起 AI 对话面板，并吃掉本次点击避免触发窗口拖动。</summary>
    private void AiBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
        SetAiPanelOpen(false);
    }

    /// <summary>
    /// 显示/隐藏 AI 对话覆盖层。打开时把键盘焦点落进输入框 —— 覆盖层在主窗口内，
    /// 普通控件焦点正常，点开即可直接打字。
    /// </summary>
    private void SetAiPanelOpen(bool open)
    {
        if (AiPanel is null)
        {
            return;
        }

        AiPanel.IsVisible = open;
        if (AiBackdrop is not null)
        {
            AiBackdrop.IsVisible = open;
        }

        if (open)
        {
            // 嵌入桌面下窗口带 WS_EX_NOACTIVATE，键盘消息根本送不进来，光 Focus() 打不进字；
            // 必须先 BeginTextEntry 放开激活，再聚焦（与 FocusInlineTextBox 同一套流程，双次兜底）。
            void TryFocus()
            {
                BeginTextEntry();
                AiPanel.FocusInput();
            }

            Dispatcher.UIThread.Post(TryFocus, DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(TryFocus, DispatcherPriority.Background);

            // 装全局鼠标钩子：点窗口外的桌面 / 其他应用也能关掉面板
            //（窗口内的点击由 AiBackdrop 负责，钩子负责背板够不到的外部）。
            _aiDismissHook.Install(OnAiOutsideClick);
        }
        else
        {
            _aiDismissHook.Uninstall();
        }
    }

    /// <summary>钩子回调：按下的位置在 AI 面板和 AI 按钮之外（含程序外的桌面）就收起面板。</summary>
    private void OnAiOutsideClick(Avalonia.PixelPoint screen)
    {
        // 点在面板内 = 正常交互；点在 AI 按钮上 = 按钮自己的 Click 负责切换，钩子都不插手。
        if (IsScreenPointInside(AiPanel, screen) || IsScreenPointInside(AiButton, screen))
        {
            return;
        }

        SetAiPanelOpen(false);
    }

    /// <summary>屏幕像素坐标是否落在某控件当前占据的矩形内。</summary>
    private static bool IsScreenPointInside(Visual? visual, Avalonia.PixelPoint screen)
    {
        if (visual is null || !visual.IsVisible)
        {
            return false;
        }

        try
        {
            var topLeft = visual.PointToScreen(new Avalonia.Point(0, 0));
            var bottomRight = visual.PointToScreen(new Avalonia.Point(visual.Bounds.Width, visual.Bounds.Height));
            return screen.X >= topLeft.X && screen.X <= bottomRight.X
                && screen.Y >= topLeft.Y && screen.Y <= bottomRight.Y;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>打开数据统计小面板（本周 / 本月完成情况）。</summary>
    private async void Statistics_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            var dialog = new StatisticsWindow();
            dialog.Initialize(_viewModel.Data, _syncRoot);
            await dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "MainWindow.Statistics");
        }
    }

    /// <summary>
    /// 打开「周期任务管理」窗口：列出所有周期任务系列，支持添加 / 编辑 / 删除单个 / 删除全部。
    /// </summary>
    private async void RecurringTask_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            var manager = new RecurringManagerWindow();
            manager.Initialize(_viewModel);
            await manager.ShowDialog(this);
            await SaveAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "MainWindow.RecurringTask");
        }
    }

    /// <summary>
    /// 视图下拉打开前：把当前所在视图的那一项加粗，菜单里不用再靠勾选标记也能看出当前选择。
    /// </summary>
    private void ViewMenu_Opening(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        // 窄屏时实际显示的是任务面板，所以高亮也要落在「任务视图」上。
        UpdateViewMenuSelection(_viewModel.IsNarrowTaskOnly
            ? CalendarViewMode.Tasks
            : _viewModel.Settings.ViewMode);
    }

    /// <summary>
    /// 把视图下拉里的当前项加粗高亮。抽出来是因为窄屏切换也要刷它 ——
    /// 只在菜单弹出时更新的话，用户缩窗口后一打开菜单会看到"没有任何一项被选中"。
    /// </summary>
    private void UpdateViewMenuSelection(CalendarViewMode mode)
    {
        if (ViewMonthItem is null || ViewWeekItem is null
            || ViewYearItem is null || ViewTaskItem is null)
        {
            return;
        }

        ViewMonthItem.FontWeight = mode == CalendarViewMode.Month ? FontWeight.Bold : FontWeight.Normal;
        ViewWeekItem.FontWeight = mode == CalendarViewMode.Week ? FontWeight.Bold : FontWeight.Normal;
        ViewYearItem.FontWeight = mode == CalendarViewMode.Year ? FontWeight.Bold : FontWeight.Normal;
        ViewTaskItem.FontWeight = mode == CalendarViewMode.Tasks ? FontWeight.Bold : FontWeight.Normal;
    }

    // ===== 顶栏「锁定位置」快捷开关（与设置里的「锁定位置，禁止拖动或缩放」是同一项）=====

    /// <summary>
    /// 顶栏快捷切换锁定：锁住后窗口既不能拖动也不能拖边缩放（拖拽 / 缩放热区都先看
    /// <see cref="AppConfig.LockWindow"/>）。改完立即落盘，省得重启后状态丢失。
    /// </summary>
    private void LockPosition_Click(object? sender, RoutedEventArgs e)
    {
        _config.LockWindow = !_config.LockWindow;

        if (OperatingSystem.IsWindows())
        {
            CanResize = !_config.LockWindow;
        }

        UpdateLockButton();
        _ = _configStore.SaveAsync(_config);
    }

    /// <summary>
    /// 按当前锁定状态刷新顶栏图标 / 颜色 / 悬浮提示。按钮不显示文字，只用锁形矢量图标：
    /// 未锁 = 打开的锁钩；已锁 = 闭合锁并随 .locked 态变红。
    /// 路径取自 Material Design 图标（lock / lock_open，24×24 网格），用 Shapes.Path 承载并
    /// 设 Stretch=Uniform 缩到 13px；PathIcon 不支持 Stretch，按 24 原始尺寸画会溢出小按钮。
    /// </summary>
    private static readonly Geometry LockedIconGeometry = Geometry.Parse(
        "M18 8h-1V6c0-2.76-2.24-5-5-5S7 3.24 7 6v2H6c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V10c0-1.1-.9-2-2-2zm-6 9c-1.1 0-2-.9-2-2s.9-2 2-2 2 .9 2 2-.9 2-2 2zm3.1-9H8.9V6c0-1.71 1.39-3.1 3.1-3.1 1.71 0 3.1 1.39 3.1 3.1v2z");

    private static readonly Geometry UnlockedIconGeometry = Geometry.Parse(
        "M12 17c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zm6-9h-1V6c0-2.76-2.24-5-5-5S7 3.24 7 6h1.9c0-1.71 1.39-3.1 3.1-3.1 1.71 0 3.1 1.39 3.1 3.1v2H6c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2h12c1.1 0 2-.9 2-2V10c0-1.1-.9-2-2-2zm0 12H6V10h12v10z");

    private void UpdateLockButton()
    {
        if (LockButton is null)
        {
            return;
        }

        var locked = _config.LockWindow;
        LockIcon.Data = locked ? LockedIconGeometry : UnlockedIconGeometry;
        LockButton.Classes.Set("locked", locked);
        ToolTip.SetTip(
            LockButton,
            locked
                ? "当前已锁定：窗口不可拖动、不可缩放。点击解除锁定。"
                : "锁定位置：禁止拖动窗口或缩放窗口。");
    }

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
        PlayContentFadeIn();

        // 切到周视图时把左栏滚回开头：默认展示的必须是「最近 7 天」，
        // 而不是上次翻到几周之后的残留位置。
        if (mode == CalendarViewMode.Week)
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    UpdateWeekCellSize();
                    if (WeekScrollViewer is not null)
                    {
                        // 走带闸门的版本：切视图后的这次归位同样不该触发尾部追加。
                        ApplyWeekOffset(0);
                    }
                },
                DispatcherPriority.Loaded);
        }
        else if (mode == CalendarViewMode.Year)
        {
            // 切到年视图时月卡才第一次真正布局；Loaded 优先级等布局完成后
            // 再按实际列宽回填日期字号（初始是默认值 11，小窗口下会偏大裁字）。
            Dispatcher.UIThread.Post(UpdateYearFontScale, DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// 视图切换后对当前可见的主内容做一次 0.62 → 1 的快速淡入，
    /// 让日历格子 / 任务面板的硬切变得柔和；宿主上挂了 DoubleTransition，这里只改值。
    /// </summary>
    private void PlayContentFadeIn()
    {
        var narrow = Bounds.Width > 0 && Bounds.Width <= NarrowLayoutThreshold
                     && _viewModel?.Settings.ViewMode != CalendarViewMode.Week;
        var host = narrow ? (InputElement?)NarrowTaskOnlyView : NormalViewHost;
        if (host is null)
        {
            return;
        }

        host.Opacity = 0.62;
        // 先让低透明度落一帧，再在下一布局周期恢复，过渡才会真正跑起来
        Dispatcher.UIThread.Post(
            () => host.Opacity = 1,
            DispatcherPriority.Background);
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
        // 启动路径：连上次存下的位置一起恢复（不是 GetBoundsForView —— 那个位置不动）。
        ApplyWindowBounds(GetStartupBounds(s.ViewMode));
        UpdateLockButton();
        UpdateAiButton();
    }

    /// <summary>AI 顶栏按钮只在「启用 AI 助手」时显示；关闭时同时收起可能还开着的对话面板。</summary>
    private void UpdateAiButton()
    {
        if (AiButton is null)
        {
            return;
        }

        AiButton.IsVisible = _config.AiEnabled;
        if (!_config.AiEnabled)
        {
            SetAiPanelOpen(false);
        }
    }

    /// <summary>
    /// 用 <see cref="ThemeCatalog.All"/> 填充主题下拉框。
    ///
    /// <para>以前这份清单是手写在 XAML 里的 ComboBoxItem，和调色板里的 switch 各存一份 ——
    /// 加主题时很容易只改一处。现在改成从 Core 的清单生成，两边不可能再不一致
    /// （枚举里没有任何一个主题会"漏在下拉列表外"）。</para>
    /// </summary>
    private void EnsureBackgroundModeItems()
    {
        if (BackgroundModeBox.ItemCount > 0)
        {
            return;
        }

        foreach (var def in ThemeCatalog.All)
        {
            BackgroundModeBox.Items.Add(new ComboBoxItem { Content = def.Name, Tag = def.Mode });
        }
    }

    private void SelectBackgroundMode(CalendarBackgroundMode mode)
    {
        EnsureBackgroundModeItems();

        // ClearBorder 等历史值由 ThemeCatalog 归一到 None，所以按"归一后的枚举"匹配即可。
        var target = ThemeCatalog.Get(mode).Mode;
        foreach (var item in BackgroundModeBox.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is CalendarBackgroundMode m && m == target)
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

        if (BackgroundModeBox.SelectedItem is not ComboBoxItem { Tag: CalendarBackgroundMode mode })
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
        _viewModel.MarkDirty();

        // 拖滑块 / 滚轮每秒会触发几十次 ValueChanged。这里只走轻量的「外壳着色」，
        // 绝不碰材质协商和整窗调色板（模式没变，重建约 30 个画刷 + 换肤会把
        //  透明分层窗口的合成线程堵死 —— 表现就是拖动一卡一卡、松手才变色）。
        // 再做一层 leading + trailing 节流：首帧立即跟手，之后每 30ms 最多重绘一次。
        if (!_opacityTintPending)
        {
            ApplyShellTint(CurrentBackgroundMode);
            _opacityTintPending = true;
        }

        _opacityTintTimer.Stop();
        _opacityTintTimer.Start();
    }

    /// <summary>
    /// 透明度调节节流定时器：每次 ValueChanged 都 Stop/Start 一次（trailing edge），
    /// 停下 30ms 后把最后一帧补齐。30ms ≈ 每帧 33ms 的显示节奏，既跟手又不会把
    /// 分层窗口的整窗合成堆成积压。
    /// </summary>
    private readonly DispatcherTimer _opacityTintTimer =
        new() { Interval = TimeSpan.FromMilliseconds(30) };

    private bool _opacityTintPending;

    private void OpacityTintTimer_Tick(object? sender, EventArgs e)
    {
        _opacityTintTimer.Stop();
        _opacityTintPending = false;
        ApplyShellTint(CurrentBackgroundMode);
    }

    /// <summary>
    /// 鼠标悬停在透明度滑块上滚动滚轮：上滚更不透明、下滚更透明，每格步进 0.05
    /// （全程约 20 格）；按住 Shift 时步进放大到 0.1（全程约 10 格），快速拉满/拉低用。
    /// 事件标记为已处理，避免滚轮同时把外层滚动条带着跑。
    /// </summary>
    private void OpacitySlider_Wheel(object? sender, PointerWheelEventArgs e)
    {
        if (_viewModel is null || e.Delta.Y == 0)
        {
            return;
        }

        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 0.05;
        var next = Math.Round(OpacitySlider.Value + Math.Sign(e.Delta.Y) * step, 2);
        OpacitySlider.Value = Math.Clamp(next, OpacitySlider.Minimum, OpacitySlider.Maximum);
        e.Handled = true;
    }

    /// <summary>
    /// 把背景模式映射成「外壳 Shell 上的 ARGB 着色」。窗口本身不带标题栏，由外壳画出
    /// 底色 / 22px 圆角 / 1px 细边框 —— 与 WPF 宿主（WindowStyle=None + AllowsTransparency）同一套观感，
    /// 各模式的差异只体现在这层刷子的颜色与不透明度上（见下面的 Shell.Background switch）。
    /// 窗口底层材质由 <see cref="ApplyWindowBackdrop"/> 决定。
    /// </summary>
    private void ApplyBackground()
    {
        if (_viewModel is null)
        {
            return;
        }

        var mode = _viewModel.Settings.BackgroundMode;

        // 先定窗口底层的系统材质（Win10 走亚克力，其余平台/系统走逐像素透明），
        // 再把颜色画到外壳 Shell 上。
        ApplyWindowBackdrop(mode);

        ApplyShellTint(mode);

        ApplyBackgroundResources(mode);
    }

    /// <summary>
    /// 只把当前主题 + 不透明度换算成一层底色刷到外壳 Shell 上 —— 不碰系统材质、不重建调色板。
    /// 拖透明度滑块时只调这一个：一次赋值只让 DWM 重新合成一层底色，开销远小于整窗换肤。
    /// </summary>
    private void ApplyShellTint(CalendarBackgroundMode mode)
    {
        if (_viewModel is null)
        {
            return;
        }

        // 底色画在外壳 Border 上而不是窗口上：窗口本身不带底色，圆角外沿才能透出桌面。
        //
        // ⚠️ 这里**不再对 alpha 做逐模式截断**。老版本写着 Math.Min(alpha, 210) / Math.Min(alpha, 150)，
        // 于是透明度滑杆拉到头也只有 82% 甚至 58%，用户反馈"透明度的强度不高"—— 是代码里限死的。
        // 现在滑杆 1:1 映射到 alpha，强度完全由用户决定。
        Shell.Background = ToBrush(ThemeCatalog.Build(mode, _viewModel.Settings.Opacity).Shell);
    }

    /// <summary>当前主题。透明度滑杆那一类"只重上色、不换主题"的路径用它取模式。</summary>
    private CalendarBackgroundMode CurrentBackgroundMode
        => _viewModel?.Settings.BackgroundMode ?? CalendarBackgroundMode.None;

    // 「ClearBorder 一律按 None 处理」这件事已经由 ThemeCatalog 在 Core 里统一负责
    // （Get(ClearBorder) 返回 None 的定义），宿主不再需要自己判一次。

    /// <summary>
    /// 决定窗口底层的系统材质，并同步窗口外形。
    ///
    /// Windows 10（1803 起）请求 DWM 的**亚克力**材质。原因是本程序的窗口是「无边框 + 整块透明」，
    /// 在 Windows 上这会落成一块分层窗口（WS_EX_LAYERED）：分层窗口没有 DWM 合成缓冲，每次重绘都要
    /// 把整窗重新合成一遍 —— 这就是用户反复上报的「频繁闪烁 / 有时候连续闪烁」。换成亚克力后窗口
    /// 不再是分层窗口，重绘交给 DWM 合成，闪烁随之消失，观感还是磨砂玻璃。
    ///
    /// 只在 Windows 10 上换：Win11 的 DWM 对分层窗口的处理没有问题，维持原来的逐像素透明（圆角更锐利、
    /// 透明度语义不变），Windows 之外（macOS / Linux）同样维持原样，不做无谓的行为变更。
    ///
    /// 代价：亚克力是 DWM 画在**整块窗口矩形**上的材质，圆角外沿也会被填满 —— 四角会变成"磨砂直角"。
    /// 所以亚克力模式下必须用 SetWindowRgn 把窗口外形裁成圆角矩形（见 <see cref="UpdateWindowShape"/>）。
    ///
    /// 幂等：材质没变就直接返回。本方法会被透明度滑杆、背景模式按钮高频调到，
    /// 反复重新协商窗口材质本身就是一种闪烁源。
    /// </summary>
    private void ApplyWindowBackdrop(CalendarBackgroundMode mode)
    {
        // 「无背景」的整个诉求就是**整窗透明、透出桌面**，而亚克力是 DWM 铺在整块窗口矩形上的材质，
        // 正好相反 —— Win10 上会看到一层白雾，用户反馈的「无背景的主题界面还是白色的」就是这么来的。
        // 所以这个模式下不请求亚克力，回到逐像素透明（圆角由外壳自己表达，不需要 SetWindowRgn 裁剪）。
        var useAcrylic = WindowBackdropService.ShouldUseAcrylic && !ThemeCatalog.IsTransparent(mode);
        if (_acrylicBackdrop == useAcrylic)
        {
            return;
        }

        _acrylicBackdrop = useAcrylic;

        // 按优先级给材质名：优先亚克力，取不到就退回逐像素透明（本程序原本的行为），
        // 保证在任何系统上窗口都还能正常显示。ActualTransparencyLevel 会报告实际拿到了哪个。
        TransparencyLevelHint = useAcrylic
            ? new[] { WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent }
            : new[] { WindowTransparencyLevel.Transparent };

        // 材质换了，上次算出来的尺寸不再代表当前外形，置零让 UpdateWindowShape 重新裁一次。
        // （_windowShapeIsRounded 不能在这里改：它是「窗口上现在有没有裁剪」的事实记录，
        //  从亚克力切回透明时要靠它去 ClearRegion。）
        _windowShapeWidth = 0;
        _windowShapeHeight = 0;
        UpdateWindowShape();
    }

    /// <summary>
    /// 亚克力模式下把窗口外形裁成圆角矩形（像素单位）。
    ///
    /// 只在「材质切换」或「窗口像素尺寸变化」时真正重裁：ApplyBackground 会被透明度滑杆、
    /// 背景模式按钮高频调用，无条件调 SetWindowRgn 会让滑杆本身变成新的闪烁源。
    /// 但尺寸变化必须重裁 —— 否则窗口放大后，旧的裁剪区域会把新长出来的部分整块切掉。
    /// </summary>
    private void UpdateWindowShape()
    {
        UpdateWindowShape(Bounds.Width, Bounds.Height);
    }

    private void UpdateWindowShape(double widthDip, double heightDip)
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

        if (_acrylicBackdrop != true)
        {
            // 逐像素透明：圆角由外壳自己表达，窗口不需要裁剪。切回这个模式时必须把裁剪清掉，
            // 否则会沿用亚克力时期留下的区域。
            if (_windowShapeIsRounded)
            {
                WindowBackdropService.ClearRegion(hwnd);
                _windowShapeIsRounded = false;
            }

            _windowShapeWidth = 0;
            _windowShapeHeight = 0;
            return;
        }

        var scaling = RenderScaling > 0 ? RenderScaling : 1.0;
        var width = (int)Math.Round(widthDip * scaling);
        var height = (int)Math.Round(heightDip * scaling);
        if (width < 1 || height < 1)
        {
            return;
        }

        if (_windowShapeIsRounded && _windowShapeWidth == width && _windowShapeHeight == height)
        {
            return;
        }

        _windowShapeIsRounded = true;
        _windowShapeWidth = width;
        _windowShapeHeight = height;
        WindowBackdropService.ApplyRoundedRegion(hwnd, width, height, ShellCornerRadius * scaling);
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
        var dark = ThemeCatalog.IsDark(mode);
        var c = ThemeCatalog.Build(mode, _viewModel?.Settings.Opacity ?? 1.0);

        // 深色模式同时把 FluentTheme 的控件主题切到 Dark：否则下拉框 / 滑杆 / 输入框 / 勾选框
        // 会在深色底上露出一排浅色控件。浅色模式显式钉死 Light，不再跟随系统主题。
        RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;

        // 全部颜色来自 ThemeCatalog —— 这里只做「角色 → XAML 资源键」的搬运，不再有任何手调数值。
        // 好处：① 两个宿主不可能再各调各的；② 加主题只需在 Core 里加一行定义；
        //       ③ 对比度 / 主题区分度可以被单测锁住。
        SetBrush("PrimaryTextBrush", ToBrush(c.PrimaryText));
        SetBrush("MutedTextBrush", ToBrush(c.MutedText));
        SetBrush("CellBorderBrush", ToBrush(c.CellBorder));
        SetBrush("TaskBorderBrush", ToBrush(c.TaskBorder));
        SetBrush("DayCellBackgroundBrush", ToBrush(c.DayCell));
        SetBrush("DayCellOutMonthBackgroundBrush", ToBrush(c.DayCellOutMonth));
        SetBrush("YearMonthBackgroundBrush", ToBrush(c.YearMonth));
        SetBrush("TodayCellBackgroundBrush", ToBrush(c.TodayCell));
        SetBrush("TodayCellBorderBrush", ToBrush(c.TodayCellBorder));
        SetBrush("SelectedCellBackgroundBrush", ToBrush(c.SelectedCell));
        SetBrush("SelectedCellBorderBrush", ToBrush(c.SelectedCellBorder));
        SetBrush("TaskBackgroundBrush", ToBrush(c.TaskPill));
        SetBrush("TodayPanelBackgroundBrush", ToBrush(c.Panel));
        SetBrush("TodayPanelBorderBrush", ToBrush(c.PanelBorder));
        SetBrush("WeekGroupBackgroundBrush", ToBrush(c.WeekGroup));
        SetBrush("WeekGroupHoverBrush", ToBrush(c.WeekGroupHover));
        SetBrush("WeekTaskRowHoverBrush", ToBrush(c.WeekRowHover));
        SetBrush("ImportantTaskBackgroundBrush", ToBrush(c.ImportantBg));
        SetBrush("ImportantTaskBorderBrush", ToBrush(c.ImportantBorder));
        SetBrush("ImportantTaskTextBrush", ToBrush(c.ImportantText));
        SetBrush("HolidayBreakBrush", ToBrush(c.HolidayBreak));
        SetBrush("HolidayWorkBrush", ToBrush(c.HolidayWork));
        SetBrush("HolidayTextBrush", ToBrush(c.HolidayText));
        SetBrush("HolidayWorkTextBrush", ToBrush(c.HolidayWorkText));
        SetBrush("WeekDoneCheckBrush", ToBrush(c.DoneCheck));
        SetBrush("WindowEdgeBrush", ToBrush(c.WindowEdge));
        SetBrush("ToolbarControlBackgroundBrush", ToBrush(c.ToolbarBackground));
        SetBrush("ToolbarControlBorderBrush", ToBrush(c.ToolbarBorder));
        SetBrush("ToolbarControlHoverBrush", ToBrush(c.ToolbarHover));
        SetBrush("ToolbarControlPressedBrush", ToBrush(c.ToolbarPressed));
        SetBrush("RecurringBadgeBrush", ToBrush(c.RecurringBadge));

        ApplyTextureLayer(c.TextureOpacity);
    }

    /// <summary>
    /// 纹理图案是**常量**，全进程只生成一次 —— 换肤/拖透明度滑杆都只是改这个画刷的 Opacity，
    /// 不重建位图（拖滑杆时每帧重建一张 bitmap 会把拖拽拖卡）。
    /// </summary>
    private static IBrush? _paperTextureBrush;

    /// <summary>
    /// 按主题的纹理浓度铺/收纹理层。<paramref name="textureOpacity"/> 已经在
    /// <see cref="ThemeCatalog.Build"/> 里乘过用户透明度了，这里直接用。
    /// </summary>
    private void ApplyTextureLayer(double textureOpacity)
    {
        if (textureOpacity <= 0)
        {
            // 非纹理主题：整层关掉 —— 不是设成全透明，而是既不画也不参与布局，
            // 免得给绝大多数主题白添一层全屏元素。
            TextureLayer.IsVisible = false;
            TextureLayer.Fill = null;
            return;
        }

        _paperTextureBrush ??= BuildPaperTextureBrush();
        TextureLayer.Fill = _paperTextureBrush;
        TextureLayer.Opacity = Math.Clamp(textureOpacity, 0, 1);
        TextureLayer.IsVisible = true;
    }

    private static IBrush BuildPaperTextureBrush()
    {
        var source = PaperTexture.Create();
        var bytes = new byte[source.Length * 4];

        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            var o = i * 4;
            // Bgra8888 + Premul：必须**自己预乘 alpha**。直接写原始 RGB 的话，
            // 半透明颗粒会被当作"亮色"叠加，整片纹理反而比底色更亮（与参考图相反）。
            bytes[o + 0] = (byte)(c.B * c.A / 255);
            bytes[o + 1] = (byte)(c.G * c.A / 255);
            bytes[o + 2] = (byte)(c.R * c.A / 255);
            bytes[o + 3] = c.A;
        }

        var bitmap = new WriteableBitmap(
            new PixelSize(PaperTexture.Size, PaperTexture.Size),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var locked = bitmap.Lock())
        {
            Marshal.Copy(bytes, 0, locked.Address, bytes.Length);
        }

        return new ImageBrush(bitmap)
        {
            TileMode = TileMode.Tile,
            Stretch = Stretch.None,
            // 平铺单元写死成图块的**像素尺寸**。不显式给的话，TileBrush 会把一格拉伸到
            // 目标控件大小 —— 整窗只会出现一颗被放大成屏幕的噪点。
            DestinationRect = new RelativeRect(
                0, 0, PaperTexture.Size, PaperTexture.Size, RelativeUnit.Absolute)
        };
    }

    private void SetBrush(string key, IBrush brush) => Resources[key] = brush;

    /// <summary>把 Core 的颜色转成 Avalonia 画刷。全透明直接复用静态实例，省一次分配。</summary>
    private static IBrush ToBrush(RgbaColor c)
        => c.A == 0 ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));

    /// <summary>
    /// 周视图窗口的高度下限。
    ///
    /// 周视图是竖排的：顶部 7 个日期格子 + 下方任务面板。窗口太矮的话面板只剩一条缝，
    /// 看起来就像"任务面板没了"。所以切到周视图时给个下限，装不下就自动长高 ——
    /// 用户之后照旧可以拖右下角随便改，改完会被记下来。
    /// </summary>
    private const double WeekMinHeight = 500;

    /// <summary>切视图时使用的默认窗口尺寸（宽 × 高）。取值偏保守，小屏上也放得下。</summary>
    private const double DefaultWindowWidth = 900;
    private const double DefaultWindowHeight = 620;

    /// <summary>
    /// 视图切换时用的边界：**位置保持不变、只换尺寸**。
    ///
    /// 切视图时统一回到「该视图的默认尺寸」，不再沿用上次自己拖出来的尺寸：
    /// 记忆的尺寸可能已经小到放不下这个视图（周视图本身就能拖得很窄，最容易中招），
    /// 切过去会显示不全、甚至被窄窗规则强制成任务面板，表现就是"点了没反应"。
    /// 位置保持不动、只换尺寸，免得窗口在屏幕上乱跳。
    /// </summary>
    private WindowBounds GetBoundsForView(CalendarViewMode mode)
    {
        if (_viewModel is null)
        {
            return new WindowBounds(Position.X, Position.Y, Width, Height);
        }

        var height = mode == CalendarViewMode.Week ? WeekMinHeight : DefaultWindowHeight;

        return new WindowBounds(Position.X, Position.Y, DefaultWindowWidth, height);
    }

    /// <summary>
    /// 「启动恢复」用的边界：把上次退出时存下的**位置 + 尺寸**整套取回来。
    ///
    /// 与 <see cref="GetBoundsForView"/>（切视图用，位置不动）的区别就在这里 ——
    /// 启动时必须连位置一起恢复，否则每次开机都回到默认坐标，用户反馈的
    /// 「重启后记不住之前所在的位置」就是这么来的。
    ///
    /// 取值规则本身在 <see cref="WindowBoundsResolver.ForStartup"/>（与 WPF 宿主共用、有单测覆盖），
    /// 这里只负责提供"兜底边界"：当前位置 + 该视图的默认尺寸。
    /// </summary>
    private WindowBounds GetStartupBounds(CalendarViewMode mode)
    {
        if (_viewModel is null)
        {
            return new WindowBounds(Position.X, Position.Y, Width, Height);
        }

        return WindowBoundsResolver.ForStartup(_viewModel.Settings, mode, GetBoundsForView(mode));
    }

    private void Window_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // ===== 最小尺寸硬夹取 =====
        //
        // Avalonia 的 Window.MinWidth / MinHeight 对本窗口无效：窗口是 WindowDecorations=None
        // 的无边框窗体，缩放走的是我们自己的 BeginResizeDrag(edge, e)，那是个原生拖拽，
        // 只认 Win32 的 WM_GETMINMAXINFO 边界，根本不会去读 MinWidth 属性 ——
        // 用户看到的就是"说好的最小宽度没生效，还能一直缩小"。
        // 原生拖拽没法拦截，只能在尺寸真变了之后立刻夹回来（同步赋值，不会闪）。
        var clampedW = Math.Max(MinWindowWidth, e.NewSize.Width);
        var clampedH = Math.Max(MinWindowHeight, e.NewSize.Height);
        if (clampedW > e.NewSize.Width + 0.5)
        {
            Width = clampedW;
        }

        if (clampedH > e.NewSize.Height + 0.5)
        {
            Height = clampedH;
        }

        // 亚克力模式下窗口外形是"裁"出来的，尺寸一变就得重裁 —— 晚了会露一帧直角或切掉新长出来的部分。
        // 这里直接用事件里的新尺寸，不读 Bounds，免得拿到还没更新的值。
        UpdateWindowShape(e.NewSize.Width, e.NewSize.Height);

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
    /// 顶栏 + 主体的自适应版式，按窗口宽度分三档降级：
    ///
    /// <list type="number">
    ///   <item>宽度够：单行展示，日期信息 + 背景/透明度 + 今天/视图/设置/锁定 全在。</item>
    ///   <item>放不下：先收起「背景 + 透明度」这组装饰设置（空间不足时它最先让位），
    ///         保证「今天 / 视图 / 设置」永远压在设置项上 —— 这是用户报的遮挡问题的正面修法。</item>
    ///   <item>还是放不下（&lt;= <see cref="TopBarWrapThreshold"/>）：换行。
    ///         第一行只留日期信息，第二行整组放「今天 / 视图 / 设置 / 锁定」——
    ///         用户明确要的就是「今天 + 周视图设置换到第二行」这个行为。</item>
    /// </list>
    ///
    /// 换行档是最后一档 —— 再窄就没有可牺牲的东西了，所以窗口本身有硬下限
    /// <see cref="MinWindowWidth"/>（= <c>Window.MinWidth</c>），拖到那儿就停住，
    /// 保证所有设置按钮任何宽度下都可见、不被遮挡。
    ///
    /// 另外窗口窄到 <see cref="NarrowLayoutThreshold"/> 时，主体只留「今日任务」这一块
    /// （今日任务 + 本周任务完成情况 + 未完成 / 已完成）。
    /// </summary>
    /// <summary>顶栏按钮的密度档，从宽到窄依次尝试（null = 样式默认档）。</summary>
    private static readonly string?[] TopBarDensitySteps = [null, "compact", "ultra"];

    /// <summary>
    /// 按密度档给顶栏所有常驻按钮加 / 去样式类（null = 恢复默认档），并同步收紧按钮间距。
    ///
    /// 这些按钮在窄窗下不隐藏 —— 窄屏恰恰就是任务视图：藏掉「设置」用户就没法改配置、
    /// 藏掉「AI」就调不出对话、藏掉「周期」就加不了周期任务。所以只能缩字号、内边距与间距。
    /// 改完类后 DesiredSize 会自动失效，紧接着的 MeasurePanelWidth 量到的就是新档位的真实宽度。
    /// </summary>
    private void SetTopBarDensity(string? densityClass)
    {
        if (TopActionPanel is null)
        {
            return;
        }

        foreach (var button in TopActionPanel.GetVisualDescendants().OfType<Button>())
        {
            button.Classes.Remove("compact");
            button.Classes.Remove("ultra");
            if (densityClass is not null)
            {
                button.Classes.Add(densityClass);
            }
        }

        // 间距跟着密度一起收：按钮变小了、间距还留着原样，既白占宽度又显得松散。
        TopActionPanel.Spacing = densityClass switch
        {
            "compact" => 4,
            "ultra" => 3,
            _ => 6
        };
    }

    private void UpdateResponsiveLayout()
    {
        if (NormalViewHost is null || NarrowTaskOnlyView is null
            || ViewControlsPanel is null
            || TopActionPanel is null || DateInfoPanel is null || TopBarGrid is null)
        {
            return;
        }

        var width = Bounds.Width;

        // 窄窗退化为「只留任务面板」—— 但周视图除外：
        // 周视图本身就是「左列日期格子 + 右侧任务面板」的窄布局，窄窗下依然可用。
        // 若把它也强制成任务面板，用户在窄窗里点「周视图」就会像"没反应"
        //（切换其实生效了，只是立刻被这条规则盖回任务面板）。
        var narrowView = width > 0 && width <= NarrowLayoutThreshold
                         && _viewModel?.Settings.ViewMode != CalendarViewMode.Week;

        // ===== 用「实际测量」而不是「拍脑袋常量」来决定档位 =====
        //
        // 之前用的是 width >= NarrowLayoutThreshold + ViewControlsWidthCost（460+262=722）这种
        // 常量判据，问题是它跟真实内容宽度对不上：字体、DPI、语言一变，或者某个按钮文字长一点，
        // 阈值就不准了 —— 用户看到的就是"该隐藏的时候不隐藏、该显示的时候已经藏了"。
        //
        // 这里改成真量：把「日期信息」和「常驻按钮」按最小自然宽度测一遍，两者相加就是单行
        // 的硬需求；装不下就先让「背景 + 透明度」这组纯装饰设置让位。测量前先复原上一次的
        // 隐藏状态，否则被隐藏的面板量出来宽度是 0，第二轮就再也放不出来了。
        ViewControlsPanel.IsVisible = true;
        if (TaskCountsPanel is not null)
        {
            TaskCountsPanel.IsVisible = true;
        }

        var available = width > 0 ? width - TopBarHorizontalChrome : double.PositiveInfinity;
        var needsWrap = false;
        if (double.IsFinite(available))
        {
            // 顶栏按钮先逐级降密度（正常 → 紧凑 → 超紧凑），取第一个能塞下的档。
            // 顺序很关键：先缩字号、最后才换行 —— 换行会白白吃掉一行高度，
            // 而这些按钮本来就只差一点点宽度。
            var dateWidth = MeasurePanelWidth(DateInfoPanel, exclude: ViewControlsPanel);
            needsWrap = true;
            foreach (var density in TopBarDensitySteps)
            {
                SetTopBarDensity(density);
                if (dateWidth + MeasurePanelWidth(TopActionPanel) <= available)
                {
                    needsWrap = false;
                    break;
                }
            }
        }

        // ===== 第 ③ 档：连「日期信息 + 常驻按钮」都摆不下 → 换行 =====
        var wrap = needsWrap;
        if (wrap)
        {
            // 常驻按钮搬到第二行（Grid.Row=1 跨满所有列），独占一行就不再和日期信息抢宽度。
            // 对齐仍然靠右：按钮组紧挨着排在第二行右端，「锁定」贴着窗口右边 ——
            // 与第一行的日期信息形成左右呼应，视觉锚点不乱。
            // （早期这里用 Stretch + 弹性列把按钮均匀铺满整行，窄窗时会出现几个大空洞，
            //   用户明确反馈过，故改为紧凑靠右。）
            Grid.SetRow(TopActionPanel, 1);
            Grid.SetColumn(TopActionPanel, 0);
            Grid.SetColumnSpan(TopActionPanel, 3);
            TopActionPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        }
        else
        {
            // 回到第一行右侧
            Grid.SetRow(TopActionPanel, 0);
            Grid.SetColumn(TopActionPanel, 1);
            Grid.SetColumnSpan(TopActionPanel, 1);
            TopActionPanel.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        }

        // 换行档下第二行会多出一段高度，外壳高度是外部算好的，这里只负责上下留白不贴边。
        TopActionPanel.Margin = wrap ? new Avalonia.Thickness(0, 3, 0, 0) : default;

        // 第二行的排布完全交给布局系统：TopActionPanel 是水平 StackPanel + 固定间距，
        // 按钮彼此紧挨、整组靠右，任何宽度下都不会出现人为的大空白。
        // 这里**不做**按像素反算间距那套 —— 手算在 SizeChanged 这一轮里量不准，会偶发对不齐。

        // ===== 第 ① / ② 档：背景 + 透明度是否还放得下 =====
        // 换行之后第一行的宽度需求降到「只有日期信息」，所以先按换行后的实际情况再量一次：
        // 只有连「日期信息 + 背景/透明度」都摆不下，才收起这两个装饰设置。
        if (double.IsFinite(available))
        {
            // 第一行净需求 = 日期信息（不含「背景+透明度」）+ 常驻按钮（换行时按钮在第二行）。
            // 再单独量这组装饰设置的真实宽度，二者相加与可用宽度比较 —— 全程真量。
            // （旧版把 ViewControlsWidthCost 常量加在已经含了 ViewControlsPanel 的测量值上，
            //   等于多算了 262px，用户得把窗口拉得特别开才看得到亮度条。）
            var firstRowNeed = wrap
                ? MeasurePanelWidth(DateInfoPanel, exclude: ViewControlsPanel)
                : MeasurePanelWidth(DateInfoPanel, exclude: ViewControlsPanel) + MeasurePanelWidth(TopActionPanel);
            var viewControlsWidth = MeasurePanelWidth(ViewControlsPanel);

            if (firstRowNeed + viewControlsWidth > available)
            {
                ViewControlsPanel.IsVisible = false;
            }
            else
            {
                // 背景/透明度保住了，再看今日 / 本周计数是否也挤得下（它比装饰设置次要）。
                var countsCost = MeasurePanelWidth(TaskCountsPanel);
                if (firstRowNeed + viewControlsWidth + countsCost > available
                    && TaskCountsPanel is not null)
                {
                    TaskCountsPanel.IsVisible = false;
                }
            }
        }

        // 顶栏常驻按钮（含视图下拉、设置、锁定）在任何宽度都保留 —— 窄屏下它们更是唯一入口，
        // 由上面的密度档缩字号来容纳，不再隐藏。

        // ===== 周视图：正方形格子的边长 =====
        // 用户要的是「7 个格子刚好铺满当前界面高度」，所以边长 = 左栏可用高度 ÷ 7，
        // 再夹到合理区间。左栏宽度与格子宽高绑同一个值，三边相等 → 正方形。
        UpdateWeekCellSize();

        // ===== 年视图：日期数字字号随月卡实际列宽缩放 =====
        UpdateYearFontScale();

        NormalViewHost.IsVisible = !narrowView;
        NarrowTaskOnlyView.IsVisible = narrowView;

        // 窄屏下主体被换成任务面板，顶栏下拉的标签也要跟着改口，
        // 否则会出现「画面是任务列表、按钮却写着周视图」这种自相矛盾的状态。
        if (_viewModel is not null && _viewModel.IsNarrowTaskOnly != narrowView)
        {
            _viewModel.IsNarrowTaskOnly = narrowView;

            // 窄屏时下拉项要跟着高亮「任务视图」，否则一打开菜单发现没有任何一项被选中。
            if (narrowView)
            {
                UpdateViewMenuSelection(CalendarViewMode.Tasks);
            }
            else
            {
                UpdateViewMenuSelection(_viewModel.Settings.ViewMode);
            }
        }
    }

    /// <summary>
    /// 计算并回填周视图格子的高度。
    ///
    /// 只算高度：宿主按「左栏可用高度 ÷ 7」求出让 7 个格子刚好铺满界面高度的值。
    /// 宽度不在这里算 —— 它固定为 <see cref="MainViewModel.WeekScrollColumnWidth"/>，
    /// 窗口缩放时左栏纹丝不动，多出来的空间全部给右侧今日任务面板。
    ///
    /// 夹取下限 44（再矮日期和任务都看不清），上限 96（超出这个高度格子会显得空荡，
    /// 用户已明确反馈"格子左右两侧空白太多"，纵向同理不该无限拉伸）。
    /// </summary>
    private void UpdateWeekCellSize()
    {
        if (_viewModel is null)
        {
            return;
        }

        var topBarHeight = TopBarBorder?.Bounds.Height ?? 0;
        var margins = WeekViewVerticalMargin;
        var bodyHeight = Bounds.Height - topBarHeight - margins;
        if (bodyHeight <= 0)
        {
            // 首帧尺寸还没准备好，先不写，等下一次 SizeChanged。
            return;
        }

        // 每个格子自带 4dp 下外边距，7 个格子共 7 份间距也要算进去，否则会溢出一点点。
        const double rowSpacing = 4.0;
        const int visibleRows = 7;
        var rowHeight = (bodyHeight - (rowSpacing * visibleRows)) / visibleRows;
        _viewModel.WeekScrollCellHeight = Math.Clamp(rowHeight, 44, 96);
    }

    /// <summary>周视图根 Grid 的上下 Margin 之和（见 MainWindow.axaml，Margin="8"）。</summary>
    private const double WeekViewVerticalMargin = 16.0;

    /// <summary>
    /// 按年视图月卡的<b>实际列宽</b>回填日期数字字号。
    ///
    /// 年视图一行 3 张月卡、每卡 7 列：窗口缩小时列宽跟着缩小，字号若不同步缩小，
    /// 两位日期数字会被右侧的节日徽标盖住或直接裁掉（用户实测"缩小之后数字看不清了"）。
    /// 这里从 YearScrollViewer 的实际宽度反推列宽，除以「基准列宽 40px」（窗口宽度
    /// 约 945px 时的实测值）得到缩放系数交给 <see cref="MainViewModel.SetYearFontScale"/>，
    /// 由它夹取与量化（0.62~1.0、步进 0.02），避免拖窗口时产生连续的通知与重排。
    /// </summary>
    private void UpdateYearFontScale()
    {
        if (_viewModel is null || YearScrollViewer is null)
        {
            return;
        }

        // YearScrollViewer → Margin 8×2 → 三列 UniformGrid；卡片间 Margin 4×2 ×3 列；
        // 卡片 Padding 6×2 + 边框 1×2；剩下的就是一列的可用宽度。
        var gridWidth = YearScrollViewer.Bounds.Width - 16 - 12; // 滚动条预留 ~12
        // 注意：这里必须写 !(x > 0) 而不是 x <= 0 —— 视图刚切换 / 首帧时 Bounds.Width 可能是 NaN，
        // 而 NaN <= 0 恒为 false，用 <= 判会把它漏过去，一路算成 NaN 的缩放系数，
        // 最终让年视图整片日期数字不渲染（只剩节日徽标）。
        if (!(gridWidth > 0))
        {
            return;
        }

        var cardOuter = (gridWidth - 8 * 3) / 3;
        var columnWidth = (cardOuter - 14) / 7;
        if (!(columnWidth > 0))
        {
            return;
        }

        _viewModel.SetYearFontScale(columnWidth / 40.0);
    }

    /// <summary>
    /// 周视图左栏滚动：滚到接近底部时追加后续日期，让用户能一直往未来翻。
    ///
    /// 判据用「距底部不足一屏的 1/3」预加载，避免滚到底才追加导致的位置跳动。
    /// </summary>
    private void WeekScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is null || sender is not ScrollViewer viewer)
        {
            return;
        }

        // 程序自身的定位（点「今天」/ 切视图归位）也会触发 ScrollChanged。
        // 那种情况下不能再追加日期：追加会往 ItemsControl 里塞 28 个新容器，
        // 布局在滚动动画中途整批重算，用户感觉到的是「点了今天要卡 1~2 秒」。
        // 定位期间的目标行本来就在已铺开的范围内，也不需要追加。
        if (_suppressWeekAutoExtend)
        {
            return;
        }

        // 只在用户**向下**滚动接近底部时才追加；程序初次布局时 Offset=0 不会命中。
        var remaining = viewer.Extent.Height - viewer.Offset.Y - viewer.Viewport.Height;
        var threshold = Math.Max(120, viewer.Viewport.Height / 3);
        if (remaining <= threshold)
        {
            // 追加成功即可（AppLog 只有 Error 级别，这里不需要额外记录）。
            _viewModel.ExtendWeekScroll();
            return;
        }

        // 向上滚到接近顶部时往**前**铺更早的日期。
        // 这是"回头看今天之前"能成立的关键：起始点虽然已经在今天之前，
        // 但一路往上滚总会有到头的时候，这里负责继续往前接。
        var top = viewer.Offset.Y;
        if (top <= threshold)
        {
            _viewModel.ExtendWeekScrollBackward();
        }
    }

    /// <summary>
    /// 周视图左栏在**头部插入了 N 天**时回调：内容整体下移了 N 行，
    /// 滚动偏移必须加上 N × 行高，否则用户当前看的那一天会瞬间往下跳一整屏。
    ///
    /// <para>与 <see cref="OnWeekScrollHeadTrimmed"/> 正好相反：那个是裁掉头部要**减**，
    /// 这个是插入头部要**加**。两个方向都必须补偿，只补一个就会出现"往下滚很顺、往上滚会跳"。</para>
    ///
    /// <para>这里直接同步赋值是安全的：补偿只在**贴近顶部**时发生，加完仍在旧 Extent 的
    /// 可滚动范围之内，不会被还没更新的 Extent 夹掉。（反方向就未必 —— 往下滚到底时
    /// 若靠加偏移补偿，就会撞上旧上限，所以那条路径用的是"从头部裁掉、偏移往下减"。）</para>
    /// </summary>
    private void OnWeekScrollHeadPrepended(int addedDays)
    {
        if (WeekScrollViewer is null || _viewModel is null)
        {
            return;
        }

        var delta = addedDays * _viewModel.WeekScrollCellHeight;
        var current = WeekScrollViewer.Offset.Y;
        ApplyWeekOffset(current + delta);
    }

    /// <summary>
    /// 周视图左栏超出上限、头部被裁掉 N 天时回调：内容整体上移了 N 行，
    /// 滚动偏移必须下调 N × 行高，否则用户当前看到的位置会瞬间跳变。
    /// 用程序化滚动闸门包住，避免这次补偿滚动又被误判成「滚到底」而再次追加。
    /// </summary>
    private void OnWeekScrollHeadTrimmed(int trimmedDays)
    {
        if (WeekScrollViewer is null || _viewModel is null)
        {
            return;
        }

        var delta = trimmedDays * _viewModel.WeekScrollCellHeight;
        var current = WeekScrollViewer.Offset.Y;
        ApplyWeekOffset(Math.Max(0, current - delta));
    }

    /// <summary>抑制「滚到接近底部就追加日期」的闸门。<see cref="ScrollWeekToToday"/> 在赋值 Offset
    /// 前后把它置位，避免程序化滚动被误判成用户滚到底。</summary>
    private bool _suppressWeekAutoExtend;

    /// <summary>顶栏左右内边距 + 列间距的固定开销（Padding 8,4 两侧 + 三列之间的间距）。</summary>
    private const double TopBarHorizontalChrome = 24.0;

    /// <summary>
    /// 量出一个面板在当前内容下的「自然宽度」。
    ///
    /// 只测自然宽度、不施加外部约束：面板被隐藏（IsVisible=false）时 Avalonia 会直接给 0，
    /// 所以调用前必须先把要测的面板置可见。测量期间用 DesiredSize 而不是 Bounds —— 后者是
    /// 上一轮布局的残留值，量不出"内容变了"这件事。
    /// </summary>
    private static double MeasurePanelWidth(Control? panel, Control? exclude = null)
    {
        if (panel is null)
        {
            return 0;
        }

        // 临时把 exclude 收起来再量，得到"不含这组装饰设置"的净宽度。
        var excludedVisible = false;
        if (exclude is not null)
        {
            excludedVisible = exclude.IsVisible;
            exclude.IsVisible = false;
        }

        try
        {
            panel.Measure(new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity));
            return panel.DesiredSize.Width;
        }
        finally
        {
            if (exclude is not null)
            {
                exclude.IsVisible = excludedVisible;
            }
        }
    }

    private void ApplyWindowBounds(WindowBounds b)
    {
        _applyingBounds = true;
        try
        {
            var w = Math.Max(MinWindowWidth, b.Width);
            var h = Math.Max(MinWindowHeight, b.Height);

            // 按**目标坐标**找屏幕，而不是 ScreenFromWindow(this) ——
            // 后者看的是窗口当前位置，启动时那还是默认位置，会选错屏。
            var screen = FindScreenFor(b.Left, b.Top) ?? Screens.Primary;
            if (screen?.WorkingArea is PixelRect wa)
            {
                // 关键：WorkingArea 是**物理像素**，而窗口的 Width/Height 是**逻辑像素** ——
                // 直接比大小会在 DPI 缩放下失效（缩放 125% 时物理宽比逻辑宽大 25%，
                // 于是 980 逻辑宽的窗口被判定"没超"，实际早已超出屏幕）。
                // 位置（Position）本来就是物理像素，所以只需把尺寸换算成逻辑像素来比较。
                var scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
                w = Math.Min(w, Math.Max(MinWindowWidth, wa.Width / scaling));
                h = Math.Min(h, Math.Max(MinWindowHeight, wa.Height / scaling));
                Width = w;
                Height = h;

                var pixelWidth = (int)Math.Round(w * scaling);
                var pixelHeight = (int)Math.Round(h * scaling);
                int x;
                int y;

                // 屏幕之外 / 只露一点点的窗口会被用户看成"程序没打开"（拔掉外接显示器就中招）。
                // 判定标准：可见面积小于半个窗口，就认为它回不来了，拉回主屏。
                if (IsMostlyOffScreen(b, pixelWidth, pixelHeight))
                {
                    var primary = Screens.Primary;
                    var pwa = primary?.WorkingArea ?? wa;
                    x = pwa.X + Math.Max(0, (pwa.Width - pixelWidth) / 2);
                    y = pwa.Y + Math.Max(0, (pwa.Height - pixelHeight) / 3);
                    AppLog.Error(
                        null,
                        $"[WindowBounds] 窗口记忆坐标 {b.Left},{b.Top} 已不在任何屏幕可见区，拉回主屏 {x},{y}");
                }
                else
                {
                    x = Math.Clamp((int)b.Left, wa.X, Math.Max(wa.X, wa.X + wa.Width - pixelWidth));
                    y = Math.Clamp((int)b.Top, wa.Y, Math.Max(wa.Y, wa.Y + wa.Height - pixelHeight));
                }

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

    /// <summary>
    /// 按物理坐标找包含该点的屏幕；没有屏幕包含时，退而求其次找**相交面积最大**的那块。
    /// 找不到任何相交屏幕返回 null。
    /// </summary>
    private Screen? FindScreenFor(double left, double top)
    {
        if (Screens.All.Count == 0)
        {
            return null;
        }

        var point = new PixelPoint((int)left, (int)top);
        foreach (var screen in Screens.All)
        {
            if (screen.Bounds.Contains(point))
            {
                return screen;
            }
        }

        // 坐标落在所有屏幕外：选离得最近的一块，后续 IsMostlyOffScreen 会把它拉回来。
        return Screens.All
            .OrderBy(s => DistanceSquaredTo(s.Bounds, left, top))
            .First();
    }

    private static double DistanceSquaredTo(PixelRect r, double x, double y)
    {
        var dx = x < r.X ? r.X - x : x > r.Right ? x - r.Right : 0;
        var dy = y < r.Y ? r.Y - y : y > r.Bottom ? y - r.Bottom : 0;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// 窗口是否"基本落在所有屏幕之外"——按各屏可见区域的并集算覆盖面积，
    /// 覆盖不到窗口面积的一半就判定为回不来。这样"一半挂在屏幕边缘"仍算可见（用户能拖回来），
    /// 只有真的整块跑到屏外（典型：外接显示器被拔掉）才会触发回拉。
    /// </summary>
    private bool IsMostlyOffScreen(WindowBounds b, int pixelWidth, int pixelHeight)
    {
        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            return false;
        }

        var left = (int)b.Left;
        var top = (int)b.Top;
        var right = left + pixelWidth;
        var bottom = top + pixelHeight;

        long visible = 0;
        foreach (var screen in Screens.All)
        {
            var wa = screen.WorkingArea;
            var overlapW = Math.Min(right, wa.Right) - Math.Max(left, wa.X);
            var overlapH = Math.Min(bottom, wa.Bottom) - Math.Max(top, wa.Y);
            if (overlapW > 0 && overlapH > 0)
            {
                visible += (long)overlapW * overlapH;
            }
        }

        return visible < (long)pixelWidth * pixelHeight / 2;
    }

    /// <summary>
    /// 把当前窗口几何记进设置：通用记忆（<see cref="CalendarSettings.WindowBounds"/>）+ 当前视图的记忆。
    ///
    /// <para>⚠️ <b>必须存真实坐标</b>。早先版本在这里把 Left/Top 抹成 0（想用"没有位置"来表达
    /// "分视图只记尺寸"），可启动恢复是把整条记录当完整边界读回去的 —— 于是每次开机窗口都精准
    /// 落在屏幕左上角。用户反馈的「重启后记不住之前的位置，打开在左上角」根因就在这里。</para>
    ///
    /// <para>"位置不按视图分记"应该由读取侧表达（见 <see cref="WindowBoundsResolver.ForStartup"/>），
    /// 不要在写入侧靠抹掉坐标来暗示 —— 那样两份数据会打架。</para>
    /// </summary>
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
                s.MonthWindowBounds = b;
                break;
            case CalendarViewMode.Week:
                s.WeekWindowBounds = b;
                break;
            case CalendarViewMode.Year:
                s.YearWindowBounds = b;
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

    /// <summary>
    /// 已完成行行首悬停浮现的「↺」：把删除线任务还原为未完成（会自动回到未完成那一段的排序位置）。
    /// 不复用勾选框事件，是因为已完成行根本没有勾选框 —— 还原入口只有这个小箭头和右键菜单。
    /// </summary>
    private void RestoreTask_Click(object? sender, RoutedEventArgs e)
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

    /// <summary>
    /// 右键任务 →「编辑」：打开编辑窗口，任务内容 / 任务时间 / 提醒时间一次改完。
    ///
    /// 以前这里只是把标题就地换成输入框（<c>task.BeginEdit()</c>），任务时刻和提醒时间
    /// 既没有控件也没有入口 —— 用户报的「编辑时改不了提醒时间和任务时间」就是这一条。
    /// 行内改标题的能力保留（双击任务条仍是就地改名，见 <see cref="TodayTaskItem_PointerPressed"/>）。
    /// </summary>
    private void EditTask_Click(object? sender, RoutedEventArgs e)
    {
        if (TaskFrom(sender) is not { } item)
        {
            return;
        }

        // 开编辑窗口前先把别的草稿收尾，避免两个输入态叠在一起
        CommitPendingEdits();

        var dialog = new TaskEditWindow(item.Model, _config);
        dialog.Closed += (_, _) =>
        {
            _modalDialogOpen = false;
            if (dialog.Saved)
            {
                _viewModel?.UpdateTask(item.Id, dialog.EditedTitle, dialog.EditedTime, dialog.EditedLeads);
            }
        };

        // 弹窗期间挡住「焦点离开表单就提交草稿」的逻辑
        _modalDialogOpen = true;

        // 嵌入桌面模式下主窗体置底且不激活，模态显示会被一起压底／抢不到激活；
        // 与设置窗口同一策略，改非模态打开，收尾统一走 Closed 回调。
        if (_config.EmbedDesktop)
        {
            dialog.Show();
        }
        else
        {
            _ = dialog.ShowDialog(this);
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
        // 含「不提醒」→ 空列表；空（未指定）→ null（默认提前15分钟）；否则档位列表。
        var leads = MicaAgenda.App.Helpers.ReminderLeadCatalog.ToCommitLeads(cell.ReminderLeadLabels);
        var time = cell.DraftTimeOnly;   // CancelAdd 会把草稿清掉，先取值
        cell.CancelAdd();
        if (ReferenceEquals(_pendingAddCell, cell))
        {
            _pendingAddCell = null;
        }

        // 以前这里没有落盘：格子里加完任务要等下一次别的改动才写文件。
        if (!string.IsNullOrWhiteSpace(title))
        {
            _viewModel?.AddTask(cell.Date, title, leads, time);
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
        // 时间选择弹层打开中：焦点可能正在弹层里（弹层是独立顶层窗口，不在主窗口视觉树里，
        // 光靠 GetVisualAncestors 找不到），也可能处于"点了按钮但焦点还没落进弹层"的瞬间。
        // 只要弹层开着，就视为"仍在表单交互中"，绝不能提交。
        if (_timePopupOpen)
        {
            return true;
        }

        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual focused)
        {
            return false;
        }

        return focused.GetVisualAncestors().Any(ancestor => ancestor is Control { Tag: "AddTaskForm" });
    }

    /// <summary>时间选择弹层是否打开中（由 ToggleButton 的 IsCheckedChanged 维护）。</summary>
    private bool _timePopupOpen;

    /// <summary>
    /// 时间小按钮的开关状态变化（弹层开/关）。
    ///
    /// 弹层打开时置标记，挡住「输入框失焦 → 误提交表单」的时序：
    /// 点按钮瞬间输入框先失焦、焦点还没落进 Popup，Background 优先级里
    /// <see cref="CommitTodayAddIfFocusLeftForm"/> 会误判焦点已离开表单而提交收起。
    /// 用这个标记让判定在弹层打开期间始终返回"还在表单内"。
    /// </summary>
    private void TodayTimeToggle_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton toggle)
        {
            _timePopupOpen = toggle.IsChecked == true;
        }
    }

    private void CommitTodayTask_Click(object? sender, RoutedEventArgs e) => CommitTodayAdd();

    /// <summary>
    /// 右侧面板的「提醒时间」多选下拉：第一次勾上任一档位、但推送通道还没配好 → 说清楚送不出去。
    ///
    /// 面板模板在宿主里有两份实例（右侧面板 + 窄窗视图），两边绑的是同一个
    /// TodayTaskLeadLabels，同一次勾选两个实例都会回调 —— 只让当前可见的那个弹，免得弹两遍。
    /// </summary>
    private void TodayLeadPicker_FirstSelected(object? sender, EventArgs e)
    {
        if (_viewModel?.IsAddingTodayTask == true && sender is Control { IsEffectivelyVisible: true })
        {
            WarnIfReminderChannelMissing();
        }
    }

    /// <summary>日期格子里那张悬浮表单的多选下拉，判断与提示同上。</summary>
    private void CellLeadPicker_FirstSelected(object? sender, EventArgs e)
    {
        if (sender is Control { DataContext: DayCellViewModel cell } && cell.IsAddingTask)
        {
            WarnIfReminderChannelMissing();
        }
    }

    /// <summary>提醒是走 webhook 推的：一个通道都没配就提醒用户"这条提醒到点也不会响"。</summary>
    private void WarnIfReminderChannelMissing()
    {
        // 飞书 webhook 在 AppConfig 里（日历数据那份 CalendarSettings 不管提醒）。
        if (!ReminderGate.ShouldWarnOnLeadToggle(_config, hadAny: false, hasAny: true))
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

    // ===== 任务拖拽改期（月 / 周视图：按住任务条拖到目标日期格子松开）=====

    private Guid? _dragTaskId;
    private Avalonia.Point? _dragOrigin;
    private bool _dragging;

    private void TaskItem_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsLeftButton(e, sender) || sender is not Control { DataContext: TaskItemViewModel vm } control)
        {
            return;
        }

        // 记录起始任务 + 起始位置，并捕获指针：移动/松开事件即使指针已移出任务条也能路由回来。
        _dragTaskId = vm.Id;
        _dragOrigin = e.GetPosition(this);
        _dragging = false;
        DragGhostText.Text = vm.Title;
        e.Pointer.Capture(control);
    }

    /// <summary>拖拽中：位移超过阈值后显示跟随鼠标的幽灵预览，实时反映"正在把任务拖到哪"。</summary>
    private void TaskItem_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTaskId is null || _dragOrigin is null)
        {
            return;
        }

        var pos = e.GetPosition(this);
        var origin = _dragOrigin.Value;
        var isDragging = Math.Abs(pos.X - origin.X) >= 6 || Math.Abs(pos.Y - origin.Y) >= 6;
        if (!isDragging)
        {
            return;
        }

        _dragging = true;
        DragGhost.IsVisible = true;

        // 幽灵预览悬停在光标右下方一点，避免遮住目标格子；位置在窗口坐标系里直接摆。
        Canvas.SetLeft(DragGhost, Math.Min(pos.X + 14, Math.Max(0, Bounds.Width - 160)));
        Canvas.SetTop(DragGhost, Math.Min(pos.Y + 12, Math.Max(0, Bounds.Height - 40)));
    }

    private void TaskItem_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragTaskId is null || _dragOrigin is null)
        {
            return;
        }

        var taskId = _dragTaskId.Value;
        var origin = _dragOrigin.Value;
        var wasDragging = _dragging;
        _dragTaskId = null;
        _dragOrigin = null;
        _dragging = false;
        DragGhost.IsVisible = false;

        if (sender is Control control)
        {
            e.Pointer.Capture(null);
        }

        var end = e.GetPosition(this);
        // 位移小于 6px 视为点击（勾选 / 选中），不是拖拽
        if (!wasDragging && Math.Abs(end.X - origin.X) < 6 && Math.Abs(end.Y - origin.Y) < 6)
        {
            return;
        }

        // 找松开位置所在的日期格子（DataContext 是 DayCellViewModel 的容器）
        if (FindDayCellAt(end)?.DataContext is DayCellViewModel day && _viewModel is not null)
        {
            if (_viewModel.ChangeTaskDate(taskId, day.Date))
            {
                _ = SaveAsync();
            }
        }
    }

    /// <summary>按窗口坐标找到其下的日期格子（月视图 dayCell / 周视图 weekDayRow，DataContext 都是 DayCellViewModel）。</summary>
    private Control? FindDayCellAt(Avalonia.Point windowPos)
    {
        foreach (var control in this.GetVisualDescendants().OfType<Control>())
        {
            if (control.DataContext is not DayCellViewModel)
            {
                continue;
            }

            if (control.TranslatePoint(new Avalonia.Point(0, 0), this) is { } origin)
            {
                if (new Avalonia.Rect(origin, control.Bounds.Size).Contains(windowPos))
                {
                    return control;
                }
            }
        }

        return null;
    }

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
    /// 把光标写到「指针底下那一圈」的每个元素上。    ///
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
                or ListBoxItem or MenuItem or TabItem or CalendarDatePicker
                or Controls.AiChatPanel)
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

    private void ClearOpenTasks_Click(object? sender, RoutedEventArgs e)
        => BulkWeekAction("删除未完成任务", "确定删除全部「未完成」的任务吗（含已逾期的历史欠账）？此操作不可撤销。", () => _viewModel!.ClearOpenTasks());

    private void MarkOpenCompleted_Click(object? sender, RoutedEventArgs e)
        => BulkWeekAction("标记完成", "把全部「未完成」的任务标记为已完成吗（含已逾期的历史欠账）？", () => _viewModel!.MarkOpenCompleted());

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
            // 必须置顶：这个框常常是「设置面板 → 检查更新」链路里弹的，而设置面板压在
            // 置底嵌入的主窗体之上 —— 不置顶就会被设置面板盖住，用户点不到「确定」（用户上报的 bug）。
            Topmost = true,
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

        // 属主随当前最上层窗口走：设置面板开着就挂它名下，否则「下载完成」这类确认框
        // 会以主窗体为属主，居中到置底主窗体、又被设置面板挡住。
        await win.ShowDialog(ResolveDialogOwner());
        return result;
    }

    // ===== 时间轴无限滚动（含向上扩展的偏移补偿）=====

    private void MonthScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv || _viewModel is null)
        {
            return;
        }

        // 定位到锚点月引发的滚动不参与边缘扩展判定，
        // 否则点「今天」往回跳会被当成"滚到顶了"，反手又插进一个月。
        if (_programmaticScroll)
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

    // ===== 把视口定位到锚点月（点「今天」/ 换月 / 首屏）=====

    /// <summary>
    /// 结构重建后把视口滚回锚点月。
    ///
    /// 月视图是一条可以无限滚下去的时间轴、年视图是一整年 12 个月的滚动列表，
    /// "现在看的是哪一段"是由**滚动位置**表达的，而滚动浏览并不会改 ViewModel 的日期。
    /// 所以"点今天要跳回今天"这件事，ViewModel 只能发信号，真正滚的是这里。
    /// </summary>
    private void OnTimelineRebuilt()
    {
        Dispatcher.UIThread.Post(() => ScrollViewportToAnchor(0), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 重试次数上限。刚切视图 / 首帧布局尚未跑完时，月份容器还没生成，
    /// 需要等下一帧；但绝不能无限重试，否则就是一个死循环。
    /// </summary>
    private const int MaxScrollAttempts = 5;

    private void ScrollViewportToAnchor(int attempt)
    {
        if (_viewModel is null)
        {
            return;
        }

        bool done;
        try
        {
            var anchor = _viewModel.TimelineAnchor;
            done = _viewModel.Settings.ViewMode switch
            {
                CalendarViewMode.Month => ScrollMonthBlockToTop(anchor),
                CalendarViewMode.Year => ScrollYearMonthToTop(anchor),
                // 周视图左栏是一条可无限上下的滚动长列表，"现在看的是哪一段"同样由滚动位置表达。
                // 用户翻到几周之后时点「今天」，必须把列表滚回今天所在那一周 ——
                // 否则日期数据虽然回到了今天，画面却还停在原来那一段，看起来就是"点了没反应"。
                CalendarViewMode.Week => ScrollWeekToToday(),
                _ => true
            };
        }
        catch (Exception ex)
        {
            // 定位失败不该影响主流程（这条路径首屏就会跑一次），记日志后作罢。
            AppLog.Error(ex, "MainWindow.ScrollViewportToAnchor");
            return;
        }

        if (!done && attempt < MaxScrollAttempts)
        {
            Dispatcher.UIThread.Post(() => ScrollViewportToAnchor(attempt + 1), DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// 把周视图左栏滚到**今天居中**的位置。
    ///
    /// 左栏是一维的日期长列表，所以定位就是「目标行的索引 × 行高」。用行高算偏移而不是
    /// <c>ScrollIntoView</c>：后者要先把容器虚拟化出来才拿得到，首帧/刚切视图时拿不到就会静默失败；
    /// 行高是已知量（格子高度 + 下外边距），算出来直接赋值最稳。
    ///
    /// 目标是让今天那一行的**垂直中心对准视口中心** —— 一屏 7 格时，正好是
    /// 上面 3 格、今天居中、下面 3 格。用"今天所在周的第一行"来定位是不对的：
    /// 那样今天落在第 0~6 行取决于当天是星期几，每点一次位置都不一样。
    /// </summary>
    private bool ScrollWeekToToday()
    {
        if (WeekScrollViewer is null || _viewModel is null)
        {
            return true;
        }

        var todayIndex = _viewModel.IndexOfTodayInWeekScroll;
        if (todayIndex < 0)
        {
            // 理论上不会发生（列表起点就在今天之前）。兜底滚回顶部。
            ApplyWeekOffset(0);
            return true;
        }

        var rowSpan = _viewModel.WeekScrollCellHeight + WeekDayRowBottomMargin;

        // 今天上方要留出的高度 = (视口高 - 行高) / 2。
        // 视口还没量出来（首帧 / 刚切视图）时它可能是 0，按"默认 7 格"这个常见情形算，
        // 否则会退化成"今天贴在最顶上"，用户第一眼看到的就不居中。
        var viewport = WeekScrollViewer.Viewport.Height;
        var above = viewport > 0
            ? Math.Max(0, (viewport - rowSpan) / 2)
            : rowSpan * WeekCenterRowsFallback;

        var target = (todayIndex * rowSpan) - above;

        // 夹到可滚动范围内，避免超出上下限导致偏移被拒（赋值被忽略看起来就像"没生效"）。
        var maxOffset = Math.Max(0, WeekScrollViewer.Extent.Height - WeekScrollViewer.Viewport.Height);
        ApplyWeekOffset(Math.Clamp(target, 0, maxOffset));
        return true;
    }

    /// <summary>视口还没量出来时，默认按"可见 7 格"给今天上方留 3 行的位置。</summary>
    private const double WeekCenterRowsFallback = 3;

    /// <summary>
    /// 带闸门地设置周视图左栏的滚动偏移。
    ///
    /// 闸门关掉的这段时间里 <see cref="WeekScroll_ScrollChanged"/> 不会追加日期 —— 否则
    /// 程序化滚动会被当成"用户滚到底"，在滚动还没稳定时往列表尾部塞 28 个容器，
    /// 布局整批重算，表现为点「今天」后卡顿 1~2 秒。
    ///
    /// 用 <c>DispatcherPriority.Background</c> 复位而不是立即复位：Offset 赋值引发的
    /// ScrollChanged 是异步派发的，立刻放开闸门会赶在它之前，等于没拦。
    /// </summary>
    private void ApplyWeekOffset(double offset)
    {
        if (WeekScrollViewer is null)
        {
            return;
        }

        _suppressWeekAutoExtend = true;
        try
        {
            WeekScrollViewer.Offset = new Avalonia.Vector(0, offset);
        }
        finally
        {
            Dispatcher.UIThread.Post(
                () => _suppressWeekAutoExtend = false,
                DispatcherPriority.Background);
        }
    }

    /// <summary>周视图日期行自带的下外边距（见 WeekDayRowTemplate 的 Margin="0,0,0,4"）。</summary>
    private const double WeekDayRowBottomMargin = 4.0;

    /// <summary>把月视图时间轴滚到指定月份块的顶边。返回 false 表示容器尚未生成，调用方应重试。</summary>
    private bool ScrollMonthBlockToTop(DateOnly month)    {
        if (MonthScrollViewer is null || MonthItemsControl is null || _viewModel is null)
        {
            return true;
        }

        var block = _viewModel.TimelineMonths
            .FirstOrDefault(b => b.Year == month.Year && b.Month == month.Month);
        if (block is null)
        {
            return true;
        }

        var container = FindContainer(MonthItemsControl, block);
        if (container is null)
        {
            return false;
        }

        if (container.TranslatePoint(new Point(0, 0), MonthItemsControl) is not { } origin)
        {
            return false;
        }

        SetScrollOffset(MonthScrollViewer, origin.Y);
        return true;
    }

    /// <summary>把年视图滚到锚点月的顶边。返回 false 表示容器尚未生成，调用方应重试。</summary>
    private bool ScrollYearMonthToTop(DateOnly anchor)
    {
        if (YearScrollViewer is null || YearItemsControl is null || _viewModel is null)
        {
            return true;
        }

        // 年份对不上时（视图停在别的年份）不要瞎滚，否则会滚到"同月但不同年"那一格。
        if (_viewModel.SelectedDate.Year != anchor.Year)
        {
            return true;
        }

        var target = _viewModel.YearMonths.FirstOrDefault(m => m.Month == anchor.Month);
        if (target is null)
        {
            return true;
        }

        var container = FindContainer(YearItemsControl, target);
        if (container is null)
        {
            return false;
        }

        if (container.TranslatePoint(new Point(0, 0), YearItemsControl) is not { } origin)
        {
            return false;
        }

        // 年视图是 3 列 × 4 行的宫格，留 8px 顶距，免得月份标题贴着上边缘。
        SetScrollOffset(YearScrollViewer, origin.Y - 8);
        return true;
    }

    /// <summary>
    /// 按内容偏移做程序化滚动。
    /// 不走 BringIntoView：后者会触发 RequestBringIntoView，在这个无限滚动的
    /// 时间轴里会连带引起自动扩展，表现为"点一下今天，月份自己又长了一截"。
    /// </summary>
    private void SetScrollOffset(ScrollViewer viewer, double y)
    {
        _programmaticScroll = true;
        viewer.Offset = new Vector(viewer.Offset.X, Math.Max(0, y));

        // 必须在下一帧无条件复位，不能指望 ScrollChanged 去消费这个标记：
        // 目标偏移与当前相同时压根不会触发 ScrollChanged，标记就会残留，
        // 之后用户真的滚到边缘时会被误判成程序化滚动，时间轴再也不扩展了。
        Dispatcher.UIThread.Post(() => _programmaticScroll = false, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 找到 ItemsControl 中承载指定数据项的那个容器控件。
    ///
    /// 月/年视图用的都是非虚拟化的 ItemsControl，容器在一次布局后全部已生成；
    /// 前序遍历里第一个 DataContext 命中该项的元素就是它自己的容器
    /// （容器一定排在自己的子元素前面）。
    /// </summary>
    private static Control? FindContainer(ItemsControl itemsControl, object item)
    {
        foreach (var visual in itemsControl.GetVisualDescendants())
        {
            if (visual is Control control && ReferenceEquals(control.DataContext, item))
            {
                return control;
            }
        }

        return null;
    }
}