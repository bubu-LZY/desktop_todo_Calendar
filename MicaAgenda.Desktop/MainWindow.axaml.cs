using System;
using System.Linq;
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
            DataContext = _viewModel;

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
            var hwnd = NativeHandle();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            DesktopEmbedService.HideFromTaskbar(hwnd);

            if (_config.EmbedDesktop)
            {
                DesktopEmbedService.EmbedToDesktop(hwnd);
                StartEmbedWatchdog();
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

    private void StartEmbedWatchdog()
    {
        if (!OperatingSystem.IsWindows() || !_config.EmbedDesktop)
        {
            return;
        }

        if (!_embedWatchdogHooked)
        {
            _embedWatchdogHooked = true;
            _embedWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _embedWatchdog.Tick += (_, _) =>
            {
                try
                {
                    var hwnd = NativeHandle();
                    if (hwnd != IntPtr.Zero)
                    {
                        DesktopEmbedService.EnsureEmbedded(hwnd);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error(ex, "EmbedWatchdog");
                }
            };
        }

        _embedWatchdog?.Start();
    }

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
            if (!config.EmbedDesktop)
            {
                _embedWatchdog?.Stop();
            }
        }

        ApplyDesktopEmbed();
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

        _apiServer = new TaskApiServer(_viewModel.Data, _syncRoot, _config.ApiToken, OnDataChangedFromApi);
        _apiServer.Start(_config.ApiPort);
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

    /// <summary>设置窗口「立即同步」入口（供 #44 设置面板调用）。</summary>
    internal async System.Threading.Tasks.Task<string> SyncMindMapNowAsync()
    {
        if (_mindMapSyncService is null)
        {
            TryStart(StartMindMapSyncService, "MindMapSync");
        }

        return _mindMapSyncService is null
            ? "同步服务不可用"
            : await _mindMapSyncService.SyncNowAsync();
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
        }
    }

    private void IncompleteTask_Click(object? sender, RoutedEventArgs e)
    {
        if (TaskFrom(sender) is { IsCompleted: true } task)
        {
            _viewModel?.ToggleTaskCompletion(task.Id);
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

        // 等新输入框进入可视树后再抢焦点
        if (sender is Control ctl)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var border = ctl.FindAncestorOfType<Border>();
                var box = border?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
                box?.Focus();
            }, DispatcherPriority.Loaded);
        }
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
        if (sender is Control ctl)
        {
            Dispatcher.UIThread.Post(() =>
            {
                var panel = ctl.FindAncestorOfType<Border>();
                var box = panel?.GetVisualDescendants().OfType<TextBox>()
                    .FirstOrDefault(t => Equals(t.Tag, "TodayTaskDraftBox"));
                box?.Focus();
            }, DispatcherPriority.Loaded);
        }
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