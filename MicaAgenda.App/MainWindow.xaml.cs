using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MicaAgenda.App.Models;
using MicaAgenda.App.Services;
using MicaAgenda.App.ViewModels;

namespace MicaAgenda.App;

internal static class FileLog
{
    [System.Diagnostics.Conditional("DEBUG")]
    public static void Write(string message)
    {
    }
}

public partial class MainWindow : Window
{
    private readonly CalendarDataStore _store = new();
    private readonly ChinaHolidayService _holidayService = new();
    private readonly AppConfigStore _configStore = new();
    private readonly object _syncRoot = new();
    private readonly DispatcherTimer _clockTimer;
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
    private bool _settingsAppliedToWindow;
    private bool _closeAfterSaveRequested;
        private bool _isExiting;
        private bool _userSizing;
        // 月视图是像素滚动，VerticalOffset / ExtentHeight / ViewportHeight 单位均为像素。
        // 距边缘不足 60px 时预加载下一批月份。
    private const double TimelineLoadThreshold = 60;
    private const double NarrowLayoutThreshold = 460.0;

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
    // 间隔压到 100ms：用户从 mica 移到 Edge 后，下一个 tick 就沉底；体感"基本无缝"。
    // 每秒 10 次只跑一次 WindowFromPoint + 一次 SetWindowPos，CPU 几乎可忽略。
    private readonly DispatcherTimer _embedWatchdog = new() { Interval = TimeSpan.FromMilliseconds(33) };

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
        FileLog.Write($"[STARTUP] MainWindow ctor - v3.1.7 - exe={Environment.ProcessPath ?? "unknown"}");
        Title = "MicaAgenda v3.1.7";

        // 窗口初始化前同步加载配置，确保桌面嵌入/锁定在首帧即生效
        _config = _configStore.Load();
        ShowActivated = !_config.EmbedDesktop;
        // 托盘图标：关闭窗口后仍可重新打开 / 打开设置 / 退出
        _trayIcon = new TrayIconService(
            showWindow: ShowWindow,
            openSettings: OpenSettings,
            exit: ExitApplication,
            hideWindow: Hide);

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
        var data = await _store.LoadAsync();
        var autoStartEnabled = _config.AutoStart;
        data.Settings.AlwaysOnTop = false;
        var holidayYear = DateOnly.FromDateTime(DateTime.Now).Year;
        var holidays = await _holidayService.LoadCachedOrEmbeddedAsync(holidayYear);
        _loadedHolidayYear = holidayYear;

        _viewModel = new MainViewModel(data, holidays: holidays, syncRoot: _syncRoot);

        // 先把内容挂上并渲染出来，首屏优先；
        // 下面那些与首屏无关的工作统一推迟到 Background 优先级，避免"启动卡两秒才显示全"。
        DataContext = _viewModel;
        ApplySettingsToWindow();
        _clockTimer.Start();
        ApplyViewHeightPolicy();

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
                    HighPriorityStartupService.SetEnabled(true);
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

        _mcpServer = new McpServer(_viewModel.Data, _syncRoot, OnDataChangedFromApi, _config.ApiToken);
        _mcpServer.Start(_config.McpPort);
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
            OnDataChangedFromApi);

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
                App.LogError(ex, "OnDataChangedFromApi");
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
            Hide();
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
                        StartEmbedWatchdog();
                    }
                    else
                    {
                        _embedWatchdog.Stop();
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

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyShellClip();
        UpdateYearScrollLimit();
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        if (NormalViewHost is null || NarrowTaskOnlyView is null || ViewControlsPanel is null)
        {
            return;
        }

        var narrow = ActualWidth > 0 && ActualWidth <= NarrowLayoutThreshold;
        ViewControlsPanel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        NormalViewHost.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        NarrowTaskOnlyView.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
    }

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
        if (_viewModel is null || MonthScrollViewer is null)
        {
            return;
        }

        if (_viewModel.Settings.ViewMode != CalendarViewMode.Month)
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
        FileLog.Write($"[SCROLL] ENTER: vp={e.ViewportHeight:F0}, ext={e.ExtentHeight:F0}, change={e.VerticalChange:F0}, extChange={e.ExtentHeightChange:F0}, progScroll={_programmaticScroll}");

        // 程序内部滚动定位（按月吸附 / 跳今天 / 锚定）不触发自动扩展
        if (_programmaticScroll)
        {
            return;
        }

        // 锁定滚动模式：时间轴完全冻结，既不自动扩展也不重建（避免"一直刷新"）
        if (_lockScroll)
        {
            FileLog.Write("[SCROLL] Skip: scroll locked by user");
            return;
        }

        if (_viewModel is null || MonthScrollViewer is null || MonthItemsControl is null)
        {
            FileLog.Write($"[SCROLL] Skip: null guard - vm={_viewModel is not null}, viewer={MonthScrollViewer is not null}, items={MonthItemsControl is not null}");
            return;
        }

        if (_viewModel.Settings.ViewMode != CalendarViewMode.Month)
        {
            FileLog.Write($"[SCROLL] Skip: not month view, mode={_viewModel.Settings.ViewMode}");
            return;
        }

        // 内容不足以滚动时不做扩展：此时 VerticalOffset 恒为 0，
        // 会一直满足"到达顶部"的条件而反复扩展。
        if (e.ExtentHeight <= e.ViewportHeight + 1)
        {
            FileLog.Write($"[SCROLL] Skip: content fits viewport (ext={e.ExtentHeight:F0} <= vp={e.ViewportHeight:F0}+1), months={_viewModel.TimelineMonths.Count}");
            return;
        }

        FileLog.Write($"[SCROLL] ScrollChanged: offset={e.VerticalOffset:F0}, change={e.VerticalChange:F0}, extH={e.ExtentHeight:F0}, extChange={e.ExtentHeightChange:F0}, vp={e.ViewportHeight:F0}, months={_viewModel.TimelineMonths.Count}");

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
            FileLog.Write($"[SCROLL] Skip: cooldown active ({(DateTime.Now - _lastTimelineExtendAt).TotalMilliseconds:F0}ms < {TimelineExtendCooldown.TotalMilliseconds}ms)");
            return;
        }

        FileLog.Write($"[SCROLL] Check edges: offset={e.VerticalOffset:F0}, vp={e.ViewportHeight:F0}, ext={e.ExtentHeight:F0}, diff={e.ExtentHeight - e.VerticalOffset - e.ViewportHeight:F0}, months={_viewModel.TimelineMonths.Count}");

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
            FileLog.Write($"[SCROLL] ExtendTimeline: Early return - vm={_viewModel is not null}, months={_viewModel?.TimelineMonths.Count}, viewer={MonthScrollViewer is not null}");
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
            FileLog.Write($"[SCROLL] ExtendTimeline: Cooldown active, pending={direction}");
            return;
        }

        FileLog.Write($"[SCROLL] ExtendTimeline: Executing dir={direction}, months={_viewModel.TimelineMonths.Count}, userInit={userInitiated}");
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
        FileLog.Write($"[SCROLL] PerformTimelineExtend: dir={direction}, months {monthsBefore}->{monthsAfter}, anchor={anchor.Year}/{anchor.Month}, prevOffset={previousOffset:F0}");

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
                    FileLog.Write($"[SCROLL] PerformTimelineExtend: container is null for anchor {anchor.Year}/{anchor.Month}");
                    return;
                }

                var point = container.TransformToVisual(MonthItemsControl).Transform(new System.Windows.Point(0, 0));
                var target = direction < 0 ? point.Y + previousOffset : previousOffset;
                FileLog.Write($"[SCROLL] PerformTimelineExtend: anchorY={point.Y:F0}, target={target:F0}");
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

        FileLog.Write($"[SCROLL_ADJ] current={current.Year}/{current.Month}, index={index}, targetIndex={targetIndex}, count={_viewModel.TimelineMonths.Count}, offset={MonthScrollViewer.VerticalOffset:F0}");

        if (targetIndex < 0 || targetIndex >= _viewModel.TimelineMonths.Count)
        {
            // 到边界了：必须走 ExtendTimeline 这条唯一入口去加载新月份。
            // 早先这里自己调 ExtendTimelineBack/Forward，与 ScrollChanged 的自动扩展形成
            // 两条竞争路径，两边各插各的块，索引错乱就会"往上滑却跳到去年"。
            // 注意：不再做 TimelineMaxMonths 硬拦截，允许窗口"滑动"到任意月份
            // （上限由 ViewModel.TrimTimeline 在追加后裁掉来保证）。

            FileLog.Write($"[SCROLL_ADJ] At boundary, calling ExtendTimeline(dir={dir})");
            ExtendTimeline(dir, userInitiated: true);
            // 扩展完成后再由下一帧定位到新月份：让 ExtendTimeline 先把偏移校正完
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_viewModel is null || MonthItemsControl is null)
                {
                    return;
                }

                var next = _viewModel.TimelineMonths.IndexOf(current) + dir;
                FileLog.Write($"[SCROLL_ADJ] Deferred scroll: next={next}, count={_viewModel.TimelineMonths.Count}");
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
    /// 启动桌面嵌入看门狗。SourceInitialized 与 Loaded 都会尝试启动，
    /// 这里做幂等保护——否则定时器会被订阅两次，每轮重复调用 SetWindowPos，
    /// 还会白白多持有一份窗口引用。
    /// </summary>
    private bool _embedWatchdogHooked;
    private IntPtr _embedHandle;
    private void StartEmbedWatchdog()
    {
        if (!_config.EmbedDesktop)
        {
            return;
        }

        // Tick 只挂载一次：嵌入桌面可能被反复开关（设置里勾选/取消），
        // 若每次 Start 都挂一次，会累积多个 Tick 处理器重复压底。
        if (!_embedWatchdogHooked)
        {
            _embedWatchdogHooked = true;
            _embedWatchdog.Tick += (_, _) =>
        {
            try
            {
                // 普通嵌入桌面的目标是“始终沉在其他窗口之下”。这里不做悬停豁免，
                // 每个 tick 都强制压底，避免点击后窗口被系统拉到最上面。
                DesktopEmbedService.EnsureEmbedded(this);
            }
            catch (Exception ex)
            {
                // 定时器回调里的异常会直接终止程序，必须兜底
                App.LogError(ex, "EmbedWatchdog");
            }
        };
        }

        _embedWatchdog.Start();
    }

    /// <summary>
    /// 按当前配置执行一次桌面嵌入。
    /// </summary>
    private void ApplyDesktopEmbed()
    {
        if (_config.EmbedDesktop)
        {
            DesktopEmbedService.EmbedToDesktop(this);
            DesktopEmbedService.SetNoActivateStyle(this, true);
            StartEmbedWatchdog();
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
    // 看门狗在悬停期间不会把窗口压回去（见 StartEmbedWatchdog）。
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
        await SwitchViewModeAsync(CalendarViewMode.Year);
    }

    private async void MonthView_Click(object sender, RoutedEventArgs e)
    {
        await SwitchViewModeAsync(CalendarViewMode.Month);
    }

    private async void TitleText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        await SwitchViewModeAsync(CalendarViewMode.Month);
    }

    private async void WeekView_Click(object sender, RoutedEventArgs e)
    {
        await SwitchViewModeAsync(CalendarViewMode.Week);
    }

    private async void Previous_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.MovePrevious();
        await RefreshHolidaysForVisibleYearAsync();
        ApplyViewHeightPolicy();
        await SaveAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.MoveNext();
        await RefreshHolidaysForVisibleYearAsync();
        ApplyViewHeightPolicy();
        await SaveAsync();
    }

    private async void Today_Click(object sender, RoutedEventArgs e)
    {
        _viewModel?.GoToday();
        ScrollToToday();
        await RefreshHolidaysForVisibleYearAsync();
        ApplyViewHeightPolicy();
        await SaveAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        // 点关闭按钮 = 隐藏到托盘，不退出；托盘菜单可重新打开或退出
        Hide();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        OpenSettings();
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
        if (e.ClickCount >= 2 && (sender as FrameworkElement)?.DataContext is TaskItemViewModel task)
        {
            BeginTaskEdit(task);
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
            await PushCompletionToMindMapAsync(task);
        }
    }

    private async Task PushCompletionToMindMapAsync(TaskItemViewModel task)
    {
        if (!_config.SyncMyMindMapEnabled || string.IsNullOrWhiteSpace(_config.MyMindMapToken)) return;
        if (!task.Title.StartsWith("[MM复习]", StringComparison.OrdinalIgnoreCase)
            && !task.Title.StartsWith("[复习]", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            // 通过同步服务推送，携带 UpdatedAt（状态最后变更时间），供对端做时间戳仲裁
            if (_mindMapSyncService is null)
            {
                StartMindMapSyncService();
            }
            if (_mindMapSyncService is not null)
            {
                await _mindMapSyncService.PushStatusAsync(_config.MyMindMapToken, task.Model);
            }
        }
        catch
        {
            // 静默：my-mindmap agent 未启动/Token 错误时不影响日历本机操作
        }
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
            _ = PushCompletionToMindMapAsync(task);
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
        _ = PushCompletionToMindMapAsync(task);
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
        _ = PushCompletionToMindMapAsync(task);
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
            await PushCompletionToMindMapAsync(task);
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

    private async void BackgroundModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingSettings || _viewModel is null || BackgroundModeBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (Enum.TryParse<CalendarBackgroundMode>(item.Tag?.ToString(), out var mode))
        {
            _viewModel.Settings.BackgroundMode = mode;
            ApplyBackground();
            ApplyViewHeightPolicy();
            await SaveAsync();
        }
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
            var bounds = GetBoundsForView(settings.ViewMode);
            ApplyWindowBounds(bounds);
            Topmost = false;
            OpacitySlider.Value = settings.Opacity;
            SelectBackgroundMode(settings.BackgroundMode);
            ApplyBackground();
            ApplyViewHeightPolicy();
        }
        finally
        {
            _settingsAppliedToWindow = true;
            _isApplyingSettings = false;
        }
    }

    private void SelectBackgroundMode(CalendarBackgroundMode mode)
    {
        if (mode == CalendarBackgroundMode.ClearBorder)
        {
            mode = CalendarBackgroundMode.None;
            if (_viewModel is not null)
            {
                _viewModel.Settings.BackgroundMode = mode;
            }
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
    }

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
                _viewModel.Settings.MonthWindowBounds = bounds with { Left = 0, Top = 0 };
                break;
            case CalendarViewMode.Week:
                _viewModel.Settings.WeekWindowBounds = bounds with { Left = 0, Top = 0 };
                break;
            case CalendarViewMode.Year:
                // 按实际高度保存：若在这里再钳一次，用户拉大年视图后会被压回理想高度
                _viewModel.Settings.YearWindowBounds = bounds with
                {
                    Left = 0,
                    Top = 0,
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

        var settings = _viewModel.Settings;
        // 旧版本数据 / 反序列化失败时 WindowBounds 可能为 null，必须兜底，
        // 否则下面和 ApplyWindowBounds 里解引用会抛 NullReferenceException。
        var fallback = settings.WindowBounds ?? new WindowBounds(120, 90, 980, 680);
        var positionSource = _settingsAppliedToWindow && IsLoaded
            ? new WindowBounds(Left, Top, Math.Max(MinWidth, Width), Math.Max(MinHeight, Height))
            : fallback;
        var sizeFallback = _settingsAppliedToWindow && IsLoaded
            ? positionSource
            : fallback;
        var sizeSource = viewMode switch
        {
            CalendarViewMode.Month => settings.MonthWindowBounds ?? fallback,
            CalendarViewMode.Week => settings.WeekWindowBounds ?? sizeFallback,
            CalendarViewMode.Year => settings.YearWindowBounds ?? new WindowBounds(
                0,
                0,
                sizeFallback.Width,
                GetYearMaximumWindowHeight()),
            _ => fallback
        };

        return new WindowBounds(positionSource.Left, positionSource.Top, sizeSource.Width, sizeSource.Height);
    }

    private void ApplyWindowBounds(WindowBounds bounds)
    {
        var workArea = SystemParameters.WorkArea;
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
        Left = Math.Clamp(bounds.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        Top = Math.Clamp(bounds.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        UpdateYearScrollLimit();
    }

    private void ApplyBackground()
    {
        if (_viewModel is null)
        {
            return;
        }

        var settings = _viewModel.Settings;

        // 透明度下限降到约 2%（原 80/255 不够透），让背景几乎全透明、仅文字与边框可见
        var alpha = (byte)Math.Clamp(settings.Opacity * 255, 6, 255);

        Shell.Background = settings.BackgroundMode switch
        {
            CalendarBackgroundMode.None => Brushes.Transparent,
            CalendarBackgroundMode.ClearBorder => Brushes.Transparent,
            CalendarBackgroundMode.Transparent => BrushFromArgb((byte)Math.Min((int)alpha, 150), 255, 255, 255),
            CalendarBackgroundMode.Solid => new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)),
            CalendarBackgroundMode.FrostedGray => BrushFromArgb(alpha, 225, 229, 235),
            CalendarBackgroundMode.FrostedDark => BrushFromArgb((byte)Math.Min((int)alpha, 210), 28, 31, 36),
            CalendarBackgroundMode.AcrylicBlue => BrushFromArgb((byte)Math.Min((int)alpha, 210), 219, 234, 254),
            CalendarBackgroundMode.AcrylicMint => BrushFromArgb((byte)Math.Min((int)alpha, 210), 209, 250, 229),
            CalendarBackgroundMode.PaperLight => BrushFromArgb(alpha, 250, 248, 242),
            CalendarBackgroundMode.Graphite => BrushFromArgb(alpha, 17, 24, 39),
            _ => BrushFromArgb(alpha, 255, 255, 255)
        };

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
        var noBackground = mode is CalendarBackgroundMode.None or CalendarBackgroundMode.ClearBorder;
        var dark = IsDarkMode(mode);

        Resources["PrimaryTextBrush"] = dark ? BrushFromArgb(255, 243, 244, 246) : BrushFromArgb(255, 17, 24, 39);
        Resources["MutedTextBrush"] = dark ? BrushFromArgb(255, 209, 213, 219) : BrushFromArgb(255, 75, 85, 99);
        Resources["CellBorderBrush"] = dark ? BrushFromArgb(70, 255, 255, 255) : BrushFromArgb(38, 17, 24, 39);
        Resources["TaskBorderBrush"] = dark ? BrushFromArgb(90, 255, 255, 255) : BrushFromArgb(55, 17, 24, 39);
        Resources["InlineInputBackgroundBrush"] = dark ? BrushFromArgb(210, 31, 41, 55) : BrushFromArgb(225, 255, 255, 255);
        Resources["TodayCellBackgroundBrush"] = dark ? BrushFromArgb(125, 30, 64, 175) : BrushFromArgb(190, 219, 234, 254);
        Resources["TodayCellBorderBrush"] = dark ? BrushFromArgb(190, 147, 197, 253) : BrushFromArgb(170, 37, 99, 235);
        Resources["ImportantTaskBackgroundBrush"] = dark ? BrushFromArgb(150, 127, 29, 29) : BrushFromArgb(230, 254, 202, 202);
        Resources["ImportantTaskBorderBrush"] = dark ? BrushFromArgb(210, 248, 113, 113) : BrushFromArgb(255, 239, 68, 68);
        Resources["ImportantTaskTextBrush"] = dark ? BrushFromArgb(255, 252, 165, 165) : BrushFromArgb(255, 185, 28, 28);
        Resources["HolidayBreakBrush"] = dark ? BrushFromArgb(130, 127, 29, 29) : BrushFromArgb(230, 254, 226, 226);
        Resources["HolidayWorkBrush"] = dark ? BrushFromArgb(130, 30, 64, 175) : BrushFromArgb(230, 219, 234, 254);
        Resources["HolidayTextBrush"] = dark ? BrushFromArgb(255, 254, 202, 202) : BrushFromArgb(255, 153, 27, 27);
        Resources["HolidayWorkTextBrush"] = dark ? BrushFromArgb(255, 191, 219, 254) : BrushFromArgb(255, 29, 78, 216);
        Resources["WindowEdgeBrush"] = mode switch
        {
            CalendarBackgroundMode.None => BrushFromArgb(34, 255, 255, 255),
            CalendarBackgroundMode.ClearBorder => BrushFromArgb(38, 255, 255, 255),
            CalendarBackgroundMode.FrostedDark or CalendarBackgroundMode.Graphite => BrushFromArgb(42, 255, 255, 255),
            _ => BrushFromArgb(30, 17, 24, 39)
        };
        Resources["ToolbarControlBackgroundBrush"] = mode switch
        {
            CalendarBackgroundMode.None or CalendarBackgroundMode.ClearBorder => BrushFromArgb(34, 255, 255, 255),
            CalendarBackgroundMode.FrostedDark or CalendarBackgroundMode.Graphite => BrushFromArgb(95, 17, 24, 39),
            CalendarBackgroundMode.AcrylicBlue => BrushFromArgb(125, 219, 234, 254),
            CalendarBackgroundMode.AcrylicMint => BrushFromArgb(125, 209, 250, 229),
            CalendarBackgroundMode.PaperLight => BrushFromArgb(155, 255, 251, 235),
            _ => BrushFromArgb(125, 255, 255, 255)
        };
        Resources["ToolbarControlBorderBrush"] = dark
            ? BrushFromArgb(70, 255, 255, 255)
            : BrushFromArgb(42, 17, 24, 39);
        Resources["ToolbarControlHoverBrush"] = dark
            ? BrushFromArgb(130, 31, 41, 55)
            : BrushFromArgb(170, 255, 255, 255);
        Resources["ToolbarControlPressedBrush"] = dark
            ? BrushFromArgb(160, 55, 65, 81)
            : BrushFromArgb(185, 229, 231, 235);
        Resources["ToolbarTrackBrush"] = dark
            ? BrushFromArgb(90, 255, 255, 255)
            : BrushFromArgb(85, 17, 24, 39);
        Resources["ToolbarThumbBrush"] = dark
            ? BrushFromArgb(225, 243, 244, 246)
            : BrushFromArgb(210, 31, 41, 55);

        if (noBackground)
        {
            Resources["DayCellBackgroundBrush"] = Brushes.Transparent;
            Resources["DayCellOutMonthBackgroundBrush"] = Brushes.Transparent;
            Resources["YearMonthBackgroundBrush"] = Brushes.Transparent;
            Resources["YearCellBackgroundBrush"] = Brushes.Transparent;
            Resources["TaskBackgroundBrush"] = dark ? BrushFromArgb(120, 31, 41, 55) : BrushFromArgb(130, 236, 253, 245);
            Resources["TodayPanelBackgroundBrush"] = dark ? BrushFromArgb(215, 36, 42, 52) : BrushFromArgb(225, 255, 255, 255);
            Resources["TodayPanelBorderBrush"] = dark ? BrushFromArgb(150, 255, 255, 255) : BrushFromArgb(100, 17, 24, 39);
            Resources["SelectedCellBackgroundBrush"] = dark ? BrushFromArgb(120, 59, 130, 246) : BrushFromArgb(102, 37, 99, 235);
            return;
        }

        if (dark)
        {
            Resources["DayCellBackgroundBrush"] = BrushFromArgb(95, 31, 41, 55);
            Resources["DayCellOutMonthBackgroundBrush"] = BrushFromArgb(45, 31, 41, 55);
            Resources["YearMonthBackgroundBrush"] = BrushFromArgb(105, 31, 41, 55);
            Resources["YearCellBackgroundBrush"] = BrushFromArgb(70, 31, 41, 55);
            Resources["TaskBackgroundBrush"] = BrushFromArgb(135, 55, 65, 81);
            // 右上面板在深色背景下需要更高的不透明度，避免和深色壁纸/卡片融在一起看不清
            Resources["TodayPanelBackgroundBrush"] = BrushFromArgb(225, 30, 36, 46);
            Resources["TodayPanelBorderBrush"] = BrushFromArgb(140, 255, 255, 255);
            // 选中态在深色背景下需要更高的不透明度才能从深灰卡片里凸显出来
            Resources["SelectedCellBackgroundBrush"] = BrushFromArgb(150, 59, 130, 246);
            return;
        }

        Resources["DayCellBackgroundBrush"] = mode switch
        {
            CalendarBackgroundMode.AcrylicBlue => BrushFromArgb(165, 239, 246, 255),
            CalendarBackgroundMode.AcrylicMint => BrushFromArgb(165, 236, 253, 245),
            CalendarBackgroundMode.FrostedGray => BrushFromArgb(155, 243, 244, 246),
            CalendarBackgroundMode.PaperLight => BrushFromArgb(180, 255, 251, 235),
            _ => BrushFromArgb(175, 255, 255, 255)
        };
        Resources["DayCellOutMonthBackgroundBrush"] = BrushFromArgb(92, 255, 255, 255);
        Resources["YearMonthBackgroundBrush"] = BrushFromArgb(145, 255, 255, 255);
        Resources["YearCellBackgroundBrush"] = BrushFromArgb(85, 255, 255, 255);
        Resources["TaskBackgroundBrush"] = mode switch
        {
            CalendarBackgroundMode.AcrylicBlue => BrushFromArgb(190, 219, 234, 254),
            CalendarBackgroundMode.AcrylicMint => BrushFromArgb(190, 209, 250, 229),
            CalendarBackgroundMode.PaperLight => BrushFromArgb(190, 254, 243, 199),
            _ => BrushFromArgb(186, 233, 247, 239)
        };
        Resources["TodayPanelBackgroundBrush"] = mode switch
        {
            CalendarBackgroundMode.AcrylicBlue => BrushFromArgb(190, 239, 246, 255),
            CalendarBackgroundMode.AcrylicMint => BrushFromArgb(190, 236, 253, 245),
            CalendarBackgroundMode.FrostedGray => BrushFromArgb(180, 243, 244, 246),
            CalendarBackgroundMode.PaperLight => BrushFromArgb(200, 255, 251, 235),
            _ => BrushFromArgb(175, 255, 255, 255)
        };
        Resources["TodayPanelBorderBrush"] = BrushFromArgb(60, 17, 24, 39);
        Resources["SelectedCellBackgroundBrush"] = BrushFromArgb(102, 37, 99, 235);
    }

    private static bool IsDarkMode(CalendarBackgroundMode mode)
    {
        return mode is CalendarBackgroundMode.FrostedDark or CalendarBackgroundMode.Graphite;
    }

    private static SolidColorBrush BrushFromArgb(byte a, byte r, byte g, byte b)
    {
        return new SolidColorBrush(Color.FromArgb(a, r, g, b));
    }

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

        if (_viewModel.Settings.ViewMode is CalendarViewMode.Month or CalendarViewMode.Week)
        {
            // 月/周视图窗口大小都由用户自由调整，不自动适配内容高度。
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

        if (_viewModel.Settings.ViewMode is CalendarViewMode.Month or CalendarViewMode.Week)
        {
            // 月/周视图窗口大小都由用户自由控制。
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
            FileLog.Write("[HOLIDAY] online refresh failed, using local fallback");
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
            // 落盘失败（文件被占用/权限不足等）不致命：保留 IsDirty 以便下次重试，并记录日志
            App.LogError(ex, "SaveAsync");
        }
    }
}
