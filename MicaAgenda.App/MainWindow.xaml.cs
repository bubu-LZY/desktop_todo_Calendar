using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using MicaAgenda.App.Models;
using MicaAgenda.App.Services;
using MicaAgenda.App.ViewModels;
using Point = System.Windows.Point;
using Screen = System.Windows.Forms.Screen;

namespace MicaAgenda.App;

public partial class MainWindow : Window
{
    private readonly CalendarDataStore _store = new();
    private readonly ChinaHolidayService _holidayService = new();
    private readonly AppConfigStore _configStore = new();
    private readonly object _syncRoot = new();

    /// <summary>
    /// 数据文件加载失败时置 true，全程关闭自动保存（数据安全闸门）：
    /// 加载失败意味着原文件已损坏并被隔离改名，若照常自动保存，空数据会立刻在原路径新建空文件。
    /// </summary>
    private bool _suppressAutoSave;
    private readonly DispatcherTimer _clockTimer;
    private System.Threading.Timer? _gcTrimTimer;
    private MainViewModel? _viewModel;
    private AppConfig _config = new();
    private TaskApiServer? _apiServer;
    private McpServer? _mcpServer;
    private ReminderService? _reminderService;
    private BackupService? _backupService;
    private ReportService? _reportService;
    private MindMapReviewSyncService? _mindMapSyncService;
    private TrayIconService? _trayIcon;
    private int? _loadedHolidayYear;
    private bool _isUiReady;
    private bool _isApplyingSettings;
    private bool _fitWindowToContentQueued;
    private bool _closeAfterSaveRequested;
        private bool _isExiting;
        private bool _userSizing;
        // 月视图是像素滚动，VerticalOffset / ExtentHeight / ViewportHeight 单位均为像素。
        // 距边缘不足 60px 时预加载下一批月份。
    private const double TimelineLoadThreshold = 60;
    private const double NarrowLayoutThreshold = 380.0;

    /// <summary>
    /// 顶栏单行能容下「日期信息 + 常驻按钮」的最小宽度。再窄就换行：
    /// 第一行只留日期信息，第二行放「今天 / 视图 / 设置」这一组。
    /// </summary>
    private const double TopBarWrapThreshold = 360.0;

    /// <summary>
    /// 窗口可被拖到的最小宽度 —— 也就是「所有设置按钮都还能看见、不被遮住」的那条线。
    /// 用户的要求是：缩到最小就停在这儿，不能再缩。
    ///
    /// 取值口径与 Avalonia 宿主保持一致（300）：换行档下第二行整组按钮的自然宽度再加一点余量，
    /// 保证换行后第二行自己不会溢出。比它更窄时按钮组会互相挤压 / 被裁掉，所以不能再小。
    /// </summary>
    private const double MinWindowWidth = 300.0;

    /// <summary>窗口可被拖到的最小高度：顶栏两行 + 主体至少露出一行内容。</summary>
    private const double MinWindowHeight = 240.0;

        // 时间轴月份块数量上限与扩展冷却。
        // 这是防御性兜底：任何未预料到的路径都不允许时间轴无限增长。
        // 上限值统一以 MainViewModel.TimelineMaxMonths 为准，避免两处定义不一致。
        private const int TimelineMaxMonths = MainViewModel.TimelineMaxMonths;
        private static readonly TimeSpan TimelineExtendCooldown = TimeSpan.FromMilliseconds(350);

        // 今日任务面板快速输入框的标识（三个视图各有一个实例，按 Tag 定位当前可见的那个）
        private const string TodayDraftTag = "TodayTaskDraftBox";

        // 程序内部滚动定位（按月吸附 / 跳今天 / 锚定）期间置位，避免被 ScrollChanged 误判为用户滚动。
        private bool _programmaticScroll;

        // 时间轴扩展冷却。扩展会改变内容高度，冷却只约束"滚到边缘自动预加载"，
        // 防止布局变化引发连锁扩展。扩展改为同步执行，不再用互斥标志，
        // 避免连续拖拽时互斥被 Input 优先级事件饿死而导致时间轴卡死。
        private DateTime _lastTimelineExtendAt = DateTime.MinValue;
        // 被冷却拦下的边缘扩展请求：冷却结束后补做一次，否则用户停在边缘时
        // 不再产生 ScrollChanged，时间轴会"卡死"再也扩不动。0=无, -1=更早, 1=更晚。
        private int _pendingExtendDir;
        private DispatcherTimer? _extendRetryTimer;

    // WPF 会自行重置 HWND 父窗口/样式，导致"嵌入桌面"失效；用看门狗周期性重新嵌入。
    // 判断"用户是否正在操作本窗口"一律以光标命中的窗口为准（见 IsCursorOverWindow），
    // 不用 MouseEnter/MouseLeave 标记——后者在被遮挡/失焦时可能不触发，会让窗口永远浮在顶层。
    //
    // 间隔 200ms：用户实测点「显示桌面」后窗口要等 1~2 秒才回来，说明「显示桌面」这条路径
    // 上后台事件钩子并不可靠（Win+D / 右下角细条把 WorkerW 提上来的同时前台窗口未必变化），
    // 只能靠轮询兜底。200ms = 一秒 5 次，每次几个只读调用、样式已是目标值就提前返回，开销远小于 UI 刷新本身。
    private readonly DispatcherTimer _embedWatchdog = new() { Interval = TimeSpan.FromMilliseconds(200) };

    // SetWindowPos 常用 flag 组合
    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpNozorder = 0x0004;

    // 选中的日期格子：滚轮仅作用于该格子内的任务列表，ESC 退出。
    private Border? _activeDayCellBorder;

    // 锁定滚动模式：开启后滚轮只在选中格子内滚动，不滚动整个月份。
    private bool _lockScroll;

    // 滚轮平滑：累积滚轮 delta，达到阈值才翻月，避免一次滚轮跳一整月。
    private double _scrollAccumulatedDelta;
    private DateTime _scrollAccumLastTick = DateTime.MinValue;
    // 累积阈值（标准 delta=120，阈值 240 = 需要 2 次滚轮才翻一月）
    private const double ScrollDeltaThreshold = 240;

    public MainWindow()
    {
        InitializeComponent();
        Title = "MicaAgenda v5.2.6";

        // 窗口初始化前同步加载配置，确保桌面嵌入/锁定在首帧即生效
        _config = _configStore.Load();
        ShowActivated = !_config.EmbedDesktop;
        // 托盘图标：关闭窗口后仍可重新打开 / 打开设置 / 退出
        _trayIcon = new TrayIconService(
            showWindow: ShowWindow,
            openSettings: OpenSettings,
            exit: ExitApplication,
            hideWindow: Hide);

        // 窗口几何（位置 / 尺寸）记忆：拖动 / 缩放停下后防抖落盘，
        // 不依赖"用户从托盘正常退出"这条路径 —— 重启 / 关机不会走那里。
        LocationChanged += (_, _) => OnWindowGeometryChanged();
        SizeChanged += (_, _) => OnWindowGeometryChanged();
        _boundsSaveTimer = new DispatcherTimer { Interval = BoundsSaveQuietPeriod };
        _boundsSaveTimer.Tick += (_, _) =>
        {
            _boundsSaveTimer.Stop();
            PersistBoundsIfChanged();
        };

        // 关机 / 重启 / 注销前同步落盘。SessionEnding 是 WPF 自带的事件，
        // 正好覆盖"重启后位置回到默认"的根因（那条路径不经过 Window_Closing）。
        SystemEvents.SessionEnding += (_, _) => SaveBoundsOnShutdown();

        _clockTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _clockTimer.Tick += async (_, _) =>
        {
            try
            {
                if (_viewModel is not null)
                {
                    var previousYear = _viewModel.Today.Year;
                    _viewModel.RefreshClock();
                    if (_viewModel.Today.Year != previousYear)
                    {
                        await RefreshHolidaysForVisibleYearAsync(force: true);
                    }

                    // 仅在数据有变更时才落盘，避免每分钟无谓写入
                    if (_viewModel.IsDirty)
                    {
                        await SaveAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                // 时钟回调是 async void，异常会直接终止程序，必须兜底
                App.LogError(ex, "ClockTimer");
            }
        };
    }

    // ===== 窗口几何（位置 / 尺寸）记忆 =====

    /// <summary>几何变化计数：每次 Position/Size 变化 +1，用于判断"稳定了没有"。</summary>
    private long _boundsRevision;

    /// <summary>已落盘的几何版本号；与 <see cref="_boundsRevision"/> 相等说明没有待存的改动。</summary>
    private long _boundsSavedRevision;

    private DispatcherTimer? _boundsSaveTimer;

    /// <summary>窗口几何稳定多久之后落盘。</summary>
    private static readonly TimeSpan BoundsSaveQuietPeriod = TimeSpan.FromMilliseconds(1200);

    /// <summary>关机 / 重启落盘的"只跑一次"闸门。</summary>
    private bool _boundsSavedOnShutdown;

    /// <summary>
    /// 窗口几何变了（拖动或缩放）。只登记"有改动 + 顺延稳定定时器"，真正的写盘交给
    /// <see cref="PersistBoundsIfChanged"/> —— 拖动过程中每帧都写盘既浪费又可能写到一半状态。
    /// </summary>
    private void OnWindowGeometryChanged()
    {
        _boundsRevision++;

        // 程序自身在应用记忆位置（启动恢复 / 切视图）时也会走到这里，但那些场景不需要防抖落盘。
        if (_isApplyingSettings || _boundsSaveTimer is null)
        {
            return;
        }

        // 锁定位置时不记 —— 用户已经把窗口钉住了。
        if (_config.LockWindow)
        {
            return;
        }

        _boundsSaveTimer.Stop();
        _boundsSaveTimer.Start();
    }

    /// <summary>几何确实变过才写盘。</summary>
    private void PersistBoundsIfChanged()
    {
        if (_boundsRevision == _boundsSavedRevision || _viewModel is null)
        {
            return;
        }

        RunGuarded(SaveCurrentViewBounds, "Window.BoundsSave");
        _boundsSavedRevision = _boundsRevision;
    }

    /// <summary>
    /// 会话结束（关机 / 重启 / 注销）路径上的落盘：<b>同步</b>写。
    ///
    /// 这里绝不能用 <c>await SaveAsync()</c>：关机时留给进程的时间只有几十到几百毫秒，
    /// 异步 I/O 很可能还没落到磁盘进程就没了 —— 表现就是"位置存了但重启后没生效"。
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
            SaveCurrentViewBounds();

            if (_viewModel is { IsDirty: true } viewModel && !_suppressAutoSave)
            {
                if (_store.Save(viewModel.Data))
                {
                    viewModel.MarkSaved();
                }
                else
                {
                    App.LogError(null, "会话结束落盘被跳过：上一次保存仍持锁或写入失败");
                }
            }
        }
        catch (Exception ex)
        {
            // 关机路径上绝不能抛异常：抛出去会影响系统结束会话。
            App.LogError(ex, "SaveBoundsOnShutdown");
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            // 初始化失败不应终止程序：记录日志，主窗口仍可展示（只是没有数据）
            App.LogError(ex, "Window_Loaded");
        }
        finally
        {
            _isUiReady = true;
            // Loaded 完成后再夹一次：此时 ActualWidth 才是真实值，SourceInitialized 时还是 0
            // 用 Dispatcher.BeginInvoke 让当前布局 pass 结束，避免 SizeChanged 循环
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                ClampToWorkingArea();
                UpdateResponsiveLayout();
                // 默认视图就是年视图时，月卡要等这次布局后才量得出实际列宽，补一次字号回填
                UpdateYearFontScale();
            }), DispatcherPriority.Background);
        }
    }

    private Border? _toastHost;

    /// <summary>
    /// 深度优先遍历可视树查找第一个匹配类型的子元素。
    /// </summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var deeper = FindVisualChild<T>(child);
            if (deeper is not null) return deeper;
        }
        return null;
    }

    /// <summary>
    /// 非阻断 toast：底部居中浮出一段文本，3 秒后自动消失。
    /// 挂在 Shell(Border) 上，靠 Margin + VerticalAlignment=Bottom 定位。
    /// </summary>
    private void ShowToast(string message)
    {
        if (_toastHost is null)
        {
            _toastHost = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 8, 14, 8),
                Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x33, 0x33, 0x33)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 14),
                IsHitTestVisible = false,
                Child = new TextBlock { Foreground = Brushes.White, FontSize = 12 }
            };
            Panel.SetZIndex(_toastHost, 9999);
            // Shell(Border) 里套了 Grid 根布局，递归找第一个 Grid 挂上去。
            var innerGrid = FindVisualChild<Grid>(Shell);
            if (innerGrid is not null)
            {
                innerGrid.Children.Add(_toastHost);
            }
        }
        if (_toastHost?.Child is TextBlock inner)
        {
            inner.Text = message;
            _toastHost.Visibility = Visibility.Visible;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                _toastHost!.Visibility = Visibility.Collapsed;
                timer.Stop();
            };
            timer.Start();
        }
    }

    private async Task InitializeAsync()
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
            App.LogError(ex, "Initialize.Load");
            _suppressAutoSave = true;
            data = new CalendarData();
            ShowToast("数据文件读取失败，已隔离备份；自动保存已关闭，请从备份恢复。");
        }

        var autoStartEnabled = _config.AutoStart;
        data.Settings.AlwaysOnTop = false;
        var holidayYear = DateOnly.FromDateTime(DateTime.Now).Year;
        var holidays = await _holidayService.LoadCachedOrEmbeddedAsync(holidayYear);
        _loadedHolidayYear = holidayYear;

        _viewModel = new MainViewModel(data, holidays: holidays, syncRoot: _syncRoot);
        _viewModel.ReviewTaskDeleted += NotifyReviewDeletion;
        _viewModel.ReviewTaskStatusChanged += OnReviewTaskStatusChanged;
        _viewModel.WeekScrollHeadTrimmed += OnWeekScrollHeadTrimmed;

        // 右上角迷你 AI 对话卡片：注入数据与配置，与 MCP 的 AI 工具共用同一套任务执行逻辑。
        AiPanel.Initialize(_viewModel.Data, _syncRoot, () => _config, OnDataChangedFromApi, CreateHostActions());

        // 先把内容挂上并渲染出来，首屏优先；
        // 下面那些与首屏无关的工作统一推迟到 Background 优先级，避免"启动卡两秒才显示全"。
        DataContext = _viewModel;
        ApplySettingsToWindow();
        _clockTimer.Start();
        ApplyViewHeightPolicy();

        // 长驻挂件空闲时定期做一次「优化式」非阻塞 GC，把工作集还给系统（避免常驻内存越堆越高）。
        _gcTrimTimer = new System.Threading.Timer(
            _ => GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false),
            null,
            TimeSpan.FromMinutes(3),
            TimeSpan.FromMinutes(3));

        _viewModel.TimelineRebuilt += OnTimelineRebuilt;
        _ = Dispatcher.BeginInvoke(new Action(ScrollTimelineToAnchor), DispatcherPriority.Loaded);

        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            RunGuarded(() =>
            {
                if (autoStartEnabled)
                {
                    AutoStartService.SetEnabled(true);
                }

                if (_config.HighPriorityStartup)
                {
                    // 自身进程优先级立刻提上去：不需要管理员权限，必定生效。
                    // 启动瞬间用 High 抢首屏，15 秒后自动回落到 AboveNormal 常驻，
                    // 避免桌面挂件长期跟前台应用抢时间片。
                    HighPriorityStartupService.ApplyProcessPriorityWithFallback(true);

                    // 计划任务缺失时补登记一次，但开机过程中不弹 UAC（太打扰），
                    // 失败只记一条日志，用户可以自己在设置里授权。
                    var highPriority = HighPriorityStartupService.Enable(allowElevation: false);
                    if (!highPriority.TaskRegistered)
                    {
                        App.LogError(null, "[STARTUP] 高优先级开机任务未登记：" + highPriority.Message);
                    }
                }
            }, "Startup.AutoStart");

            // 桌面可能在 SourceInitialized 时尚未就绪，这里再尝试一次嵌入，确保真正钉到桌面
            RunGuarded(ApplyDesktopEmbed, "Startup.Embed");

            // 关键：先确保 Token 已生成，再启动任何 HTTP 服务。
            // 否则 API 关闭 / MCP 单独开启时，Token 永远是空字符串，
            // McpServer 会跳过鉴权 ——整个端点裸奔。
            EnsureAuthToken();

            RunGuarded(StartApiServer, "Startup.Api");
            RunGuarded(StartMcpServer, "Startup.Mcp");
            RunGuarded(StartReminderService, "Startup.Reminder");
            RunGuarded(StartBackupService, "Startup.Backup");
            RunGuarded(StartReportService, "Startup.Report");
            RunGuarded(StartMindMapSyncService, "Startup.MindMapSync");

            // 联网刷新节假日：拿到后才会重建日历，放到最后以免打断首屏
            _ = RefreshHolidaysForVisibleYearAsync(force: true);
        }), DispatcherPriority.Background);
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
            configProvider: () => _config,
            hostActions: CreateHostActions());
        _mcpServer.Start(_config.McpPort);
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
        _reportService.ReportSent += text =>
        {
            // 从后台线程回调，切回 UI 线程再提示
            Dispatcher.InvokeAsync(() => ShowToast("任务完成情况报告已推送"));
        };
    }

    private void StartMindMapSyncService()
    {
        if (_viewModel is null || _mindMapSyncService is not null)
        {
            return;
        }

        _mindMapSyncService = new MindMapReviewSyncService(
            _viewModel.Data,
            _syncRoot,
            () => _config,
            OnDataChangedFromApi);
    }

    /// <summary>
    /// 设置窗口「立即同步」按钮的入口，返回同步结果文本。
    /// 传入面板里当前填写的地址 / Token：用户常常还没点保存就先点「立即同步」验证，
    /// 只按已保存配置走会拿到旧值（甚至提示"未开启同步"）。
    /// </summary>
    private async Task<string> SyncMindMapNowAsync(string baseUrl, string token)
    {
        if (_mindMapSyncService is null)
        {
            StartMindMapSyncService();
        }
        return await (_mindMapSyncService?.SyncNowAsync(baseUrl, token) ?? Task.FromResult("同步服务不可用"));
    }

    /// <summary>
    /// 任何 HTTP 服务启动前，保证 Token 已存在。
    /// 关键是 API 与 MCP 任意一个被单独开启时都要有 Token —— 否则单独开启 MCP
    /// 会让整个 /mcp 端点没有任何鉴权（历史上确实出过这个问题）。
    /// </summary>
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

        // Token 在 EnsureAuthToken 阶段已经存在，这里只是二次确认
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

        if (_apiServer.Start(_config.ApiPort))
        {
            WriteApiTokenHint();
        }
    }

    private void StartReminderService()
    {
        if (_viewModel is null)
        {
            return;
        }

        _reminderService = new ReminderService(_viewModel.Data, _syncRoot, () => _config);
    }

    private bool _apiRefreshPending;
    private readonly object _apiRefreshLock = new();

    private void OnDataChangedFromApi()
    {
        // 批量接口会在极短时间内连续写入，先合并再刷新，避免高频重建 UI 与重复落盘
        lock (_apiRefreshLock)
        {
            if (_apiRefreshPending)
            {
                return;
            }

            _apiRefreshPending = true;
        }

        // API 在后台线程修改数据，需调度回 UI 线程刷新并保存
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                await Task.Delay(250);

                if (_viewModel is not null)
                {
                    _viewModel.RebuildCalendar();
                    _viewModel.MarkDirty();
                    await SaveAsync();
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex, "OnDataChangedFromApi");
            }
            finally
            {
                // 复位必须放 finally：RebuildCalendar 一旦抛异常，标志位会永久卡 true，
                // 之后所有 API 刷新都被静默跳过。
                lock (_apiRefreshLock)
                {
                    _apiRefreshPending = false;
                }
            }
        });
    }

    private void WriteApiTokenHint()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var hintPath = System.IO.Path.Combine(appData, "MicaAgenda", "api-token.txt");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(hintPath)!);
            System.IO.File.WriteAllText(hintPath,
                $"MicaAgenda HTTP API\n端口: {_config.ApiPort}\nToken: {_config.ApiToken}\n" +
                "鉴权方式: 请求头 Authorization: Bearer <token> 或 X-Auth-Token: <token>\n");
        }
        catch
        {
            // 忽略提示文件写入失败
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        // 非显式退出：点关闭按钮只隐藏到托盘，不真正退出
        if (!_isExiting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (_viewModel is null || _closeAfterSaveRequested)
        {
            // 无可保存数据，或已完成保存：放行真正关闭
            return;
        }

        e.Cancel = true;

        RunGuarded(SaveCurrentViewBounds, "Window_Closing.SaveBounds");
        await SaveAsync();

        // 无论保存成功与否都要放行，否则窗口会永远关不掉
        _closeAfterSaveRequested = true;
        Close();
    }

    /// <summary>
    /// 点击本窗口导致系统把窗口激活并提到最前面时，立即重新压底或挂回桌面层。
    /// </summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        if (_config.EmbedDesktop)
        {
            Dispatcher.BeginInvoke(new Action(() =>
                RunGuarded(LowerToBottom, "OnActivated.Lower")),
                DispatcherPriority.Background);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        try
        {
            // 清理Toast窗口
            Helpers.ToastHelper.Cleanup();

            _gcTrimTimer?.Dispose();
            _gcTrimTimer = null;

            // 逐个兜底释放：任一服务 Dispose 抛异常都不能影响剩余资源的清理
            RunGuarded(() => _apiServer?.Dispose(), "OnClosed.ApiServer");
            _apiServer = null;
            RunGuarded(() => _mcpServer?.Dispose(), "OnClosed.McpServer");
            _mcpServer = null;
            RunGuarded(() => _reminderService?.Dispose(), "OnClosed.Reminder");
            _reminderService = null;
            RunGuarded(() => _backupService?.Dispose(), "OnClosed.Backup");
            _backupService = null;
            RunGuarded(() => _reportService?.Dispose(), "OnClosed.Report");
            _reportService = null;
            RunGuarded(() => _trayIcon?.Dispose(), "OnClosed.TrayIcon");
            _trayIcon = null;
        }
        finally
        {
            base.OnClosed(e);
        }

        // OnExplicitShutdown 模式下需显式结束进程
        if (_isExiting)
        {
            RunGuarded(() => Application.Current.Shutdown(), "OnClosed.Shutdown");
        }
    }

    /// <summary>显示窗口（从托盘恢复）。</summary>
    private void ShowWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        if (_config.EmbedDesktop)
        {
            DesktopEmbedService.EmbedToDesktop(this);
        }
    }

    /// <summary>打开设置面板。</summary>
    private void OpenSettings()
    {
        if (_viewModel is null || _backupService is null)
        {
            return;
        }

        // 报告服务可能因为启动阶段异常而没建起来，这里兜底补一次，
        // 避免设置面板拿到 null 后直接 NullReferenceException 崩掉设置流程。
        if (_reportService is null)
        {
            RunGuarded(StartReportService, "OpenSettings.StartReport");
        }

        if (_reportService is null)
        {
            return;
        }

        try
        {
            var dialog = new SettingsWindow(
                _config,
                _configStore,
                () => SafeActivePrefix(_apiServer) ?? $"http://localhost:{_config.ApiPort}",
                _backupService,
                () => SafeActivePrefix(_mcpServer) is { } p ? $"{p}mcp" : $"http://localhost:{_config.McpPort}/mcp",
                _holidayService,
                _reportService)
            {
                Owner = _config.EmbedDesktop ? null : this
            };
            dialog.ApplyRequested += OnSettingsApplied;
            dialog.TasksImported += () =>
            {
                _viewModel.RebuildCalendar();
                _ = SaveAsync();
            };
            dialog.HolidayRefreshRequested += () => _ = RefreshHolidaysForVisibleYearAsync(force: true);
            dialog.DeleteAllTasksRequested += () =>
            {
                if (_viewModel is null)
                {
                    return 0;
                }

                var removed = _viewModel.DeleteAllTasks();
                _ = SaveAsync();
                return removed;
            };
            dialog.MindMapSyncRequested += SyncMindMapNowAsync;
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            // 设置面板的任何异常都必须就地消化，绝不能冒泡成"点设置就退出"
            App.LogError(ex, "OpenSettings");
        }
    }

    /// <summary>
    /// 读取服务的首个监听前缀。服务已释放时 HttpListener.Prefixes 会抛
    /// ObjectDisposedException，这里统一兜底返回 null。
    /// </summary>
    private static string? SafeActivePrefix(object? server)
    {
        try
        {
            return server switch
            {
                TaskApiServer api => api.ActivePrefixes.FirstOrDefault(),
                McpServer mcp => mcp.ActivePrefixes.FirstOrDefault(),
                _ => null
            };
        }
        catch (Exception ex)
        {
            App.LogError(ex, "SafeActivePrefix");
            return null;
        }
    }

    /// <summary>设置面板保存后回调，重新应用运行中服务。</summary>
    private void OnSettingsApplied(AppConfig config, AppConfig previous)
    {
        // 必须用"保存前"的快照来比对：设置窗口与本类共用同一个 AppConfig 实例，
        // 保存时已经就地改写过，拿 _config 当旧值只会永远等于新值 ——
        // 于是下面这些"保存后立即生效"的分支从不执行，只能重启程序才生效。
        var oldEmbed = previous.EmbedDesktop;
        var oldLock = previous.LockWindow;
        var oldSyncEnabled = previous.SyncMyMindMapEnabled;
        var oldSyncUrl = previous.MindMapBaseUrl;
        var oldSyncToken = previous.MyMindMapToken;
        _config = config;

        RunGuarded(UpdateCloseButtonVisibility, "OnSettingsApplied.UpdateCloseButton");
        UpdateAiButton();

        // 重启 API（端口/开关/Token 可能变化）
        RunGuarded(() =>
        {
            _apiServer?.Dispose();
            _apiServer = null;
            StartApiServer();
        }, "OnSettingsApplied.RestartApi");

        // 重启 MCP 服务
        RunGuarded(() =>
        {
            _mcpServer?.Dispose();
            _mcpServer = null;
            StartMcpServer();
        }, "OnSettingsApplied.RestartMcp");

        // 重启提醒服务
        RunGuarded(() =>
        {
            _reminderService?.Dispose();
            _reminderService = null;
            StartReminderService();
        }, "OnSettingsApplied.RestartReminder");

        // 重启备份服务
        RunGuarded(() =>
        {
            _backupService?.Dispose();
            _backupService = null;
            StartBackupService();
        }, "OnSettingsApplied.RestartBackup");

        // 重启报告服务（周期 / 时间 / 渠道改了之后要立刻按新配置跑）
        RunGuarded(() =>
        {
            _reportService?.Dispose();
            _reportService = null;
            StartReportService();
        }, "OnSettingsApplied.RestartReport");

        // 桌面嵌入 / 锁定改动：保存后立即生效，不用重启。
        // （之前只是弹"将在下次启动时生效"的提示——用户勾完发现没反应，误以为坏了，
        //   而且设置保存历史上有 DialogResult 双调用 bug 会额外弹错误框，体验很差。）
        if (oldEmbed != _config.EmbedDesktop)
        {
            // 关键：把嵌入操作延迟到 SettingsWindow 完全关闭后再执行。
            // SettingsWindow 此时还是 Owner MicaAgenda 的状态，owned window 在
            // owner 关闭完成前调 SetWindowPos(HWND_BOTTOM) 会被系统忽略
            // （owned 永远画在 owner 上方，这是微软的设计）。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                RunGuarded(() =>
                {
                    if (_config.EmbedDesktop)
                    {
                        ForceLowerToBottom();
                        DesktopEmbedService.SetNoActivateStyle(this, true);
                        StartWindowWatchdog();
                    }
                    else
                    {
                        // 看门狗不能停：非嵌入模式仍需还原「显示桌面」造成的最小化。
                        // tick 内部按 _config.EmbedDesktop 决定是否压底，关掉后只保留巡检。
                        DesktopEmbedService.SetNoActivateStyle(this, false);
                        RaiseToTop();
                    }
                }, "OnSettingsApplied.Embed");
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        if (oldLock != _config.LockWindow)
        {
            RunGuarded(() =>
            {
                if (_config.LockWindow)
                {
                    DesktopEmbedService.LockWindow(this);
                }
                else
                {
                    // 解锁：恢复可调整大小（窗口默认值见 MainWindow.xaml）
                    this.ResizeMode = ResizeMode.CanResize;
                }
            }, "OnSettingsApplied.Lock");
        }

        // 刚开启同步（或改了地址 / Token）时立刻同步一次。否则要等定时器下一轮，
        // 用户会觉得"勾了没反应"：定时器首跑只在程序启动 20 秒后触发，改设置并不会重排它。
        if (_config.SyncMyMindMapEnabled
            && (!oldSyncEnabled
                || !string.Equals(oldSyncUrl, _config.MindMapBaseUrl, StringComparison.Ordinal)
                || !string.Equals(oldSyncToken, _config.MyMindMapToken, StringComparison.Ordinal)))
        {
            _ = SyncMindMapSoonAsync();
        }
    }

    /// <summary>设置保存后稍等一下再同步，留出设置窗口关闭与界面刷新的时间。</summary>
    private async Task SyncMindMapSoonAsync()
    {
        try
        {
            await Task.Delay(1200);
            var result = await SyncMindMapNowAsync(_config.MindMapBaseUrl, _config.MyMindMapToken);
            if (result.Contains("同步完成", StringComparison.Ordinal))
            {
                // 从后台线程回调，切回 UI 线程再提示
                _ = Dispatcher.InvokeAsync(() => ShowToast(result));
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex, "OnSettingsApplied.SyncMindMapSoon");
        }
    }

    /// <summary>执行一段可能抛异常的逻辑并兜底记录日志，避免设置相关的异常中断调用链。</summary>
    private static void RunGuarded(Action action, string context)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            App.LogError(ex, context);
        }
    }

    /// <summary>真正退出程序。</summary>
    private void ExitApplication()
    {
        _isExiting = true;
        Close();
    }

    /// <summary>
    /// 右上角的「×」在任何状态下都不显示。这是个长期挂在桌面上的小挂件，
    /// 误点关闭会让人以为程序退出了；旧版只在勾了"锁定位置"时才隐藏，用户要求一律去掉。
    /// 需要收起窗口走托盘菜单的「隐藏到托盘」，退出走托盘菜单的「退出」。
    /// </summary>
    private void UpdateCloseButtonVisibility()
    {
        CloseButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>AI 顶栏按钮只在「启用 AI 助手」时显示；关闭时同时收起可能还开着的悬浮层。</summary>
    private void UpdateAiButton()
    {
        if (AiButton is null)
        {
            return;
        }

        AiButton.Visibility = _config.AiEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (!_config.AiEnabled)
        {
            AiButton.IsChecked = false;
        }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 最小尺寸硬夹取。WPF 的 Window.MinWidth 在有原生边框时通常有效，但缩放热区
        // （ResizeGrip）走的是我们自己算的 DragDelta，边界值可能被绕过，这里再兜一道，
        // 保证「所有设置按钮都可见」的那条线无论如何都守得住（与 Avalonia 宿主同口径）。
        if (e.NewSize.Width > 0 && Width < MinWindowWidth)
        {
            Width = MinWindowWidth;
        }

        if (e.NewSize.Height > 0 && Height < MinWindowHeight)
        {
            Height = MinWindowHeight;
        }

        ApplyShellClip();
        UpdateYearScrollLimit();
        UpdateResponsiveLayout();
    }

    /// <summary>
    /// 顶栏 + 主体的自适应版式，按窗口宽度分三档降级（与 Avalonia 宿主 UpdateResponsiveLayout 同口径）：
    /// <list type="number">
    ///   <item>宽度够：单行展示，日期信息 + 背景/透明度 + 今天/视图/设置 全在。</item>
    ///   <item>放不下：先收起「背景 + 透明度」这组装饰设置，保证常驻按钮不压在设置项上。</item>
    ///   <item>还是放不下：换行 —— 常驻按钮整体挪到第二行，第一行只留日期信息。</item>
    /// </list>
    /// 换行档是最后一档，再窄就没有可牺牲的东西了：窗口本身有硬下限 <see cref="MinWindowWidth"/>
    /// （= <c>Window.MinWidth</c>），拖到那儿就停住，保证所有设置按钮任何宽度下都可见、不被遮挡。
    /// 另外窗口窄到 <see cref="NarrowLayoutThreshold"/> 时，主体只留「今日任务」这一块。
    /// </summary>
    /// <summary>
    /// 清掉上一轮给顶栏按钮加的宽度补偿（见 UpdateResponsiveLayout 末尾的均摊）。
    /// 补偿值会直接改变按钮组总宽，不清掉的话下一轮的密度测量与换行判断都会带上它而失真。
    /// </summary>
    private void ResetToolbarWidthPadding()
    {
        if (ToolbarActionPanel is null)
        {
            return;
        }

        foreach (var button in EnumerateVisual<Button>(ToolbarActionPanel))
        {
            button.MinWidth = 0;
        }
    }

    /// <summary>
    /// 顶栏按钮的三档密度：0 = 默认，1 = 紧凑，2 = 超紧凑（与 Avalonia 宿主同思路）。
    ///
    /// 这些入口在窄窗下不隐藏 —— 窄屏恰恰就是任务视图：藏掉「设置」用户就没法改配置、
    /// 藏掉「AI」就调不出对话。所以按可用宽度逐级缩字号与内边距，实在放不下才换行。
    /// 直接写局部值（而非切 Style）：按钮上的 Padding 本来就是局部值，层级一致才盖得住。
    /// </summary>
    private void SetToolbarDensity(int level)
    {
        if (ToolbarGrid is null)
        {
            return;
        }

        var (fontSize, padH, padV) = level switch
        {
            2 => (9.0, 3.5, 1.0),
            1 => (10.0, 5.0, 2.0),
            _ => (11.5, 7.0, 3.0)
        };

        foreach (var button in EnumerateVisual<Button>(ToolbarGrid))
        {
            button.FontSize = fontSize;
            button.Padding = new Thickness(padH, padV, padH, padV);
        }
    }

    /// <summary>深度优先枚举视觉树里指定类型的元素（WPF 没有 Avalonia 的 GetVisualDescendants）。</summary>
    private static IEnumerable<T> EnumerateVisual<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var nested in EnumerateVisual<T>(child))
            {
                yield return nested;
            }
        }
    }

    private void UpdateResponsiveLayout()
    {
        if (NormalViewHost is null || NarrowTaskOnlyView is null || ViewControlsPanel is null
            || ToolbarActionPanel is null || DateInfoPanel is null)
        {
            return;
        }

        var width = ActualWidth;
        // 窄窗退化为「只留任务面板」—— 但周视图除外：
        // 周视图本身就是「左列日期格子 + 右侧任务面板」的窄布局，窄窗下依然可用。
        // 若把它也强制成任务面板，用户在窄窗里点「周视图」就会像"没反应"
        //（切换其实生效了，只是立刻被这条规则盖回任务面板）。
        var narrowView = width > 0 && width <= NarrowLayoutThreshold
                         && _viewModel?.Settings.ViewMode != CalendarViewMode.Week;

        // ===== 用「实际测量」而不是「拍脑袋常量」来决定档位 =====
        //
        // 旧版用 width >= NarrowLayoutThreshold + ViewControlsWidthCost（460+262=722）这种常量判据，
        // 跟真实内容宽度对不上：字体、DPI 一变阈值就失准，表现就是"该隐藏时不隐藏、该显示时已经藏了"。
        // 改成先按最小自然宽度实测「日期信息」和「常驻按钮」，再加装饰设置的固定开销。
        // 测量前先把上一轮隐藏的面板复原，否则被隐藏的元素量出来是 0，就再也放不回来了。
        ViewControlsPanel.Visibility = Visibility.Visible;
        TodayCountText.Visibility = Visibility.Visible;
        WeekCountText.Visibility = Visibility.Visible;

        // 复位上一轮给按钮加的宽度补偿：它会直接改变按钮组总宽，
        // 不清掉的话下面的密度测量与档位判断都会带上它而失真。
        ResetToolbarWidthPadding();

        var available = width > 0 ? width - ToolbarHorizontalChrome : double.PositiveInfinity;
        var needsWrap = false;
        if (double.IsFinite(available))
        {
            // 顶栏按钮先逐级降密度（收缩字号与内边距），三档都不够才换行。
            // 这些入口在窄屏下都是必需的 —— 窄屏正是任务视图：藏掉「设置」就改不了配置、
            // 藏掉「AI」就调不出对话、藏掉「周期」就加不了周期任务。所以只能缩，不能藏。
            needsWrap = true;
            for (var density = 0; density <= 2; density++)
            {
                SetToolbarDensity(density);
                var essential = MeasurePanelWidth(DateInfoPanel, exclude: ViewControlsPanel)
                                + MeasurePanelWidth(ToolbarActionPanel);
                if (essential <= available)
                {
                    needsWrap = false;
                    break;
                }
            }
        }

        // ③ 档：连「日期信息 + 常驻按钮」都摆不下 → 换行，常驻按钮搬到第二行
        var wrap = needsWrap;
        Grid.SetRow(ToolbarActionPanel, wrap ? 1 : 0);
        Grid.SetColumn(ToolbarActionPanel, wrap ? 0 : 1);
        Grid.SetColumnSpan(ToolbarActionPanel, wrap ? 2 : 1);
        ToolbarActionPanel.HorizontalAlignment =
            wrap ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        ToolbarActionPanel.Margin = wrap
            ? new Thickness(0, 4, 0, 0)
            : new Thickness(0);

        // 换行后按钮行独占整行，但水平 StackPanel 是从左往右排的、末尾会剩一段空白，
        // 最右的「设置」就比下方内容区的右边框短一截。这里把余量**均摊到每个按钮**上
        //（而不是只撑某一个 —— 那会变成一个突兀的长条），让整行宽度正好等于内容区宽度。
        // 只在换行档做：不换行时按钮跟在日期右侧，右边缘本来就贴着内容区。
        if (wrap && ToolbarActionPanel is not null && ToolbarGrid is not null && ToolbarGrid.ActualWidth > 0)
        {
            var buttons = EnumerateVisual<Button>(ToolbarActionPanel).ToList();
            var extraPerButton = (ToolbarGrid.ActualWidth - MeasurePanelWidth(ToolbarActionPanel)) / Math.Max(1, buttons.Count);
            if (extraPerButton > 0.5)
            {
                foreach (var button in buttons)
                {
                    button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    button.MinWidth = button.DesiredSize.Width + extraPerButton;
                }
            }
        }

        // ① / ② 档：背景 + 透明度是否还放得下。
        // 换行之后第一行只需容纳日期信息本身，所以按换行后的实际情况再量一次。
        if (double.IsFinite(available))
        {
            // 第一行净需求 = 日期信息（不含「背景+透明度」）+ 常驻按钮（换行时按钮在第二行）。
            // 再单独量这组装饰设置的真实宽度，二者相加与可用宽度比较 —— 全程真量。
            // （旧版把 ViewControlsWidthCost 常量加在已经含了 ViewControlsPanel 的测量值上，
            //   等于多算了 262px，用户得把窗口拉得特别开才看得到亮度条。）
            var firstRowNeed = wrap
                ? MeasurePanelWidth(DateInfoPanel, exclude: ViewControlsPanel)
                : MeasurePanelWidth(DateInfoPanel, exclude: ViewControlsPanel) + MeasurePanelWidth(ToolbarActionPanel);
            var viewControlsWidth = MeasurePanelWidth(ViewControlsPanel);

            if (firstRowNeed + viewControlsWidth > available)
            {
                ViewControlsPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                // 背景/透明度保住了，今日 / 本周计数这组次要信息才让位。
                var countsCost = MeasurePanelWidth(TodayCountText, exclude: null)
                                 + MeasurePanelWidth(WeekCountText, exclude: null);
                if (firstRowNeed + viewControlsWidth + countsCost > available)
                {
                    TodayCountText.Visibility = Visibility.Collapsed;
                    WeekCountText.Visibility = Visibility.Collapsed;
                }
            }
        }

        NormalViewHost.Visibility = narrowView ? Visibility.Collapsed : Visibility.Visible;
        NarrowTaskOnlyView.Visibility = narrowView ? Visibility.Visible : Visibility.Collapsed;

        // 窄屏下主体被换成任务面板（与 ViewMode 无关了），顶栏下拉的标签也要跟着改口，
        // 否则会出现「画面是任务列表、按钮却写着周视图」这种自相矛盾的状态。
        if (_viewModel is not null)
        {
            _viewModel.IsNarrowTaskOnly = narrowView;
        }

        // ===== 周视图：正方形格子的边长 =====
        UpdateWeekCellSize();

        // ===== 年视图：日期数字字号随月卡实际列宽缩放 =====
        UpdateYearFontScale();
    }

    /// <summary>
    /// 按年视图月卡的<b>实际列宽</b>回填日期数字字号（与 Avalonia 宿主同一口径）。
    ///
    /// 年视图一行 3 张月卡、每卡 7 列：窗口缩小时列宽跟着缩小，字号若不同步缩小，
    /// 两位日期数字会被右侧的节日徽标盖住或直接裁掉（用户实测"缩小之后数字看不清了"）。
    /// 从 YearScrollViewer 的实际宽度反推列宽，除以「基准列宽 40px」得到缩放系数交给
    /// <see cref="MicaAgenda.App.ViewModels.MainViewModel.SetYearFontScale"/>
    /// （内部夹取 0.62~1.0、量化 0.02 步进，避免拖窗口时连续通知与重排）。
    /// </summary>
    private void UpdateYearFontScale()
    {
        if (_viewModel is null || YearScrollViewer is null)
        {
            return;
        }

        // YearScrollViewer → 三列 UniformGrid；卡片 Margin 4×2 ×3 列；
        // 卡片 Padding 6×2 + 边框 1×2；剩下的就是一列的可用宽度。
        var gridWidth = YearScrollViewer.ActualWidth - 12; // 滚动条预留 ~12
        // 必须写 !(x > 0)：切视图首帧 ActualWidth 可能是 NaN，而 NaN <= 0 恒为 false，
        // 用 <= 判会漏过它，一路算成 NaN 的缩放系数，最终让年视图整片日期数字不渲染。
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

    /// <summary>顶栏左右内边距 + 列间距的固定开销。</summary>
    private const double ToolbarHorizontalChrome = 24.0;

    /// <summary>
    /// 量出一个面板在当前内容下的「自然宽度」（与 Avalonia 宿主同一口径）。
    /// 隐藏状态下 WPF 量出来是 0，所以调用前必须先把要测的面板置可见。
    /// </summary>
    private static double MeasurePanelWidth(FrameworkElement? panel, FrameworkElement? exclude = null)
    {
        if (panel is null)
        {
            return 0;
        }

        var excludedVisibility = Visibility.Visible;
        if (exclude is not null)
        {
            excludedVisibility = exclude.Visibility;
            exclude.Visibility = Visibility.Collapsed;
        }

        try
        {
            panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return panel.DesiredSize.Width;
        }
        finally
        {
            if (exclude is not null)
            {
                exclude.Visibility = excludedVisibility;
            }
        }
    }

    /// <summary>
    /// 计算并回填周视图格子的高度（与 Avalonia 宿主同一口径）。
    ///
    /// 只算高度：按「左栏可用高度 ÷ 7」求出让 7 个格子刚好铺满界面高度的值。
    /// 宽度固定为 <see cref="MainViewModel.WeekScrollColumnWidth"/>，窗口缩放时左栏不变，
    /// 多出来的空间全部给右侧今日任务面板。
    ///
    /// 夹到 44~96：下限保证日期与任务看得清，上限避免格子高高拉起后内部空荡。
    /// </summary>
    private void UpdateWeekCellSize()
    {
        if (_viewModel is null)
        {
            return;
        }

        var topBarHeight = ToolbarPanel?.ActualHeight ?? 0;
        var bodyHeight = ActualHeight - topBarHeight - WeekViewVerticalMargin;
        if (bodyHeight <= 0)
        {
            // 首帧尺寸还没准备好，等下一次 SizeChanged 再写。
            return;
        }

        const double rowSpacing = 4.0;
        const int visibleRows = 7;
        var rowHeight = (bodyHeight - (rowSpacing * visibleRows)) / visibleRows;
        _viewModel.WeekScrollCellHeight = Math.Clamp(rowHeight, 44, 96);
    }

    /// <summary>周视图区域的上下外边距之和（左栏 Margin + 根 Grid Margin）。</summary>
    private const double WeekViewVerticalMargin = 32.0;

    /// <summary>
    /// 周视图左栏滚动：滚到接近底部时追加后续日期，让用户能一直往未来翻。
    /// 判据用「距底部不足一屏的 1/3」预加载，避免滚到底才追加导致的位置跳动。
    /// </summary>
    private void WeekScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_viewModel is null || sender is not ScrollViewer viewer)
        {
            return;
        }

        // 程序自身的定位（点「今天」/ 切视图归位）也会触发 ScrollChanged。
        // 那种情况下不能再追加日期：追加会往列表尾部塞 28 个新容器，布局在滚动还没稳定时
        // 整批重算，用户感觉到的是「点了今天要卡 1~2 秒」。
        if (_suppressWeekAutoExtend)
        {
            return;
        }

        var remaining = viewer.ExtentHeight - viewer.VerticalOffset - viewer.ViewportHeight;
        var threshold = Math.Max(120, viewer.ViewportHeight / 3);
        if (remaining > threshold)
        {
            return;
        }

        _viewModel.ExtendWeekScroll();
    }

    /// <summary>
    /// 抑制「滚到接近底部就追加日期」的闸门。<see cref="ScrollWeekToToday"/> 在赋值偏移
    /// 前后把它置位，避免程序化滚动被误判成用户滚到底。
    /// </summary>
    private bool _suppressWeekAutoExtend;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ClearActiveDayCell();
        }
    }

    private void OnTimelineRebuilt()
    {
        Dispatcher.BeginInvoke(new Action(ScrollTimelineToAnchor), DispatcherPriority.Loaded);
    }

    private void ScrollTimelineToAnchor()
    {
        if (_viewModel is null)
        {
            return;
        }

        // 周视图左栏是可无限上下的滚动长列表，"现在看的是哪一段"同样由滚动位置表达。
        // 用户翻到几周之后时点「今天」，必须把列表滚回今天所在那一周，
        // 否则日期数据虽然回到了今天、画面却还停在原处，看起来就是"点了没反应"。
        if (_viewModel.Settings.ViewMode == CalendarViewMode.Week)
        {
            ScrollWeekToToday();
            return;
        }

        if (MonthScrollViewer is null || _viewModel.Settings.ViewMode != CalendarViewMode.Month)
        {
            return;
        }

        var anchor = _viewModel.TimelineAnchor;
        var block = _viewModel.TimelineMonths.FirstOrDefault(b => b.Year == anchor.Year && b.Month == anchor.Month);
        if (block is null)
        {
            return;
        }

        ScrollBlockIntoView(block, 0);
    }

    /// <summary>
    /// 把周视图左栏滚回"今天所在那一周"（与 Avalonia 宿主同一口径）。
    ///
    /// 左栏是一维日期长列表，定位就是「目标行索引 × 行高」。用行高算偏移而不是
    /// ScrollIntoView：后者要先把容器虚拟化出来才拿得到，首帧/刚切视图时会静默失败。
    /// 目标是今天所在周的**第一行**（周日），让整周完整进视口。
    /// </summary>
    private void ScrollWeekToToday()
    {
        if (WeekScrollViewer is null || _viewModel is null)
        {
            return;
        }

        var todayIndex = _viewModel.IndexOfTodayInWeekScroll;
        if (todayIndex < 0)
        {
            // 兜底：列表起点就是本周周日，滚回顶部等同于"回到最近 7 天"。
            ApplyWeekOffset(0);
            return;
        }

        var weekStartIndex = todayIndex - (todayIndex % 7);
        var rowSpan = _viewModel.WeekScrollCellHeight + WeekDayRowBottomMargin;
        var target = weekStartIndex * rowSpan;

        var maxOffset = Math.Max(0, WeekScrollViewer.ExtentHeight - WeekScrollViewer.ViewportHeight);
        ApplyWeekOffset(Math.Clamp(target, 0, maxOffset));
    }

    /// <summary>
    /// 带闸门地设置周视图左栏的滚动偏移。
    ///
    /// 闸门关掉的这段时间里 <see cref="WeekScroll_ScrollChanged"/> 不会追加日期 —— 否则
    /// 程序化滚动会被当成"用户滚到底"，在滚动还没稳定时往尾部塞 28 个容器，
    /// 布局整批重算，表现为点「今天」后卡顿 1~2 秒。
    ///
    /// 用 <c>DispatcherPriority.Background</c> 复位而不是立即复位：
    /// ScrollToVerticalOffset 引发的 ScrollChanged 是异步派发的，立刻放开等于没拦。
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
            WeekScrollViewer.ScrollToVerticalOffset(offset);
        }
        finally
        {
            Dispatcher.BeginInvoke(new Action(() => _suppressWeekAutoExtend = false),
                DispatcherPriority.Background);
        }
    }

    /// <summary>周视图日期行自带的下外边距（见 WeekDayRowTemplate 的 Margin="0,0,0,4"）。</summary>
    private const double WeekDayRowBottomMargin = 4.0;

    // 点击"今天"后滚动回今日所在月份块
    private void ScrollToToday()
    {
        if (_viewModel is null || MonthScrollViewer is null)
        {
            return;
        }

        if (_viewModel.Settings.ViewMode == CalendarViewMode.Year)
        {
            ScrollYearToToday();
            return;
        }

        if (_viewModel.Settings.ViewMode != CalendarViewMode.Month)
        {
            return;
        }

        var today = _viewModel.Today;
        var block = _viewModel.TimelineMonths.FirstOrDefault(b => b.Year == today.Year && b.Month == today.Month);
        if (block is null)
        {
            // 今日不在当前时间轴，重建到今日（重建后会自动滚动定位）
            _viewModel.GoToday();
            return;
        }

        // 容器可能尚未生成，延后到布局完成再滚动
        Dispatcher.BeginInvoke(new Action(() => ScrollBlockIntoView(block, 0)), DispatcherPriority.Loaded);
    }

    private void ScrollYearToToday()
    {
        if (_viewModel is null || YearScrollViewer is null || YearItemsControl is null)
        {
            return;
        }

        var today = _viewModel.Today;
        var target = _viewModel.YearMonths.FirstOrDefault(month => month.Month == today.Month);
        if (target is null)
        {
            return;
        }

        var container = YearItemsControl.ItemContainerGenerator.ContainerFromItem(target) as FrameworkElement;
        if (container is null)
        {
            // 年视图刚切换/布局尚未生成时，延后到下一布局帧再滚。
            Dispatcher.BeginInvoke(new Action(ScrollYearToToday), DispatcherPriority.Loaded);
            return;
        }

        var point = container.TransformToVisual(YearItemsControl).Transform(new Point(0, 0));
        YearScrollViewer.ScrollToVerticalOffset(Math.Max(0, point.Y - 8));
    }

    /// <summary>
    /// 滚动到指定月份块，使其顶边对齐视口（可留一段顶部留白）。
    /// 像素滚动下需按容器实际位置计算；容器尚未生成时返回 false，调用方应延后重试。
    /// </summary>
    private bool ScrollBlockIntoView(MonthBlockViewModel block, double topPadding)
    {
        if (MonthScrollViewer is null || MonthItemsControl is null)
        {
            return false;
        }

        var container = MonthItemsControl.ItemContainerGenerator.ContainerFromItem(block) as FrameworkElement;
        if (container is null)
        {
            return false;
        }

        // 直接按内容偏移滚动，避免触发 RequestBringIntoView（否则会引发自动滚动/跳动）
        var point = container.TransformToVisual(MonthItemsControl).Transform(new Point(0, 0));
        ScrollToOffsetProgrammatically(point.Y - topPadding);
        return true;
    }

    /// <summary>
    /// 以程序方式滚动到指定位置，并抑制由此引发的自动扩展。
    /// 参数在月视图下是"逻辑偏移"（已滚过的月份块数，小数表示部分滚动）。
    /// </summary>
    private void ScrollToOffsetProgrammatically(double targetOffset)
    {
        if (MonthScrollViewer is null)
        {
            return;
        }

        _programmaticScroll = true;
        MonthScrollViewer.ScrollToVerticalOffset(Math.Max(0, targetOffset));
        // 必须在下一帧无条件复位，不能依赖 ScrollChanged 去消费标记：
        // 目标偏移与当前相同时 ScrollToVerticalOffset 根本不会触发 ScrollChanged，
        // 标记就会残留，之后用户滚动被误判，进而反复触发时间轴扩展。
        Dispatcher.BeginInvoke(new Action(() => _programmaticScroll = false), DispatcherPriority.Loaded);
    }

    private void MonthScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {

        // 程序内部滚动定位（按月吸附 / 跳今天 / 锚定）不触发自动扩展
        if (_programmaticScroll)
        {
            return;
        }

        // 锁定滚动模式：时间轴完全冻结，既不自动扩展也不重建（避免"一直刷新"）
        if (_lockScroll)
        {
            return;
        }

        if (_viewModel is null || MonthScrollViewer is null || MonthItemsControl is null)
        {
            return;
        }

        if (_viewModel.Settings.ViewMode != CalendarViewMode.Month)
        {
            return;
        }

        // 内容不足以滚动时不做扩展：此时 VerticalOffset 恒为 0，
        // 会一直满足"到达顶部"的条件而反复扩展。
        if (e.ExtentHeight <= e.ViewportHeight + 1)
        {
            return;
        }


        // 内容高度变化且没有实际滚动偏移变化（数据刷新引起的通知）时不扩展。
        // 注意：不能只看 ExtentHeightChange——扩展后内容高度必然变化，
        // 但用户仍在滚动（VerticalChange != 0），此时应继续检查边缘条件，
        // 否则扩展后 ScrollChanged 再次触发时会被误拦住，时间轴永远扩不动。
        if (Math.Abs(e.ExtentHeightChange) > 0.01 && Math.Abs(e.VerticalChange) < 0.01)
        {
            _pendingExtendDir = 0;
            return;
        }

        // 没有实际的滚动偏移变化（如纯刷新引起的通知）也不扩展
        if (Math.Abs(e.VerticalChange) < 0.01)
        {
            return;
        }

        // 冷却：扩展会改变内容高度并再次引发 ScrollChanged，冷却防止布局变化引发连锁扩展。
        if (DateTime.Now - _lastTimelineExtendAt < TimelineExtendCooldown)
        {
            return;
        }


        if (e.VerticalOffset <= TimelineLoadThreshold)
        {
            // 用户拖到顶部：向前（更早）加载更多月份
            ExtendTimeline(-1);
        }
        else if (e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - TimelineLoadThreshold)
        {
            // 用户拖到底部：向后（更晚）加载更多月份
            ExtendTimeline(1);
        }
        else
        {
            // 离开边缘：撤销尚未兑现的暂存扩展请求，避免之后无谓地自动扩
            _pendingExtendDir = 0;
        }
    }

    /// <summary>
    /// 在时间轴的一端追加一个月，并把画面校正回原来停留的位置。
    /// 这是时间轴扩展的**唯一入口**：滚轮翻月也必须走这里，
    /// 否则两条路径各插各的月份块，索引对不上就会出现"往上滑却跳到去年"的怪象。
    /// </summary>
    /// <param name="direction">-1 向前（更早），1 向后（更晚）。</param>
    /// <param name="userInitiated">
    /// 是否由用户主动操作触发（滚轮翻月）。用户动作不受冷却限制，
    /// 否则快速滚轮时翻月请求会被吞掉，表现成"滚动很慢、翻不动"。
    /// </param>
    private void ExtendTimeline(int direction, bool userInitiated = false)
    {
        if (_viewModel is null || _viewModel.TimelineMonths.Count == 0 || MonthScrollViewer is null)
        {
            return;
        }

        // 注意：这里不再做 TimelineMaxMonths 的硬拦截。
        // 时间轴上限由 ViewModel.TrimTimeline 在每次追加后从反方向裁掉来保证
        // （始终保持 <= TimelineMaxMonths 块，内存有界），同时窗口可以"滑动"到任意月份。
        // 旧的硬拦截会让时间轴一旦攒满 12 块就彻底冻结、再也滚不动（卡在 11 月）。

        // 冷却只约束"滚到边缘自动预加载"，防止布局变化引发连锁扩展。
        // 被冷却拦下的请求要暂存，待冷却结束后补做——否则用户停在边缘时不再产生
        // ScrollChanged，时间轴会彻底卡死、再也往下扩不动。
        if (!userInitiated && DateTime.Now - _lastTimelineExtendAt < TimelineExtendCooldown)
        {
            _pendingExtendDir = direction;
            ScheduleExtendRetry();
            return;
        }

        PerformTimelineExtend(direction);
    }

    /// <summary>同步扩展一次时间轴。扩展是即时完成的，无需互斥；用户拖拽期间也不会被饿死。</summary>
    private void PerformTimelineExtend(int direction)
    {
        if (_viewModel is null || MonthScrollViewer is null || MonthItemsControl is null)
        {
            return;
        }

        _lastTimelineExtendAt = DateTime.Now;

        // 以扩展方向的端点月份为锚，扩展后把偏移精确还原到原本停留的位置。
        var anchor = direction < 0 ? _viewModel.TimelineMonths[0] : _viewModel.TimelineMonths[^1];
        var previousOffset = MonthScrollViewer.VerticalOffset;
        var monthsBefore = _viewModel.TimelineMonths.Count;

        if (direction < 0)
        {
            _viewModel.ExtendTimelineBack();
        }
        else
        {
            _viewModel.ExtendTimelineForward();
        }

        var monthsAfter = _viewModel.TimelineMonths.Count;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (MonthScrollViewer is null || MonthItemsControl is null)
                {
                    return;
                }

                // 扩展后锚点容器已重排；向前扩展时锚点位下移，需把新位置偏移还原。
                var container = MonthItemsControl.ItemContainerGenerator.ContainerFromItem(anchor) as FrameworkElement;
                if (container is null)
                {
                    return;
                }

                var point = container.TransformToVisual(MonthItemsControl).Transform(new System.Windows.Point(0, 0));
                var target = direction < 0 ? point.Y + previousOffset : previousOffset;
                ScrollToOffsetProgrammatically(target);
            }
            catch (Exception ex)
            {
                App.LogError(ex, "PerformTimelineExtend");
            }
        }), DispatcherPriority.Loaded);
    }

    /// <summary>安排一次冷却结束后的补做（一次性定时器）。</summary>
    private void ScheduleExtendRetry()
    {
        if (_extendRetryTimer is null)
        {
            _extendRetryTimer = new DispatcherTimer { Interval = TimelineExtendCooldown };
            _extendRetryTimer.Tick += (_, _) =>
            {
                _extendRetryTimer.Stop();
                var dir = _pendingExtendDir;
                _pendingExtendDir = 0;
                if (dir != 0)
                {
                    ExtendTimeline(dir);
                }
            };
        }

        if (!_extendRetryTimer.IsEnabled)
        {
            _extendRetryTimer.Start();
        }
    }

    private void MonthScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 锁定滚动模式：滚轮只在选中格子的任务列表内滚动，不翻动整个月份
        if (_lockScroll)
        {
            var activeCell = ActiveDayCellBorder;
            if (activeCell is not null)
            {
                var innerScroll = FindVisualChildren<ScrollViewer>(activeCell)
                    .FirstOrDefault(sv => sv != MonthScrollViewer && sv.ExtentHeight > sv.ViewportHeight);
                if (innerScroll is not null)
                {
                    var offset = innerScroll.VerticalOffset - e.Delta / 120.0 * 30;
                    innerScroll.ScrollToVerticalOffset(Math.Max(0, Math.Min(offset, innerScroll.ExtentHeight - innerScroll.ViewportHeight)));
                    e.Handled = true;
                    return;
                }
            }

            e.Handled = true;
            return;
        }

        // 普通模式：滚轮累积 delta，达到阈值才翻月，避免一次滚轮跳一整月
        e.Handled = true;
        var direction = e.Delta > 0 ? -1 : 1;

        // 方向改变时重置累积
        if (_scrollAccumulatedDelta != 0 && Math.Sign(_scrollAccumulatedDelta) != direction)
        {
            _scrollAccumulatedDelta = 0;
        }

        _scrollAccumulatedDelta += direction * Math.Abs(e.Delta);

        if (Math.Abs(_scrollAccumulatedDelta) >= ScrollDeltaThreshold)
        {
            _scrollAccumulatedDelta = 0;
            ScrollToAdjacentMonth(direction);
        }
    }

    /// <summary>年视图滚轮：锁定滚动时吞掉事件，允许自然滚动。</summary>
    private void YearScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_lockScroll)
        {
            e.Handled = true;
            return;
        }
    }

    /// <summary>找到当前视口顶部所在的月份块。</summary>
    private MonthBlockViewModel? GetMonthBlockAtOffset(double offset)
    {
        if (_viewModel is null || MonthItemsControl is null)
        {
            return null;
        }

        MonthBlockViewModel? result = null;
        var bestTop = double.NegativeInfinity;
        foreach (var block in _viewModel.TimelineMonths)
        {
            var container = MonthItemsControl.ItemContainerGenerator.ContainerFromItem(block) as FrameworkElement;
            if (container is null)
            {
                continue;
            }

            var top = container.TransformToVisual(MonthItemsControl).Transform(new Point(0, 0)).Y;
            if (top <= offset + 0.5 && top > bestTop)
            {
                bestTop = top;
                result = block;
            }
        }

        return result ?? _viewModel.TimelineMonths.FirstOrDefault();
    }

    /// <summary>按月份整块滚动：dir=-1 上一月(更早)，dir=1 下一月(更晚)。必要时自动扩展时间轴。</summary>
    private void ScrollToAdjacentMonth(int dir)
    {
        if (_viewModel is null || MonthScrollViewer is null)
        {
            return;
        }

        var current = GetMonthBlockAtOffset(MonthScrollViewer.VerticalOffset + MonthScrollViewer.ViewportHeight / 2);
        if (current is null)
        {
            return;
        }

        var index = _viewModel.TimelineMonths.IndexOf(current);
        var targetIndex = index + dir;


        if (targetIndex < 0 || targetIndex >= _viewModel.TimelineMonths.Count)
        {
            // 到边界了：必须走 ExtendTimeline 这条唯一入口去加载新月份。
            // 早先这里自己调 ExtendTimelineBack/Forward，与 ScrollChanged 的自动扩展形成
            // 两条竞争路径，两边各插各的块，索引错乱就会"往上滑却跳到去年"。
            // 注意：不再做 TimelineMaxMonths 硬拦截，允许窗口"滑动"到任意月份
            // （上限由 ViewModel.TrimTimeline 在追加后裁掉来保证）。

            ExtendTimeline(dir, userInitiated: true);
            // 扩展完成后再由下一帧定位到新月份：让 ExtendTimeline 先把偏移校正完
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_viewModel is null || MonthItemsControl is null)
                {
                    return;
                }

                var next = _viewModel.TimelineMonths.IndexOf(current) + dir;
                if (next < 0 || next >= _viewModel.TimelineMonths.Count)
                {
                    return;
                }

                ScrollBlockIntoView(_viewModel.TimelineMonths[next], 0);
            }), DispatcherPriority.Loaded);
            return;
        }

        ScrollBlockIntoView(_viewModel.TimelineMonths[targetIndex], 0);
    }

    /// <summary>
    /// 内层任务列表 ScrollViewer 的 PreviewMouseWheel 处理器。
    /// 当内层无可滚动内容时，吞掉事件并手动转发给 MonthScrollViewer，确保月份视图能正常滚动。
    /// 当内层有可滚动内容时，不做处理，让内层 ScrollViewer 的内置处理器接管。
    /// </summary>
    private void TaskListScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv && sv.ExtentHeight <= sv.ViewportHeight)
        {
            // 内层无可滚动内容：吞掉此事件，阻止内层 ScrollViewer.OnMouseWheel 标记 Handled
            e.Handled = true;
            // 手动将滚轮事件转发给外层 MonthScrollViewer
            var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent
            };
            MonthScrollViewer.RaiseEvent(forwarded);
        }
    }

    private void MonthScrollViewer_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 仅在点击滚动条时标记"用户滚动"，点击内容区域不应触发时间轴自动扩展
    }

    private void MonthScrollViewer_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
    }

    // 抑制因焦点/控件进入可视区域而触发的自动滚动（导致界面自己滚动）
    private void MonthScrollViewer_RequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        e.Handled = true;
    }

    private void Shell_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyShellClip();
        UpdateYearScrollLimit();
    }

    /// <summary>
    /// 启动窗口外观看门狗。SourceInitialized 与 Loaded 都会尝试启动，
    /// 这里做幂等保护——否则定时器会被订阅两次，每轮重复调用 SetWindowPos，
    /// 还会白白多持有一份窗口引用。
    ///
    /// 不管是否开启嵌入桌面都常驻：嵌入模式下每 tick 强制压底；任何模式下都要把被
    /// 「显示桌面」（Win+D / 任务栏右键 / 右下角细条）最小化的窗口无声还原——本程序
    /// 没有任务栏按钮，被最小化后用户没有入口找回。
    /// </summary>
    private bool _embedWatchdogHooked;
    private IntPtr _embedHandle;
    private void StartWindowWatchdog()
    {
        // Tick 只挂载一次：嵌入桌面可能被反复开关（设置里勾选/取消），
        // 若每次 Start 都挂一次，会累积多个 Tick 处理器重复压底。
        if (!_embedWatchdogHooked)
        {
            _embedWatchdogHooked = true;
            _embedWatchdog.Tick += (_, _) => ReinforceWindowChrome();

            // 前台窗口一变（含「显示桌面」把桌面提为前台）就立刻纠一次 Z 序，
            // 这样窗口被桌面层盖住的瞬间就能回来，不用等 2s 的下一轮 tick。
            DesktopEmbedService.InstallForegroundWatch(() => ReinforceWindowChrome());
        }

        _embedWatchdog.Start();
    }

    /// <summary>
    /// 把窗口外观重新拉回「桌面小部件」该有的样子（幂等，随时可重复调用）。
    /// 由看门狗低频 tick 与前台事件钩子共同驱动。
    /// </summary>
    private void ReinforceWindowChrome()
    {
        try
        {
            // ===== 免疫「显示桌面」：摘样式 + 还原，且必须"摘 → 还原 → 再摘" =====
            //
            //  ① 摘掉 WS_MINIMIZEBOX，让批量最小化从源头跳过本窗口；
            //  ② 万一还是被带走（最小化 或 WS_VISIBLE 被清零），无激活还原。
            //
            // 关键在 ① 要做两次：实测发现还原用的 SW_RESTORE 会连带重建非客户区，
            // 把刚摘掉的 WS_MINIMIZEBOX 又写回来 —— 于是下一轮 tick 窗口又是"可最小化"的，
            // 系统随时能在还原的缝里再收一次（表现为反复消失）。所以还原后必须再摘一遍。
            // 样式会被宿主/系统改回去，所以每个 tick 都要重放。
            // 托盘「隐藏」会先打开 SuppressAutoRestore 闸门，不会被误伤。
            DesktopEmbedService.StripMinimizeBox(this);

            // ③ 第三道（也是唯一真正闭环的）防线：把最小化消息在窗口过程里直接吃掉。
            //
            // 前两道 —— 摘 WS_MINIMIZEBOX + 事后还原 —— 都只能"减少被收走的概率"
            // 或"事后补救"，而施压是连续的，补救永远慢半拍（实测最坏 200ms 窗口期，
            // 用户看到的就是闪一下没了）。装钩子之后窗口从未进入最小化态，问题从根上消失。
            //
            // 句柄会变，所以每 tick 调一次；方法内部幂等，句柄没变时只做一次句柄比较。
            DesktopEmbedService.GuardAgainstMinimize(this);

            if (DesktopEmbedService.RestoreIfMinimized(this))
            {
                DesktopEmbedService.StripMinimizeBox(this);

                // 还原指令发出去了，但施压可能还在继续，窗口此刻未必真可见。
                // 复查一次，没落地就立刻原 tick 重试，不必等 2s 后的下一轮。
                for (var retry = 0; retry < RestoreRetryPerTick; retry++)
                {
                    if (!DesktopEmbedService.IsHiddenOrMinimized(this))
                    {
                        break;
                    }

                    DesktopEmbedService.RestoreIfMinimized(this);
                    DesktopEmbedService.StripMinimizeBox(this);
                }
            }

            // 普通嵌入桌面的目标是“始终沉在其他窗口之下”。这里不做悬停豁免，
            // 每个 tick 都强制压底，避免点击后窗口被系统拉到最上面。
            if (_config.EmbedDesktop)
            {
                DesktopEmbedService.EnsureEmbedded(this);
            }
            else
            {
                // 没开嵌入也要防被桌面层盖住：「显示桌面」浮起 WorkerW 时不挑模式，
                // 本窗口一样会被压到桌面下面去。
                DesktopEmbedService.KeepAboveDesktopLayer(this);
            }
        }
        catch (Exception ex)
        {
            // 定时器 / 事件回调里的异常会直接终止程序，必须兜底
            App.LogError(ex, "EmbedWatchdog");
        }
    }

    /// <summary>
    /// 同一个看门狗 tick 内允许的还原重试次数。
    ///
    /// 施压是持续性的，一次 ShowWindow 可能刚好落在两次施压的缝里被立刻压回。
    /// 等下一个 200ms tick 太慢（用户能明显看到窗口消失一段时间），所以在 tick 内小步重试。
    /// 次数不能大：这里是 UI 线程，重试多了会反过来卡住界面。
    /// </summary>
    private const int RestoreRetryPerTick = 3;

    /// <summary>
    /// 按当前配置执行一次桌面嵌入。看门狗本身不依赖嵌入开关（非嵌入模式也要还原最小化），
    /// 由 SourceInitialized 无条件启动；这里只在开启嵌入时补一次即时压底。
    /// </summary>
    private void ApplyDesktopEmbed()
    {
        // 幂等：Tick 只挂一次，重复调用 Start 不会叠加处理器。
        StartWindowWatchdog();

        if (_config.EmbedDesktop)
        {
            DesktopEmbedService.EmbedToDesktop(this);
            DesktopEmbedService.SetNoActivateStyle(this, true);
        }
    }

    private void Window_SourceInitialized(object sender, EventArgs e)
    {
        // 缓存窗口句柄给看门狗做前台窗口对比（避免每帧都走一次 WindowInteropHelper）
        _embedHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        // 桌面嵌入：不占任务栏、贴壁纸层、锁定位置
        DesktopEmbedService.HideFromTaskbar(this);

        if (_config.EmbedDesktop)
        {
            DesktopEmbedService.SetNoActivateStyle(this, true);
        }

        if (_viewModel is not null)
        {
            WindowEffects.Apply(this, _viewModel.Settings.BackgroundMode);
        }

        ApplyDesktopEmbed();

        if (_config.LockWindow)
        {
            DesktopEmbedService.LockWindow(this);
        }

        // 更新 CloseButton 可见性
        UpdateCloseButtonVisibility();
        UpdateAiButton();
    }

    private void Window_DpiChanged(object sender, DpiChangedEventArgs e)
    {
        // 跨 DPI 屏拖动窗口时重新夹回屏幕内
        Dispatcher.BeginInvoke(new Action(ClampToWorkingArea), DispatcherPriority.Background);
    }

    /// <summary>
    /// 窗口失活（用户切到别的程序）时立刻沉到底层。
    ///
    /// Shell.MouseLeave 在 WS_EX_LAYERED 窗口上不可靠（被遮住时经常不触发），
    /// 失活是最可靠的"用户已经离开 MicaAgenda"信号，配合 250ms 的看门狗
    /// 双重兜底，可以保证mica 不会赖在 HWND_TOP 不下来。
    /// </summary>
    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (!_config.EmbedDesktop)
        {
            return;
        }

        RunGuarded(LowerToBottom, "Window_Deactivated.Lower");
    }

    private bool _clampInProgress;
    private void ClampToWorkingArea()
    {
        try
        {
            // 防止 SizeChanged -> Clamp -> 设尺寸 -> SizeChanged 的同步重入。
            // 这里只挡“正在执行”的重入，不挡下一次 DPI / Loaded 后的再次夹取。
            if (_clampInProgress) return;
            _clampInProgress = true;

            var work = SystemParameters.WorkArea;
            // ActualWidth/Height 在 SourceInitialized 阶段可能还是 0，退回到 XAML 默认 Width/Height
            var curW = ActualWidth > 0 ? ActualWidth : Width;
            var curH = ActualHeight > 0 ? ActualHeight : Height;
            if (curW <= 0 || curH <= 0) return;
            var maxW = Math.Max(MinWidth, Math.Min(curW, work.Width));
            var maxH = Math.Max(MinHeight, Math.Min(curH, work.Height));
            // 限制位置：窗口必须完全在屏幕工作区内
            var left = Math.Max(work.Left, Math.Min(Left, work.Right - maxW));
            var top = Math.Max(work.Top, Math.Min(Top, work.Bottom - maxH));
            // 仅当需要修正时才设置（避免循环触发 SizeChanged）
            if (Math.Abs(Width - maxW) > 1) { Width = maxW; }
            if (Math.Abs(Height - maxH) > 1) { Height = maxH; }
            if (Math.Abs(Left - left) > 1) { Left = left; }
            if (Math.Abs(Top - top) > 1) { Top = top; }
        }
        catch
        {
            // 极端情况（无显示器）下静默忽略
        }
        finally
        {
            _clampInProgress = false;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_config.LockWindow)
        {
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            e.Handled = true;
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 子窗口/桌面嵌入状态下 DragMove 可能被系统中断，忽略即可。
            }
        }
    }

    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_config.LockWindow)
        {
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed || e.ClickCount != 1 || IsInteractiveDragSource(e.OriginalSource))
        {
            return;
        }

        // 检查鼠标是否在窗口边缘（用于拖动缩放）
        var pos = e.GetPosition(this);
        var edge = GetEdgeAtPosition(pos);
        if (edge != WindowEdge.None)
        {
            // 开始边缘拖动缩放
            StartEdgeResize(edge, e);
            return;
        }

        // 正常拖动窗口
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove 被中断时忽略
        }

        e.Handled = true;
    }

    private enum WindowEdge
    {
        None,
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    private WindowEdge GetEdgeAtPosition(Point pos)
    {
        const double edgeSize = 8;
        var isLeft = pos.X < edgeSize;
        var isRight = pos.X > ActualWidth - edgeSize;
        var isTop = pos.Y < edgeSize;
        var isBottom = pos.Y > ActualHeight - edgeSize;

        if (isTop && isLeft) return WindowEdge.TopLeft;
        if (isTop && isRight) return WindowEdge.TopRight;
        if (isBottom && isLeft) return WindowEdge.BottomLeft;
        if (isBottom && isRight) return WindowEdge.BottomRight;
        if (isLeft) return WindowEdge.Left;
        if (isRight) return WindowEdge.Right;
        if (isTop) return WindowEdge.Top;
        if (isBottom) return WindowEdge.Bottom;

        return WindowEdge.None;
    }

    private void StartEdgeResize(WindowEdge edge, MouseButtonEventArgs e)
    {
        // 锁定位置时禁止调整大小
        if (_config.LockWindow)
        {
            return;
        }

        try
        {
            // 使用 Win32 API 开始窗口边缘拖动
            // HTLEFT=10, HTRIGHT=11, HTTOP=12, HTTOPLEFT=13, HTTOPRIGHT=14, HTBOTTOM=15, HTBOTTOMLEFT=16, HTBOTTOMRIGHT=17
            var hitTest = edge switch
            {
                WindowEdge.Left => 10,        // HTLEFT
                WindowEdge.Right => 11,       // HTRIGHT
                WindowEdge.Top => 12,         // HTTOP
                WindowEdge.TopLeft => 13,     // HTTOPLEFT
                WindowEdge.TopRight => 14,    // HTTOPRIGHT
                WindowEdge.Bottom => 15,      // HTBOTTOM
                WindowEdge.BottomLeft => 16,  // HTBOTTOMLEFT
                WindowEdge.BottomRight => 17, // HTBOTTOMRIGHT
                _ => 0
            };

            if (hitTest != 0)
            {
                // 发送 WM_NCLBUTTONDOWN 消息开始拖动
                var handle = new WindowInteropHelper(this).Handle;
                if (handle != IntPtr.Zero)
                {
                    SendMessage(handle, 0x00A1, (IntPtr)hitTest, IntPtr.Zero);
                }
            }
        }
        catch
        {
            // 忽略边缘拖动错误
        }

        e.Handled = true;
    }

    // 导入 Win32 API
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private void Shell_MouseMove(object sender, MouseEventArgs e)
    {
        if (_config.LockWindow)
        {
            return;
        }

        var pos = e.GetPosition(this);
        var edge = GetEdgeAtPosition(pos);

        var cursor = edge switch
        {
            WindowEdge.Left => Cursors.SizeWE,
            WindowEdge.Right => Cursors.SizeWE,
            WindowEdge.Top => Cursors.SizeNS,
            WindowEdge.Bottom => Cursors.SizeNS,
            WindowEdge.TopLeft => Cursors.SizeNWSE,
            WindowEdge.TopRight => Cursors.SizeNESW,
            WindowEdge.BottomLeft => Cursors.SizeNESW,
            WindowEdge.BottomRight => Cursors.SizeNWSE,
            _ => Cursors.Arrow
        };

        Shell.Cursor = cursor;
    }

    // ===== 悬停置顶（解决"点击日期小方格没反应"） =====
    //
    // 桌面嵌入模式下，窗口常驻 HWND_BOTTOM，被其他窗口（含桌面待办小组件）盖在下面，
    // 点击会落到上层窗口——表现就是"点了没反应"。这里改成"悬停置顶、离开回沉"：
    // 鼠标进入 MicaAgenda 时把窗口提到 HWND_TOP（让点击落到我们自己），短暂离开再回沉。
    // 看门狗在悬停期间不会把窗口压回去（见 StartWindowWatchdog）。
    private void Shell_MouseEnter(object sender, MouseEventArgs e)
    {
        // 嵌入模式不再通过“悬停置顶”交互：窗口必须始终沉在其它窗口之下。
    }

    private void Shell_MouseLeave(object sender, MouseEventArgs e)
    {
        // 嵌入模式不再依赖 MouseLeave 回沉，统一由看门狗强制压底。
    }

    private void RaiseToTop()
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            // HWND_TOP = (HWND)0 —— 提到普通 Z 序顶部（不抢全局焦点、不激活）
            SetWindowPosHelper(handle, IntPtr.Zero, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "RaiseToTop");
        }
    }

    private void LowerToBottom()
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            // HWND_BOTTOM = (HWND)1
            SetWindowPosHelper(handle, new IntPtr(1), 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "LowerToBottom");
        }
    }

    /// <summary>
    /// 强制压底，绕过 IsCursorOverWindow 守卫。仅在用户主动操作后（如保存设置、托盘菜单）调用，
    /// 此时用户预期看到窗口立刻沉底，不应该被"鼠标还停在窗口上"的判定挡住。
    /// </summary>
    private void ForceLowerToBottom()
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            SetWindowPosHelper(handle, new IntPtr(1), 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "ForceLowerToBottom");
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    private static extern bool SetWindowPosHelper(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
    private static extern bool GetCursorPosHelper(out WinPoint lpPoint);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }

    /// <summary>
    /// 光标下"真正能接收到点击的窗口"是不是本窗口。
    /// 用于替代不可靠的 MouseEnter/MouseLeave 标记：
    /// 只看坐标会误判（窗口被别的程序盖住时光标坐标仍可能落在它的矩形内），
    /// 所以这里用 WindowFromPoint 取光标下最顶层窗口，再上溯到根窗口与本窗口比较。
    /// 这样：光标停在 MicaAgenda 露出来的部分 → 置顶可点；光标在别的程序上 → 立即沉底。
    /// </summary>
    private bool IsCursorOverWindow()
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            if (!GetCursorPosHelper(out var cursor))
            {
                return false;
            }

            var hit = WindowFromPointHelper(cursor);
            if (hit == IntPtr.Zero)
            {
                return false;
            }

            // GA_ROOT = 2：上溯到根窗口，避免命中本窗口的某个子控件
            var root = GetAncestorHelper(hit, 2);
            return root == handle;
        }
        catch (Exception ex)
        {
            App.LogError(ex, "IsCursorOverWindow");
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "WindowFromPoint", SetLastError = true)]
    private static extern IntPtr WindowFromPointHelper(WinPoint point);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetAncestor", SetLastError = true)]
    private static extern IntPtr GetAncestorHelper(IntPtr hWnd, uint gaFlags);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
    private static extern bool GetWindowRectHelper(IntPtr hWnd, out WinRect lpRect);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetForegroundWindowHelper();

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WinRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private async void YearView_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SwitchViewModeAsync(CalendarViewMode.Year);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "YearView_Click");
        }
    }

    private async void MonthView_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SwitchViewModeAsync(CalendarViewMode.Month);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "MonthView_Click");
        }
    }

    private async void TitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            await SwitchViewModeAsync(CalendarViewMode.Month);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "TitleText_Click");
        }
    }

    private async void WeekView_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SwitchViewModeAsync(CalendarViewMode.Week);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "WeekView_Click");
        }
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel?.MovePrevious();
            await RefreshHolidaysForVisibleYearAsync();
            ApplyViewHeightPolicy();
            await SaveAsync();
        }
        catch (Exception ex)
        {
            App.LogError(ex, "Previous_Click");
        }
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel?.MoveNext();
            await RefreshHolidaysForVisibleYearAsync();
            ApplyViewHeightPolicy();
            await SaveAsync();
        }
        catch (Exception ex)
        {
            App.LogError(ex, "Next_Click");
        }
    }

    private async void Today_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _viewModel?.GoToday();
            ScrollToToday();
            await RefreshHolidaysForVisibleYearAsync();
            ApplyViewHeightPolicy();
            await SaveAsync();
        }
        catch (Exception ex)
        {
            App.LogError(ex, "Today_Click");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // 点关闭按钮 = 隐藏到托盘，不退出；托盘菜单可重新打开或退出
        HideToTray();
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

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
    }

    /// <summary>
    /// AI 悬浮层打开后把键盘焦点落进输入框：Popup 是独立顶层窗口，不会自动承接焦点，
    /// 不聚焦的话用户点开就能看到输入框、却怎么也打不进字。延后一帧确保 PopupRoot 已挂载。
    /// </summary>
    private void AiPopup_Opened(object sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => AiPanel.FocusInput()), DispatcherPriority.Loaded);
    }

    /// <summary>打开数据统计小面板（本周 / 本月完成情况）。</summary>
    private void Statistics_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            var dialog = new StatisticsWindow(_viewModel.Data, _syncRoot) { Owner = this };
            dialog.ShowDialog();
        }
        catch (Exception ex)
        {
            App.LogError(ex, "Statistics_Click");
        }
    }

    /// <summary>打开「添加周期任务」小面板，确定后创建源任务 + 物化未来实例并落盘。</summary>
    private void RecurringTask_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            var manager = new RecurringManagerWindow(_viewModel) { Owner = this };
            manager.ShowDialog();
            _ = SaveAsync();
        }
        catch (Exception ex)
        {
            App.LogError(ex, "RecurringTask_Click");
        }
    }

    private async void AddTask_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        await FocusInlineTaskInputAsync(_viewModel.SelectedDate);
    }

    private void DayCell_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel is null || (sender as FrameworkElement)?.DataContext is not DayCellViewModel day)
        {
            return;
        }

        // 点击 TextBox（如任务输入框）时不切换选中
        if (e.OriginalSource is DependencyObject source && FindParent<TextBox>(source) is not null)
        {
            return;
        }

        // 点击交互控件（+ 按钮 / 完成复选框等任何 ButtonBase）时不切换选中。
        // 复选框是 ToggleButton（ButtonBase 而非 Button），若只跳过 Button，
        // 在小格子里勾选任务会先触发下面的切选，已选中的格子被取消选中、
        // 面板随之跳回今日，表现为"勾选时到处跳"。
        if (e.OriginalSource is DependencyObject src && FindParent<ButtonBase>(src) is not null)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            return;
        }

        // 单击：切换选中格子（不设置 SelectedDate，避免触发 RebuildCalendar 导致时间轴跳变）
        if (sender is Border border)
        {
            ToggleActiveDayCell(border, day);
        }
    }

    private void Task_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var element = sender as FrameworkElement;
        if (element?.DataContext is not TaskItemViewModel task)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            BeginTaskEdit(task);
            return;
        }

        // 单击：记录起始任务与位置，捕获鼠标 —— 拖到别的日期格子松开即改期（见 Task_MouseLeftButtonUp）。
        _dragTaskId = task.Id;
        _dragOrigin = e.GetPosition(this);
        _dragging = false;
        DragGhostText.Text = task.Title;
        element.CaptureMouse();
    }

    /// <summary>拖拽中：位移超过阈值后显示跟随鼠标的幽灵预览。</summary>
    private void Task_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTaskId is null || _dragOrigin is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var pos = e.GetPosition(this);
        var origin = _dragOrigin.Value;
        if (Math.Abs(pos.X - origin.X) < 6 && Math.Abs(pos.Y - origin.Y) < 6)
        {
            return;
        }

        _dragging = true;
        DragGhost.Visibility = Visibility.Visible;
        Canvas.SetLeft(DragGhost, Math.Min(pos.X + 14, Math.Max(0, ActualWidth - 160)));
        Canvas.SetTop(DragGhost, Math.Min(pos.Y + 12, Math.Max(0, ActualHeight - 40)));
    }

    private void Task_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
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
        DragGhost.Visibility = Visibility.Collapsed;

        if (sender is FrameworkElement element)
        {
            element.ReleaseMouseCapture();
        }

        var end = e.GetPosition(this);
        // 位移小于 6px 视为点击，不是拖拽
        if (!wasDragging && Math.Abs(end.X - origin.X) < 6 && Math.Abs(end.Y - origin.Y) < 6)
        {
            return;
        }

        if (FindDayCellAt(end)?.DataContext is DayCellViewModel day && _viewModel is not null)
        {
            if (_viewModel.ChangeTaskDate(taskId, day.Date))
            {
                _ = SaveAsync();
            }
        }
    }

    private Guid? _dragTaskId;
    private Point? _dragOrigin;
    private bool _dragging;

    /// <summary>按窗口坐标找到其下的日期格子（DataContext 是 DayCellViewModel 的容器）。</summary>
    private FrameworkElement? FindDayCellAt(Point windowPos)
    {
        foreach (var element in FindVisualChildren<FrameworkElement>(this))
        {
            if (element.DataContext is not DayCellViewModel || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                continue;
            }

            try
            {
                var topLeft = element.TranslatePoint(new Point(0, 0), this);
                if (new Rect(topLeft, new Size(element.ActualWidth, element.ActualHeight)).Contains(windowPos))
                {
                    return element;
                }
            }
            catch
            {
                // 隐藏/脱离布局的元素 TranslatePoint 可能抛异常，跳过即可
            }
        }

        return null;
    }

    private static IEnumerable<FrameworkElement> FindVisualChildren(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe)
            {
                yield return fe;
            }

            foreach (var descendant in FindVisualChildren(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// 切换日期格子的选中状态：点击已选中的格子取消选中，点击其他格子切换。
    /// </summary>
    private void ToggleActiveDayCell(Border border, DayCellViewModel day)
    {
        if (_viewModel is null)
        {
            return;
        }

        // 选中态交给 ViewModel 保存，由样式触发器渲染高亮：
        // 这样日历重建（切月/加任务）后高亮不会丢，也不会残留本地值把背景刷成异常颜色。
        if (_viewModel.SelectedCellDate == day.Date)
        {
            ClearActiveDayCell();
            return;
        }

        ClearActiveDayCell();
        _activeDayCellBorder = border;
        _viewModel.SelectCell(day.Date);
    }

    private void ClearActiveDayCell()
    {
        _activeDayCellBorder = null;
        _viewModel?.ClearCellSelection();
    }

    /// <summary>
    /// 当前有效的选中格子容器。日历重建后旧容器已从视觉树移除，
    /// 此处做惰性失效校验，避免持有悬挂引用导致滚动/交互异常。
    /// </summary>
    private Border? ActiveDayCellBorder
    {
        get
        {
            if (_activeDayCellBorder is null)
            {
                return null;
            }

            if (!_activeDayCellBorder.IsLoaded)
            {
                _activeDayCellBorder = null;
            }

            return _activeDayCellBorder;
        }
    }

    // ===== 今日任务面板 =====

    private void AddTodayTask_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.BeginAddTodayTask();
        Dispatcher.InvokeAsync(FocusTodayTaskInput, DispatcherPriority.Background);
    }

    /// <summary>一键删除当前面板日期（_panelDate）的所有任务，弹出确认对话框防误删。</summary>
    private async void ClearPanelDate_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        var panelDate = _viewModel.PanelDate;
        var count = _viewModel.TodayTasks.Count;
        if (count <= 0)
        {
            return;
        }

        var result = MessageBox.Show(
            $"确定要删除 {panelDate:yyyy年M月d日} 的全部 {count} 条任务吗？\n此操作不可撤销。",
            "清空当天任务",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        var removed = _viewModel.ClearPanelDateTasks();
        _viewModel.MarkDirty();
        await SaveAsync();
        ShowToast($"已删除 {panelDate:yyyy年M月d日} 的 {removed} 条任务");
    }

    /// <summary>一键删除本周所有任务 + 所有未完成逾期任务，弹出确认对话框。</summary>
    /// <summary>右侧"本周任务完成情况"各子项的批量按钮都走同一个壳：确认 → 调 viewModel → 落盘 → 提示。</summary>
    private async void BulkWeekAction(string title, string prompt, Func<int> action)
    {
        if (_viewModel is null)
        {
            return;
        }

        var answer = MessageBox.Show(this, prompt, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        int removed;
        try
        {
            removed = action();
            _viewModel.MarkDirty();
            await SaveAsync();
        }
        catch (Exception ex)
        {
            App.LogError(ex, $"BulkWeekAction:{title}");
            MessageBox.Show(this, $"操作失败：{ex.Message}", title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ShowToast($"已处理 {removed} 条任务");
    }
    private void ClearOverdueTasks_Click(object sender, RoutedEventArgs e) => BulkWeekAction(
        "删除逾期未完成",
        "确定要一键删除所有逾期未完成的任务吗？本操作不可撤销。",
        () => _viewModel?.ClearOverdueTasks() ?? 0);

    private void MarkOverdueCompleted_Click(object sender, RoutedEventArgs e) => BulkWeekAction(
        "标记逾期为已完成",
        "确定要一键把所有逾期未完成任务标记为已完成吗？",
        () => _viewModel?.MarkOverdueCompleted() ?? 0);

    private void ClearOpenTasks_Click(object sender, RoutedEventArgs e) => BulkWeekAction(
        "删除本周未完成",
        "确定要一键删除本周内所有未完成的任务吗？本操作不可撤销。",
        () => _viewModel?.ClearOpenTasks() ?? 0);

    private void MarkOpenCompleted_Click(object sender, RoutedEventArgs e) => BulkWeekAction(
        "标记本周为已完成",
        "确定要一键把所有本周未完成任务标记为已完成吗？",
        () => _viewModel?.MarkOpenCompleted() ?? 0);

    private void ClearCompletedTasks_Click(object sender, RoutedEventArgs e) => BulkWeekAction(
        "删除本周已完成",
        "确定要一键删除本周内所有已完成的任务吗？本操作不可撤销。",
        () => _viewModel?.ClearCompletedTasks() ?? 0);

    private void MarkCompletedIncomplete_Click(object sender, RoutedEventArgs e) => BulkWeekAction(
        "标记本周为未完成",
        "确定要一键把所有本周已完成的任务标记为未完成吗？",
        () => _viewModel?.MarkCompletedIncomplete() ?? 0);

    private void FocusTodayTaskInput()
    {
        var box = FindVisualChildren<TextBox>(CalendarBody)
            .FirstOrDefault(textBox => textBox.Tag as string == TodayDraftTag && textBox.IsVisible);
        box?.Focus();
    }

    private async void TodayTaskTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitTodayTaskAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel?.CancelTodayTask();
        }
    }

    private async void TodayTaskTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        await CommitTodayTaskAsync();
    }

    private async Task CommitTodayTaskAsync()
    {
        if (_viewModel is null || !_viewModel.IsAddingTodayTask)
        {
            return;
        }

        CalendarTask? created = null;
        PreserveMonthScroll(() => created = _viewModel.CommitTodayTask());
        if (created is null)
        {
            return;
        }

        FitWindowToContent();
        await SaveAsync();
    }

    /// <summary>
    /// 在执行会刷新日历的操作时保住月视图的滚动位置。
    /// 改动任务会重排日期格子的内容，虚拟化面板可能顺势重算偏移，
    /// 视觉上就是"左侧月份自己跳走"；这里刷新后校正回去。
    /// </summary>
    private void PreserveMonthScroll(Action action)
    {
        var viewer = MonthScrollViewer;
        if (viewer is null || _viewModel is null || _viewModel.Settings.ViewMode != CalendarViewMode.Month)
        {
            action();
            return;
        }

        var offset = viewer.VerticalOffset;
        action();

        // 布局要到下一帧才稳定，延后再校正
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (MonthScrollViewer is null)
            {
                return;
            }

            if (Math.Abs(MonthScrollViewer.VerticalOffset - offset) > 0.01)
            {
                ScrollToOffsetProgrammatically(offset);
            }
        }), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 今日任务面板内的滚轮：内容不足一屏时吞掉事件，
    /// 避免冒泡到外层把整个月份时间轴一起滚走。
    /// </summary>
    private void TodayPanelScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv && sv.ExtentHeight <= sv.ViewportHeight)
        {
            e.Handled = true;
        }
    }

    private async void AddTaskOnSelectedCell_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as FrameworkElement)?.DataContext is not DayCellViewModel day)
        {
            return;
        }

        // 不要在这里设置 SelectedDate：在月视图中它会改变锚点月份，
        // 触发 RebuildCalendar 重建时间轴并滚动定位，造成界面跳动。
        await FocusInlineTaskInputAsync(day.Date);
    }

    private async void CompleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as FrameworkElement)?.DataContext is not TaskItemViewModel task)
        {
            return;
        }

        if (!task.IsCompleted)
        {
            PreserveMonthScroll(() => _viewModel.ToggleTaskCompletion(task.Id));
            await SaveAsync();
            await PushCompletionToMindMapAsync(task.Model);
        }
    }

    private async Task PushCompletionToMindMapAsync(CalendarTask task)
    {
        if (!_config.SyncMyMindMapEnabled || string.IsNullOrWhiteSpace(_config.MyMindMapToken)) return;
        if (!task.IsReviewTask) return;
        try
        {
            // 通过同步服务推送，携带 UpdatedAt（状态最后变更时间），供对端做时间戳仲裁
            if (_mindMapSyncService is null)
            {
                StartMindMapSyncService();
            }
            if (_mindMapSyncService is not null)
            {
                await _mindMapSyncService.PushStatusAsync(_config.MyMindMapToken, task);
            }
        }
        catch
        {
            // 静默：my-mindmap agent 未启动/Token 错误时不影响日历本机操作
        }
    }

    /// <summary>
    /// 复习任务被删除：通知 my-mindmap agent 一起删掉对应的复习周期。
    ///
    /// 界面删除走 <see cref="MainViewModel.ReviewTaskDeleted"/>，HTTP API 与 MCP 走构造时注入的回调 ——
    /// 三条入口都要接，否则下一次同步会按对端复习计划把它重新建回来。
    /// 推送要走网络，不能占着调用线程（可能是 HttpListener 后台线程），丢给线程池。
    /// </summary>
    /// <summary>
    /// 复习任务的完成状态变了（单条勾选、批量「标记完成 / 还原未完成」，以及 HTTP API / MCP
    /// 那两条由服务构造时注入的回调直接点名本方法）：立刻回推给 my-mindmap agent。
    /// 推送要走网络，丢给线程池，别占着调用线程。
    /// </summary>
    private void OnReviewTaskStatusChanged(CalendarTask task)
    {
        if (!task.IsReviewTask)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await PushCompletionToMindMapAsync(task);
            }
            catch
            {
                // 尽力而为：对端未启动 / Token 失效时不影响日历本机操作
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
            StartMindMapSyncService();
        }

        var service = _mindMapSyncService;
        if (service is null)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await service.RegisterReviewDeletionAsync(task);
            }
            catch
            {
                // 尽力而为：失败只影响对端清理，本机删除照旧
            }
        });
    }

    /// <summary>任务后的小复选框：快速勾选/取消完成状态。</summary>
    private void TaskCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: TaskItemViewModel task } || _viewModel is null)
        {
            return;
        }

        var isChecked = ((CheckBox)sender).IsChecked == true;
        if (isChecked != task.IsCompleted)
        {
            PreserveMonthScroll(() => _viewModel.ToggleTaskCompletion(task.Id));
            _ = SaveAsync();
            _ = PushCompletionToMindMapAsync(task.Model);
        }
    }

    /// <summary>本周任务完成情况区（右侧底部）行内 CheckBox：单击切换完成态。阻止冒泡避免触发行的单击/双击。</summary>
    private void WeekTaskCheck_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: TaskItemViewModel task } || _viewModel is null)
        {
            return;
        }

        // 阻止冒泡到 Border，避免双击编辑被误触发
        if (e is RoutedEventArgs re) re.Handled = true;

        PreserveMonthScroll(() => _viewModel.ToggleTaskCompletion(task.Id));
        _ = SaveAsync();
        _ = PushCompletionToMindMapAsync(task.Model);
    }

    /// <summary>本周任务行：单击切换完成状态，双击进入编辑。</summary>
    private void WeekTaskRow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TaskItemViewModel task || _viewModel is null)
        {
            return;
        }

        if (e.ClickCount >= 2)
        {
            // 双击进入编辑
            e.Handled = true;
            BeginTaskEdit(task);
            return;
        }

        // 单击切换完成态；右键菜单的 Complete/Incomplete 不变
        e.Handled = true;
        PreserveMonthScroll(() => _viewModel.ToggleTaskCompletion(task.Id));
        _ = SaveAsync();
        _ = PushCompletionToMindMapAsync(task.Model);
    }

    private async void IncompleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as FrameworkElement)?.DataContext is not TaskItemViewModel task)
        {
            return;
        }

        if (task.IsCompleted)
        {
            _viewModel.ToggleTaskCompletion(task.Id);
            await SaveAsync();
            await PushCompletionToMindMapAsync(task.Model);
        }
    }

    private async void ImportantTask_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as FrameworkElement)?.DataContext is not TaskItemViewModel task)
        {
            return;
        }

        if (!task.IsImportant)
        {
            _viewModel.ToggleTaskImportance(task.Id);
            await SaveAsync();
        }
    }

    private async void UnimportantTask_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as FrameworkElement)?.DataContext is not TaskItemViewModel task)
        {
            return;
        }

        if (task.IsImportant)
        {
            _viewModel.ToggleTaskImportance(task.Id);
            await SaveAsync();
        }
    }

    private void EditTask_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaskItemViewModel task)
        {
            BeginTaskEdit(task);
        }
    }

    private void DeleteTask_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || (sender as FrameworkElement)?.DataContext is not TaskItemViewModel task)
        {
            return;
        }

        // 同步删除任务（重建日历格、右侧面板、本周分组都是同步完成），落盘交给后台；
        // 否则 await SaveAsync 会把右键菜单的响应拖到磁盘写入之后——大文件 + 多任务时
        // 体感是「点完删除过几秒到半分钟才真的消失」。
        _viewModel.DeleteTask(task.Id);
        _ = SaveAsync();
    }

    private void LockScrollBox_Changed(object sender, RoutedEventArgs e)
    {
        _lockScroll = LockScrollBox.IsChecked == true;

        // ⚠️ 不要在这里切换 VerticalScrollBarVisibility。
        //
        // 用户反馈"点锁定滚动时月份会跑到上一个月的方向"——8 月点一下 LockScroll
        // 就跳到 7 月。实测定位到根因：Hidden ↔ Disabled/Auto 的属性切换会触发
        // WPF ScrollViewer 内部 ScrollInfo 重建，VerticalOffset 在重建过程中被夹紧
        // 或重置到某个非预期值（向上漂一个月），而 ScrollChanged 事件已 _lockScroll=true
        // 被早退，扩展/锚定逻辑都不会再跑，所以漂移就留下了。
        //
        // 锁定滚动时冻结时间轴的需求其实已经被 PreviewMouseWheel 拦截（_lockScroll=true 时
        // 滚轮只在选中格子的任务列表内滚动，并 e.Handled=true 阻止冒泡到 ScrollViewer 的
        // 默认滚动）。唯一会漏的入口是键盘 PageUp/PageDown/Home/End，但 XAML 默认的
        // VerticalScrollBarVisibility=Hidden 已经让 ScrollViewer 不响应键盘滚动。
        // 所以这里不需要再去碰 ScrollBarVisibility，让它始终保持 Hidden。

        // 取消可能还处于冷却中、尚未兑现的自动扩展请求，避免解锁后立刻补做一次扩展，
        // 否则会出现"刚解锁就自动跳月/刷新"的突兀感。
        _pendingExtendDir = 0;
        if (_extendRetryTimer is { IsEnabled: true })
        {
            _extendRetryTimer.Stop();
        }
    }

    /// <summary>
    /// 用 ThemeCatalog.All 填充主题下拉框（与 Avalonia 宿主同一份清单，见 ThemeCatalog）。
    /// 以前清单手写在 XAML 里，和调色板各存一份 —— 加主题时很容易只改一处。
    /// </summary>
    private void EnsureBackgroundModeItems()
    {
        if (BackgroundModeBox.Items.Count > 0)
        {
            return;
        }

        foreach (var def in ThemeCatalog.All)
        {
            BackgroundModeBox.Items.Add(new ComboBoxItem { Content = def.Name, Tag = def.Mode });
        }
    }

    private async void BackgroundModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingSettings || _viewModel is null
            || BackgroundModeBox.SelectedItem is not ComboBoxItem { Tag: CalendarBackgroundMode mode })
        {
            return;
        }

        _viewModel.Settings.BackgroundMode = mode;
        ApplyBackground();
        ApplyViewHeightPolicy();
        await SaveAsync();
    }

    private async void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isApplyingSettings || _viewModel is null)
        {
            return;
        }

        _viewModel.Settings.Opacity = Math.Round(OpacitySlider.Value, 2);
        ApplyBackground();
        await SaveAsync();
    }

    private async void InlineTaskTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitInlineTaskAsync(textBox);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelInlineTask(textBox);
        }
    }

    private async void InlineTaskTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            await CommitInlineTaskAsync(textBox);
        }
    }

    private async void TaskEditTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitTaskEditAsync(textBox);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelTaskEdit(textBox);
        }
    }

    private async void TaskEditTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            await CommitTaskEditAsync(textBox);
        }
    }

    private async Task CommitInlineTaskAsync(TextBox textBox)
    {
        if (_viewModel is null || textBox.DataContext is not DayCellViewModel day)
        {
            return;
        }

        var title = day.DraftTitle.Trim();
        day.CancelAdd();
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        // AddTask 已按 day.Date 写入并触发增量刷新，无需再改 SelectedDate；
        // 避免月视图下改变锚点月份导致时间轴重建与滚动跳动。
        PreserveMonthScroll(() => _viewModel.AddTask(day.Date, title));
        FitWindowToContent();
        await SaveAsync();
    }

    private void CancelInlineTask(TextBox textBox)
    {
        if (textBox.DataContext is DayCellViewModel day)
        {
            day.CancelAdd();
        }
    }

    private async Task CommitTaskEditAsync(TextBox textBox)
    {
        if (_viewModel is null || textBox.DataContext is not TaskItemViewModel task)
        {
            return;
        }

        var title = task.EditTitle.Trim();
        task.IsEditing = false;
        if (string.IsNullOrWhiteSpace(title))
        {
            task.CancelEdit();
            return;
        }

        _viewModel.RenameTask(task.Id, title);
        FitWindowToContent();
        await SaveAsync();
    }

    private void CancelTaskEdit(TextBox textBox)
    {
        if (textBox.DataContext is TaskItemViewModel task)
        {
            task.CancelEdit();
        }
    }

    private void BeginAddTask(DayCellViewModel day)
    {
        day.BeginAdd();
        Dispatcher.InvokeAsync(() => FocusDayInput(day.Date), DispatcherPriority.Background);
    }

    private void BeginTaskEdit(TaskItemViewModel task)
    {
        task.BeginEdit();
        Dispatcher.InvokeAsync(() => FocusTaskInput(task.Id), DispatcherPriority.Background);
    }

    private async Task FocusInlineTaskInputAsync(DateOnly date)
    {
        // 周视图的格子位于 VisibleDays，月视图的格子位于 TimelineMonths 各月份块中；
        // 需要同时检索两者，否则月视图点击加号会找不到对应格子、输入框不出现。
        var day = _viewModel?.VisibleDays.FirstOrDefault(item => item.Date == date)
                  ?? _viewModel?.TimelineMonths.SelectMany(block => block.Days).FirstOrDefault(item => item.Date == date);
        if (day is not null)
        {
            BeginAddTask(day);
        }
        await Task.CompletedTask;
    }

    private void FocusDayInput(DateOnly date)
    {
        var input = FindVisualChildren<TextBox>(CalendarBody)
            .FirstOrDefault(textBox => textBox.DataContext is DayCellViewModel day && day.Date == date);
        input?.Focus();
    }

    private void FocusTaskInput(Guid taskId)
    {
        var input = FindVisualChildren<TextBox>(CalendarBody)
            .FirstOrDefault(textBox => textBox.DataContext is TaskItemViewModel task && task.Id == taskId);
        if (input is null)
        {
            return;
        }

        input.Focus();
        input.SelectAll();
    }

    private void ApplySettingsToWindow()
    {
        if (_viewModel is null)
        {
            return;
        }

        _isApplyingSettings = true;
        try
        {
            var settings = _viewModel.Settings;
            // 启动路径：连上次存下的位置一起恢复（不是 GetBoundsForView —— 那个位置不动）。
            var bounds = GetStartupBounds(settings.ViewMode);
            ApplyWindowBounds(bounds);
            Topmost = false;
            OpacitySlider.Value = settings.Opacity;
            SelectBackgroundMode(settings.BackgroundMode);
            ApplyBackground();
            ApplyViewHeightPolicy();
        }
        finally
        {
            _isApplyingSettings = false;
        }
    }

    private void SelectBackgroundMode(CalendarBackgroundMode mode)
    {
        EnsureBackgroundModeItems();

        // ClearBorder 等历史值由 ThemeCatalog 归一到 None。
        var target = ThemeCatalog.Get(mode).Mode;
        if (target == CalendarBackgroundMode.None && _viewModel is not null)
        {
            _viewModel.Settings.BackgroundMode = mode;
        }

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

    private async Task SwitchViewModeAsync(CalendarViewMode viewMode)
    {
        if (_viewModel is null)
        {
            return;
        }

        SaveCurrentViewBounds();
        _viewModel.SetViewMode(viewMode);
        await RefreshHolidaysForVisibleYearAsync();
        ApplyWindowBounds(GetBoundsForView(viewMode));
        ApplyViewHeightPolicy();
        SaveCurrentViewBounds();
        await SaveAsync();

        // 切到周视图时把左栏滚回开头：默认展示的必须是「最近 7 天」，
        // 而不是上次翻到几周之后的残留位置。
        if (viewMode == CalendarViewMode.Week)
        {
            // 用 InvokeAsync 而不是 BeginInvoke：后者的返回值是 awaitable，未 await 会报 CS4014。
            _ = Dispatcher.InvokeAsync(
                () =>
                {
                    UpdateWeekCellSize();
                    WeekScrollViewer?.ScrollToTop();
                },
                DispatcherPriority.Loaded);
        }
        else if (viewMode == CalendarViewMode.Year)
        {
            // 切到年视图时月卡才第一次真正布局；Loaded 优先级等布局完成后
            // 再按实际列宽回填日期字号（初始是默认值 11，小窗口下会偏大裁字）。
            _ = Dispatcher.InvokeAsync(UpdateYearFontScale, DispatcherPriority.Loaded);
        }
    }

    /// <summary>
    /// 把当前窗口几何记进设置：通用记忆 + 当前视图的记忆。
    ///
    /// <para>⚠️ <b>必须存真实坐标</b>。早先版本在这里把 Left/Top 抹成 0，
    /// 而启动恢复是把整条记录当完整边界读回去的 —— 于是每次开机窗口都落在屏幕左上角。
    /// 详见 <see cref="WindowBoundsResolver.ForStartup"/> 的说明。</para>
    /// </summary>
    private void SaveCurrentViewBounds()
    {
        if (_viewModel is null)
        {
            return;
        }

        // 窗口位置/尺寸变化需在下次周期性保存时落盘
        _viewModel.MarkDirty();

        var bounds = new WindowBounds(Left, Top, Width, Height);
        _viewModel.Settings.WindowBounds = bounds;
        switch (_viewModel.Settings.ViewMode)
        {
            case CalendarViewMode.Month:
                _viewModel.Settings.MonthWindowBounds = bounds;
                break;
            case CalendarViewMode.Week:
                _viewModel.Settings.WeekWindowBounds = bounds;
                break;
            case CalendarViewMode.Year:
                // 按实际高度保存：若在这里再钳一次，用户拉大年视图后会被压回理想高度
                _viewModel.Settings.YearWindowBounds = bounds with
                {
                    Height = Math.Min(bounds.Height, GetYearResizeLimit())
                };
                break;
        }
    }

    private WindowBounds GetBoundsForView(CalendarViewMode viewMode)
    {
        if (_viewModel is null)
        {
            return new WindowBounds(Left, Top, Width, Height);
        }

        // 切视图时统一回到「该视图的默认尺寸」，不再沿用上次自己拖出来的尺寸：
        // 记忆的尺寸可能已经小到放不下这个视图（周视图本身就能拖得很窄，最容易中招），
        // 切过去会显示不全、甚至被窄窗规则强制成任务面板，表现就是"点了没反应"。
        // 位置保持不动、只换尺寸，免得窗口在屏幕上乱跳。
        var height = viewMode switch
        {
            // 周视图一行 7 个格子，太矮会把格子压扁到看不清
            CalendarViewMode.Week => 500.0,
            CalendarViewMode.Year => GetYearMaximumWindowHeight(),
            _ => 620.0
        };

        return new WindowBounds(Left, Top, 900.0, height);
    }

    /// <summary>
    /// 「启动恢复」用的边界：把上次退出时存下的<b>位置 + 尺寸</b>整套取回来。
    ///
    /// 与 <see cref="GetBoundsForView"/>（切视图用，位置不动）的区别就在这里 ——
    /// 启动时必须连位置一起恢复，否则每次开机都回到默认坐标。
    ///
    /// 取值规则在 <see cref="WindowBoundsResolver.ForStartup"/>（与 Avalonia 宿主共用、有单测覆盖）。
    /// </summary>
    private WindowBounds GetStartupBounds(CalendarViewMode viewMode)
    {
        if (_viewModel is null)
        {
            return new WindowBounds(Left, Top, Width, Height);
        }

        return WindowBoundsResolver.ForStartup(
            _viewModel.Settings, viewMode, GetBoundsForView(viewMode));
    }

    private void ApplyWindowBounds(WindowBounds bounds)
    {
        // 目标坐标落在哪块屏上：多显示器下必须按坐标找，不能只看主屏工作区 ——
        // 窗口上次在副屏，按主屏夹取会被硬拽回主屏。
        // WinForms 的 Screen.FromPoint 收的是 System.Drawing.Point（物理像素），
        // 而 WPF 的 Left/Top 就是设备无关的逻辑像素 —— PerMonitorV2 + 100% 缩放下两者一致，
        // 高 DPI 下会有偏差，但那只会影响"选哪块屏"，选错的兜底是后面的可见性判断。
        var target = new System.Drawing.Point((int)bounds.Left, (int)bounds.Top);
        var screen = Screen.FromPoint(target);
        var workArea = screen?.WorkingArea ?? ToDrawingRect(SystemParameters.WorkArea);

        var rawWidth = Math.Max(MinWidth, bounds.Width);
        var rawHeight = Math.Max(MinHeight, bounds.Height);

        if (_viewModel?.Settings.ViewMode == CalendarViewMode.Year)
        {
            // 只受屏幕工作区限制：GetYearMaximumWindowHeight 只是"没有保存过尺寸时"的初始理想高度，
            // 若在这里也用它钳制，用户自己拉大的年视图一切换视图就被压回去了。
            rawHeight = Math.Min(rawHeight, GetYearResizeLimit());
        }

        // 关键：必须把尺寸夹到工作区内，否则保存的旧尺寸（如 1470x1020）会直接生效，
        // 在 1280x800 的屏幕上"只显示一半儿"。配合 LockWindow=true（用户无法拖动调整），
        // 唯一能自救的就是在恢复时直接夹回屏幕。
        var width = Math.Min(rawWidth, Math.Max(MinWidth, workArea.Width));
        var height = Math.Min(rawHeight, Math.Max(MinHeight, workArea.Height));

        Width = width;
        Height = height;

        if (IsMostlyOffScreen(bounds, width, height))
        {
            // 保存的坐标已经不在任何屏幕的可见区里（典型：那块外接显示器被拔了）。
            // 夹取救不了这种情况 —— 夹到一个不存在的坐标系上，窗口照样看不见，
            // 用户会认为"程序没启动"。直接拉回主屏可见区。
            var primaryArea = Screen.PrimaryScreen?.WorkingArea ?? workArea;
            Left = primaryArea.Left + Math.Max(0, (primaryArea.Width - width) / 2);
            Top = primaryArea.Top + Math.Max(0, (primaryArea.Height - height) / 3);
            App.LogError(null, $"[WindowBounds] 记忆坐标 {bounds.Left},{bounds.Top} 已不可见，拉回主屏 {Left},{Top}");
        }
        else
        {
            Left = Math.Clamp(bounds.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
            Top = Math.Clamp(bounds.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        }

        UpdateYearScrollLimit();
    }

    /// <summary>WPF 的 <see cref="Rect"/> 转 WinForms 的 <see cref="System.Drawing.Rectangle"/>。</summary>
    private static System.Drawing.Rectangle ToDrawingRect(Rect rect)
        => new((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height);

    /// <summary>
    /// 窗口是否"基本落在所有屏幕之外"——各屏可见区域的并集覆盖不到窗口面积的一半就判定回不来。
    /// 这样"一半挂在屏幕边缘"仍算可见（用户能拖回来），只有真的整块跑到屏外才会触发回拉。
    /// </summary>
    private static bool IsMostlyOffScreen(WindowBounds bounds, double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var left = bounds.Left;
        var top = bounds.Top;
        var right = left + width;
        var bottom = top + height;

        double visible = 0;
        foreach (var screen in Screen.AllScreens)
        {
            var area = screen.WorkingArea;
            var overlapW = Math.Min(right, area.Right) - Math.Max(left, area.Left);
            var overlapH = Math.Min(bottom, area.Bottom) - Math.Max(top, area.Top);
            if (overlapW > 0 && overlapH > 0)
            {
                visible += overlapW * overlapH;
            }
        }

        return visible < width * height / 2;
    }

    private void ApplyBackground()
    {
        if (_viewModel is null)
        {
            return;
        }

        var settings = _viewModel.Settings;

        // 颜色与 alpha 全部来自 ThemeCatalog（Core，两个宿主共用）——这里不再对 alpha 做逐模式截断：
        // 老版本写着 Math.Min(alpha, 210) / Math.Min(alpha, 150)，透明度滑杆拉到头也只有 82% / 58%，
        // 用户反馈"透明度的强度不高"，是代码里限死的。现在 1:1 映射，强度交给用户。
        Shell.Background = ToBrush(ThemeCatalog.Build(settings.BackgroundMode, settings.Opacity).Shell);

        ApplyBackgroundResources(settings.BackgroundMode);
        Shell.BorderBrush = (Brush)Resources["WindowEdgeBrush"];
        ApplyShellClip();

        // 实时调整整窗透明度（含背景层与被 DWM 背景覆盖的情况），拖动滑块即可预览
        this.Opacity = Math.Clamp(settings.Opacity, 0.2, 1);

        WindowEffects.Apply(this, settings.BackgroundMode);
    }

    private void ApplyShellClip()
    {
        if (Shell.ActualWidth <= 0 || Shell.ActualHeight <= 0)
        {
            return;
        }

        Shell.Clip = CreateRoundedShellClip(Shell.ActualWidth, Shell.ActualHeight, Shell.CornerRadius.TopLeft);
    }

    internal static RectangleGeometry CreateRoundedShellClip(double width, double height, double radius)
    {
        return new RectangleGeometry(new Rect(0, 0, width, height), radius, radius);
    }

    private void ApplyBackgroundResources(CalendarBackgroundMode mode)
    {
        var c = ThemeCatalog.Build(mode, _viewModel?.Settings.Opacity ?? 1.0);

        // 全部颜色来自 Core 的 ThemeCatalog —— 与 Avalonia 宿主共用同一份定义，
        // 这里只做「角色 → XAML 资源键」的搬运。WPF 宿主另有几个自己用的资源键
        // （行内输入框底色 / 滑杆轨道与拇指 / 年视图格子），复用手边最贴近的角色即可。
        Resources["PrimaryTextBrush"] = ToBrush(c.PrimaryText);
        Resources["MutedTextBrush"] = ToBrush(c.MutedText);
        Resources["CellBorderBrush"] = ToBrush(c.CellBorder);
        Resources["TaskBorderBrush"] = ToBrush(c.TaskBorder);
        Resources["InlineInputBackgroundBrush"] = ToBrush(c.Panel);
        Resources["DayCellBackgroundBrush"] = ToBrush(c.DayCell);
        Resources["DayCellOutMonthBackgroundBrush"] = ToBrush(c.DayCellOutMonth);
        Resources["YearMonthBackgroundBrush"] = ToBrush(c.YearMonth);
        Resources["YearCellBackgroundBrush"] = ToBrush(c.YearMonth);
        Resources["TodayCellBackgroundBrush"] = ToBrush(c.TodayCell);
        Resources["TodayCellBorderBrush"] = ToBrush(c.TodayCellBorder);
        Resources["SelectedCellBackgroundBrush"] = ToBrush(c.SelectedCell);
        Resources["SelectedCellBorderBrush"] = ToBrush(c.SelectedCellBorder);
        Resources["TaskBackgroundBrush"] = ToBrush(c.TaskPill);
        Resources["TodayPanelBackgroundBrush"] = ToBrush(c.Panel);
        Resources["TodayPanelBorderBrush"] = ToBrush(c.PanelBorder);
        Resources["WeekGroupBackgroundBrush"] = ToBrush(c.WeekGroup);
        Resources["WeekGroupHoverBrush"] = ToBrush(c.WeekGroupHover);
        Resources["WeekTaskRowHoverBrush"] = ToBrush(c.WeekRowHover);
        Resources["ImportantTaskBackgroundBrush"] = ToBrush(c.ImportantBg);
        Resources["ImportantTaskBorderBrush"] = ToBrush(c.ImportantBorder);
        Resources["ImportantTaskTextBrush"] = ToBrush(c.ImportantText);
        Resources["HolidayBreakBrush"] = ToBrush(c.HolidayBreak);
        Resources["HolidayWorkBrush"] = ToBrush(c.HolidayWork);
        Resources["HolidayTextBrush"] = ToBrush(c.HolidayText);
        Resources["HolidayWorkTextBrush"] = ToBrush(c.HolidayWorkText);
        Resources["WeekDoneCheckBrush"] = ToBrush(c.DoneCheck);
        Resources["WindowEdgeBrush"] = ToBrush(c.WindowEdge);
        Resources["ToolbarControlBackgroundBrush"] = ToBrush(c.ToolbarBackground);
        Resources["ToolbarControlBorderBrush"] = ToBrush(c.ToolbarBorder);
        Resources["ToolbarControlHoverBrush"] = ToBrush(c.ToolbarHover);
        Resources["ToolbarControlPressedBrush"] = ToBrush(c.ToolbarPressed);
        Resources["ToolbarTrackBrush"] = ToBrush(c.ToolbarBorder);
        Resources["ToolbarThumbBrush"] = ToBrush(c.PrimaryText);
    }

    /// <summary>把 Core 的颜色转成 WPF 画刷。全透明直接复用静态实例。</summary>
    private static Brush ToBrush(RgbaColor c)
        => c.A == 0 ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));

    private void ApplyCellScale()
    {
        CalendarBody.LayoutTransform = Transform.Identity;
    }

    private void ResizeGrip_DragStarted(object? sender, DragStartedEventArgs e)
    {
        _userSizing = true;
    }

    private void ResizeGrip_DragCompleted(object? sender, DragCompletedEventArgs e)
    {
        _userSizing = false;
        SaveCurrentViewBounds();
    }

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        // 锁定位置时禁止缩放
        if (_config.LockWindow)
        {
            return;
        }
        if (Math.Abs(e.HorizontalChange) > 0.01)
        {
            Width = Math.Max(MinWidth, Width + e.HorizontalChange);
            if (_viewModel.Settings.ViewMode == CalendarViewMode.Year)
            {
                UpdateYearScrollLimit();
            }
        }

        if (Math.Abs(e.VerticalChange) > 0.01)
        {
            if (_viewModel.Settings.ViewMode == CalendarViewMode.Year)
            {
                // 年视图：只受屏幕工作区限制，不再被"4 行月卡片"的理想高度钳住，
                // 否则窗口一旦到达该高度就再也拉不大了。
                Height = Math.Clamp(Height + e.VerticalChange, MinHeight, GetYearResizeLimit());
                UpdateYearScrollLimit();
            }
            else if (_viewModel.Settings.ViewMode == CalendarViewMode.Week)
            {
                // 周视图底部任务面板使用星号行占满剩余空间，因此这里直接调整窗口高度，
                // 而不是只增加日期格高度。
                Height = Math.Max(MinHeight, Height + e.VerticalChange);
            }
            else
            {
                // 月视图可滚动：竖直方向直接调整窗口高度，不自动适配内容
                Height = Math.Max(MinHeight, Height + e.VerticalChange);
            }
        }

        SaveCurrentViewBounds();
        e.Handled = true;
    }

    private void ApplyViewHeightPolicy()
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_viewModel.Settings.ViewMode == CalendarViewMode.Year)
        {
            ClampYearWindowHeight();
            UpdateYearScrollLimit();
            return;
        }

        if (_viewModel.Settings.ViewMode is CalendarViewMode.Month or CalendarViewMode.Week or CalendarViewMode.Tasks)
        {
            // 月/周/任务视图窗口大小都由用户自由调整，不自动适配内容高度。
            return;
        }

        FitWindowToContent();
    }

    private void ClampYearWindowHeight()
    {
        if (_viewModel?.Settings.ViewMode != CalendarViewMode.Year)
        {
            return;
        }

        // 上限用屏幕工作区而不是"4 行理想高度"，保证用户可以自由把年视图拉大
        Height = Math.Min(Math.Max(MinHeight, Height), GetYearResizeLimit());
    }

    private double GetYearMaximumWindowHeight()
    {
        if (_viewModel is null)
        {
            return Math.Max(MinHeight, Height);
        }

        var toolbarHeight = ToolbarPanel.ActualHeight > 0 ? ToolbarPanel.ActualHeight : 30;
        var shellPadding = Shell.Padding.Top + Shell.Padding.Bottom;
        var shellBorder = Shell.BorderThickness.Top + Shell.BorderThickness.Bottom;
        var toolbarMargin = 6.0;
        var yearRows = 4.0;
        var monthCardOuterHeight = _viewModel.YearMonthHeight + 8.0;
        var resizeGripSpace = 24.0;
        var desiredHeight = shellPadding + shellBorder + toolbarHeight + toolbarMargin + yearRows * monthCardOuterHeight + resizeGripSpace + 2;
        var workAreaLimit = Math.Max(MinHeight, SystemParameters.WorkArea.Height);

        return Math.Max(MinHeight, Math.Min(desiredHeight, workAreaLimit));
    }

    /// <summary>
    /// 年视图允许拉伸到的最大高度：只受屏幕工作区限制。
    /// 与 GetYearMaximumWindowHeight 的区别：后者是"刚好显示 4 行月卡片"的理想高度（用于初次进入年视图时的自适应），
    /// 若拿它当上限，窗口一到这个高度就再也拉不大了。
    /// </summary>
    private double GetYearResizeLimit()
    {
        return Math.Max(MinHeight, SystemParameters.WorkArea.Height);
    }

    private void UpdateYearScrollLimit()
    {
        if (_viewModel?.Settings.ViewMode != CalendarViewMode.Year)
        {
            return;
        }

        var toolbarHeight = ToolbarPanel.ActualHeight > 0 ? ToolbarPanel.ActualHeight : 30;
        var shellPadding = Shell.Padding.Top + Shell.Padding.Bottom;
        var shellBorder = Shell.BorderThickness.Top + Shell.BorderThickness.Bottom;
        var toolbarMargin = 6.0;
        var resizeGripSpace = 24.0;
        YearScrollViewer.MaxHeight = Math.Max(
            120,
            Height - shellPadding - shellBorder - toolbarHeight - toolbarMargin - resizeGripSpace);
    }

    private void FitWindowToContent()
    {
        if (_viewModel is null || _viewModel.Settings.ViewMode == CalendarViewMode.Year)
        {
            return;
        }

        if (_fitWindowToContentQueued)
        {
            return;
        }

        _fitWindowToContentQueued = true;
        Dispatcher.InvokeAsync(() =>
        {
            _fitWindowToContentQueued = false;
            FitWindowToContentNow();
        }, DispatcherPriority.Render);
    }

    private void QueueFitWindowToContent()
    {
        FitWindowToContent();
    }

    private void FitWindowToContentNow()
    {
        if (_viewModel is null || _userSizing)
        {
            return;
        }

        if (_viewModel.Settings.ViewMode == CalendarViewMode.Year)
        {
            return;
        }

        if (_viewModel.Settings.ViewMode is CalendarViewMode.Month or CalendarViewMode.Week or CalendarViewMode.Tasks)
        {
            // 月/周/任务视图窗口大小都由用户自由控制。
            return;
        }

        CalendarBody.UpdateLayout();
        Shell.Measure(new Size(Math.Max(MinWidth, Width), double.PositiveInfinity));
        var desiredHeight = Math.Ceiling(Shell.DesiredSize.Height) + 1;
        Height = Math.Max(MinHeight, desiredHeight);
    }

    private async Task RefreshHolidaysForVisibleYearAsync(bool force = false)
    {
        if (_viewModel is null)
        {
            return;
        }

        var year = _viewModel.SelectedDate.Year;

        // 强制刷新 OR 缓存已过期（TTL/未联网过该年/超过同年内重拉窗口）→ 走联网路径；
        // 否则直接读缓存，避免无意义的网络请求。
        var cacheStatus = await _holidayService.GetCacheStatusAsync();
        bool needRefresh = force
            || !cacheStatus.OnlineYears.Contains(year)
            || (DateTimeOffset.Now - cacheStatus.UpdatedAt) > ChinaHolidayService.CacheTtl;

        if (!needRefresh && _loadedHolidayYear == year)
        {
            return;
        }

        IReadOnlyList<ChinaHoliday> holidays;
        bool onlineSuccess;
        try
        {
            holidays = await _holidayService.LoadAndRefreshAsync(year);
            onlineSuccess = true;
        }
        catch
        {
            // 联网失败：降级用本地缓存 + 内嵌兜底（Service 内部已处理），不影响日历显示
            onlineSuccess = false;
            holidays = await _holidayService.LoadCachedOrEmbeddedAsync(year);
        }

        if (_viewModel is null)
        {
            return;
        }

        _loadedHolidayYear = year;
        _viewModel.SetHolidays(holidays);
        ApplyViewHeightPolicy();

        // 仅在 UI 已就绪后才弹提示（避免启动阶段的初次联网失败刷屏）
        if (_isUiReady && !onlineSuccess)
        {
            ShowToast("节假日数据获取失败，已用本地缓存/兜底数据");
        }
    }

    private static bool IsInteractiveDragSource(object originalSource)
    {
        if (originalSource is not DependencyObject source)
        {
            return false;
        }

        if (FindParent<ButtonBase>(source) is not null ||
            FindParent<Slider>(source) is not null ||
            FindParent<ComboBox>(source) is not null ||
            FindParent<TextBox>(source) is not null ||
            FindParent<Thumb>(source) is not null ||
            FindParent<ScrollBar>(source) is not null)
        {
            return true;
        }

        return FindDataContext<DayCellViewModel>(source) is not null ||
               FindDataContext<TaskItemViewModel>(source) is not null ||
               FindDataContext<MonthSummaryViewModel>(source) is not null;
    }

    private static T? FindParent<T>(DependencyObject source)
        where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject source)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(source); i++)
        {
            var child = VisualTreeHelper.GetChild(source, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static T? FindDataContext<T>(DependencyObject source)
        where T : class
    {
        var current = source;
        while (current is not null)
        {
            if (current is FrameworkElement element && element.DataContext is T dataContext)
            {
                return dataContext;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private async Task SaveAsync()
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
            // 落盘失败（文件被占用/权限不足等）不致命：保留 IsDirty 以便下次重试，并记录日志
            App.LogError(ex, "SaveAsync");
        }
    }

    /// <summary>
    /// 周视图左栏超出上限、头部被裁掉 N 天时回调：内容整体上移了 N 行，
    /// 滚动偏移必须下调 N × 行高，否则用户当前看到的位置会瞬间跳变。
    /// </summary>
    private void OnWeekScrollHeadTrimmed(int trimmedDays)
    {
        if (WeekScrollViewer is null || _viewModel is null)
        {
            return;
        }

        var delta = trimmedDays * _viewModel.WeekScrollCellHeight;
        var current = WeekScrollViewer.VerticalOffset;
        ApplyWeekOffset(Math.Max(0, current - delta));
    }
}
