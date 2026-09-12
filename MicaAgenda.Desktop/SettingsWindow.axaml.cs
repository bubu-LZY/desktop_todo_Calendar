using System;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MicaAgenda.App.Helpers;
using MicaAgenda.App.Models;
using MicaAgenda.App.Services;

namespace MicaAgenda.Desktop;

/// <summary>
/// 设置窗口（Avalonia 版，对齐 WPF SettingsWindow 的配置项与动作）。
/// 与 WPF 的差异：用内联状态文字替代 Toast；用 Avalonia 模态窗替代 MessageBox；
/// 用 StorageProvider 替代 Win32 文件对话框；开机自启（注册表/schtasks，Windows 专属）未移植。
/// </summary>
public partial class SettingsWindow : Window
{
    private AppConfig _config = null!;
    private AppConfigStore _configStore = null!;
    private Func<string> _apiBaseProvider = null!;
    private Func<string> _mcpEndpointProvider = null!;
    private BackupService _backupService = null!;
    private ChinaHolidayService _holidayService = null!;
    private ReportService _reportService = null!;

    /// <summary>保存后触发，参数为更新后的配置（主窗体据此重启服务 / 应用嵌入锁定）。</summary>
    public Action<AppConfig>? ApplyRequested { get; set; }

    /// <summary>导入任务后触发，主窗体刷新界面。</summary>
    public Action? TasksImported { get; set; }

    /// <summary>手动刷新节假日后触发，主窗体重新拉数据。</summary>
    public Action? HolidayRefreshRequested { get; set; }

    /// <summary>请求主窗体清空所有任务，返回删除数。</summary>
    public Func<int>? DeleteAllTasksRequested { get; set; }

    /// <summary>请求主窗体立即执行一次 my-mindmap 同步，返回结果文本。</summary>
    public Func<Task<string>>? MindMapSyncRequested { get; set; }

    // Avalonia XAML 编译器要求根类型存在公共无参构造函数（仅供编译期/设计期）；
    // 运行时一律使用下面的有参构造注入依赖。
    public SettingsWindow()
    {
        InitializeComponent();
    }

    public SettingsWindow(
        AppConfig config,
        AppConfigStore configStore,
        Func<string> apiBaseProvider,
        BackupService backupService,
        Func<string> mcpEndpointProvider,
        ChinaHolidayService holidayService,
        ReportService reportService)
    {
        InitializeComponent();
        _config = config;
        _configStore = configStore;
        _apiBaseProvider = apiBaseProvider;
        _backupService = backupService;
        _mcpEndpointProvider = mcpEndpointProvider;
        _holidayService = holidayService;
        _reportService = reportService;

        LoadValues();
        _ = LoadHolidayStatusAsync();
    }

    private void LoadValues()
    {
        ApiEnabledBox.IsChecked = _config.ApiEnabled;
        ApiPortBox.Text = _config.ApiPort.ToString();
        ApiTokenBox.Text = _config.ApiToken;
        ReminderEnabledBox.IsChecked = _config.ReminderEnabled;
        ReminderTimeBox.Text = _config.ReminderTime;
        FeishuWebhookBox.Text = _config.FeishuWebhook;
        WeComWebhookBox.Text = _config.WeComWebhook;
        BackupEnabledBox.IsChecked = _config.BackupEnabled;
        BackupTimeBox.Text = _config.BackupTime;
        BackupSendFeishuBox.IsChecked = _config.BackupSendToFeishu;
        BackupSendWeComBox.IsChecked = _config.BackupSendToWeCom;
        McpEnabledBox.IsChecked = _config.McpEnabled;
        McpPortBox.Text = _config.McpPort.ToString();
        SyncMyMindMapBox.IsChecked = _config.SyncMyMindMapEnabled;
        MindMapBaseUrlBox.Text = string.IsNullOrWhiteSpace(_config.MindMapBaseUrl) ? "http://127.0.0.1:17800" : _config.MindMapBaseUrl;
        MyMindMapTokenBox.Text = _config.MyMindMapToken;
        EmbedDesktopBox.IsChecked = _config.EmbedDesktop;
        LockWindowBox.IsChecked = _config.LockWindow;
        LoadReportValues();
        UpdateApiHint();
        UpdateMcpEndpoint();
    }

    // ===== 定时报告 =====

    private void LoadReportValues()
    {
        ReportDayOfWeekBox.ItemsSource = new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
        ReportDayOfMonthBox.ItemsSource = Enumerable.Range(1, 28).Select(d => d.ToString()).ToList();

        ReportEnabledBox.IsChecked = _config.ReportEnabled;
        ReportTimeBox.Text = _config.ReportTime;
        ReportSendFeishuBox.IsChecked = _config.ReportSendToFeishu;
        ReportSendWeComBox.IsChecked = _config.ReportSendToWeCom;
        ReportCustomWebhookBox.Text = _config.ReportCustomWebhook;

        var monthly = string.Equals(_config.ReportSchedule, "Monthly", StringComparison.OrdinalIgnoreCase);
        ReportMonthlyRadio.IsChecked = monthly;
        ReportWeeklyRadio.IsChecked = !monthly;

        ReportDayOfWeekBox.SelectedIndex = Math.Clamp(_config.ReportDayOfWeek, 1, 7) - 1;
        ReportDayOfMonthBox.SelectedIndex = Math.Clamp(_config.ReportDayOfMonth, 1, 28) - 1;

        SyncScheduleControls();
    }

    private void SyncScheduleControls()
    {
        var monthly = ReportMonthlyRadio.IsChecked == true;
        ReportDayOfWeekBox.IsEnabled = !monthly;
        ReportDayOfMonthBox.IsEnabled = monthly;
    }

    private void ReportSchedule_Changed(object? sender, RoutedEventArgs e) => SyncScheduleControls();

    private void ReportPreview_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            ReportPreviewBox.Text = _reportService.Preview();
            ReportPreviewBox.IsVisible = true;
            ReportActionStatus.Text = "预览已生成（内容取自当前数据，未推送）。";
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "SettingsWindow.ReportPreview");
            ReportActionStatus.Text = "预览失败：" + ex.Message;
        }
    }

    private async void ReportSendNow_Click(object? sender, RoutedEventArgs e)
    {
        // 立即发送用界面上未保存的值，先写回内存配置，免得按旧配置走空渠道。
        if (!TryApplyReportValues(showErrors: false))
        {
            return;
        }

        ReportSendNowButton.IsEnabled = false;
        ReportActionStatus.Text = "正在推送…";
        try
        {
            var (sent, message) = await _reportService.RunOnceAsync();
            ReportActionStatus.Text = message;
            ReportActionStatus.Foreground = sent ? Brushes.Green : Brushes.IndianRed;
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "SettingsWindow.ReportSendNow");
            ReportActionStatus.Text = "推送异常：" + ex.Message;
        }
        finally
        {
            ReportSendNowButton.IsEnabled = true;
        }
    }

    private bool TryApplyReportValues(bool showErrors)
    {
        var timeText = ReportTimeBox.Text?.Trim() ?? string.Empty;
        if (ReportEnabledBox.IsChecked == true && !TimeOnly.TryParse(timeText, out _))
        {
            ReportFail(showErrors, "报告发送时间格式应为 HH:mm，例如 18:00");
            return false;
        }

        var customHook = ReportCustomWebhookBox.Text?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(customHook) && !IsHttpUrl(customHook))
        {
            ReportFail(showErrors, "自定义 webhook 必须是 http:// 或 https:// 开头的完整地址。");
            return false;
        }

        _config.ReportEnabled = ReportEnabledBox.IsChecked == true;
        _config.ReportTime = timeText;
        _config.ReportSchedule = ReportMonthlyRadio.IsChecked == true ? "Monthly" : "Weekly";
        _config.ReportDayOfWeek = Math.Clamp(ReportDayOfWeekBox.SelectedIndex + 1, 1, 7);
        _config.ReportDayOfMonth = Math.Clamp(ReportDayOfMonthBox.SelectedIndex + 1, 1, 31);
        _config.ReportSendToFeishu = ReportSendFeishuBox.IsChecked == true;
        _config.ReportSendToWeCom = ReportSendWeComBox.IsChecked == true;
        _config.ReportCustomWebhook = customHook;
        return true;
    }

    private void ReportFail(bool showErrors, string message)
    {
        if (showErrors)
        {
            _ = ShowInfoAsync("输入错误", message);
        }
        else
        {
            ReportActionStatus.Text = message;
        }
    }

    private void UpdateMcpEndpoint() => McpEndpointBox.Text = _mcpEndpointProvider();

    private void UpdateApiHint()
    {
        var baseUrl = _apiBaseProvider();
        ApiHint.Text = $"Agent 调用示例：curl -H \"X-Auth-Token: <token>\" {baseUrl}/api/tasks?range=today";
    }

    // ===== Token / 复制 =====

    private async void CopyToken_Click(object? sender, RoutedEventArgs e)
        => await CopyToClipboardAsync(ApiTokenBox.Text ?? string.Empty, "Token");

    private async void RegenerateToken_Click(object? sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("确认", "重新生成 Token 后，旧的 Token 将立即失效，需要重新告知所有调用方。确定重新生成？"))
        {
            ApiTokenBox.Text = Guid.NewGuid().ToString("N");
        }
    }

    private async void CopySkillDoc_Click(object? sender, RoutedEventArgs e)
    {
        var text = McpDocsProvider.BuildSkillDoc((McpEndpointBox.Text ?? string.Empty).Trim(), _config.ApiToken);
        await CopyToClipboardAsync(text, "Skill 文档");
    }

    private async void CopyMcpConfig_Click(object? sender, RoutedEventArgs e)
    {
        var text = McpDocsProvider.BuildMcpConfigJson((McpEndpointBox.Text ?? string.Empty).Trim(), _config.ApiToken);
        await CopyToClipboardAsync(text, "MCP 配置 JSON");
    }

    /// <summary>
    /// 复制内容：Avalonia 12 的剪贴板 API（IDataTransferItem 模型）较底层且各版本有差异，
    /// 这里统一用「弹出可全选文本框，用户按 Ctrl+C 复制」的稳妥方式，保证任何平台都能拿到内容。
    /// </summary>
    private async Task CopyToClipboardAsync(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 220,
            FontFamily = new FontFamily("Consolas,Microsoft YaHei")
        };
        var close = new Button { Content = "关闭", MinWidth = 72 };
        var win = new Window
        {
            Title = $"复制{label}",
            Width = 520,
            Height = 340,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true,
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = $"以下为{label}内容，已全选，按 Ctrl+C 复制：", TextWrapping = TextWrapping.Wrap },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { close }
                    }
                }
            }
        };

        close.Click += (_, _) => win.Close();
        win.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await win.ShowDialog(this);
    }

    // ===== 备份 =====

    private async void BackupNow_Click(object? sender, RoutedEventArgs e)
    {
        BackupNowButton.IsEnabled = false;
        try
        {
            var result = await _backupService.RunBackupAsync();
            var sb = new StringBuilder();
            sb.AppendLine("备份完成！");
            if (!string.IsNullOrWhiteSpace(result.LocalPath))
            {
                sb.AppendLine($"本地文件：{result.LocalPath}");
            }

            if (result.FeishuSent)
            {
                sb.AppendLine("飞书：已推送");
            }

            if (result.WeComSent)
            {
                sb.AppendLine("企业微信：已推送");
            }

            if (result.Errors.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("以下渠道失败：");
                foreach (var err in result.Errors)
                {
                    sb.AppendLine($"- {err}");
                }
            }

            await ShowInfoAsync("备份", sb.ToString());
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "SettingsWindow.BackupNow");
            await ShowInfoAsync("备份失败", ex.Message);
        }
        finally
        {
            BackupNowButton.IsEnabled = true;
        }
    }

    private void OpenBackupFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = BackupService.GetBackupDirectory();
            if (!System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            OpenExternal(dir);
        }
        catch (Exception ex)
        {
            _ = ShowInfoAsync("错误", $"打开文件夹失败：{ex.Message}");
        }
    }

    private async void ExportJson_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "导出任务 JSON",
                SuggestedFileName = $"micaagenda-tasks-{DateTime.Now:yyyyMMdd}.json",
                DefaultExtension = "json",
                FileTypeChoices = new[] { new FilePickerFileType("JSON 文件") { Patterns = new[] { "*.json" } } }
            });

            if (file is null)
            {
                return;
            }

            var json = _backupService.ExportYearJson(DateTime.Now.Year);
            var path = file.Path.LocalPath;
            await System.IO.File.WriteAllTextAsync(path, json, new UTF8Encoding(false));
            await ShowInfoAsync("导出成功", $"已导出到：{path}");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "SettingsWindow.ExportJson");
            await ShowInfoAsync("导出失败", ex.Message);
        }
    }

    private async void ImportJson_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "导入任务 JSON",
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType("JSON 文件") { Patterns = new[] { "*.json" } } }
            });

            var file = files.FirstOrDefault();
            if (file is null)
            {
                return;
            }

            var json = await System.IO.File.ReadAllTextAsync(file.Path.LocalPath);
            var added = _backupService.ImportJson(json);
            TasksImported?.Invoke();
            await ShowInfoAsync("导入完成", $"新增 {added} 条任务。");
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "SettingsWindow.ImportJson");
            await ShowInfoAsync("导入失败", ex.Message);
        }
    }

    // ===== MCP / my-mindmap 同步 =====

    private void OpenMindMapAgentProject_Click(object? sender, RoutedEventArgs e)
        => OpenExternal("https://github.com/bubu-LZY/my-mindmap-agent");

    private async void TestMyMindMap_Click(object? sender, RoutedEventArgs e)
    {
        TestMyMindMapButton.IsEnabled = false;
        MyMindMapTestStatus.Text = "正在测试…";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var baseUrl = (MindMapBaseUrlBox.Text ?? "http://127.0.0.1:17800").Trim().TrimEnd('/');
            var url = $"{baseUrl}/api/status?token={Uri.EscapeDataString((MyMindMapTokenBox.Text ?? string.Empty).Trim())}";
            using var resp = await client.GetAsync(url);
            MyMindMapTestStatus.Text = resp.IsSuccessStatusCode ? "连接成功 ✓" : $"连接失败：HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            MyMindMapTestStatus.Text = "连接失败：" + ex.Message;
        }
        finally
        {
            TestMyMindMapButton.IsEnabled = true;
        }
    }

    private async void SyncMindMapNow_Click(object? sender, RoutedEventArgs e)
    {
        SyncMindMapNowButton.IsEnabled = false;
        SyncMindMapStatus.Text = "正在同步…";
        try
        {
            SyncMindMapStatus.Text = MindMapSyncRequested is null
                ? "同步功能不可用"
                : await MindMapSyncRequested();
        }
        catch (Exception ex)
        {
            SyncMindMapStatus.Text = "同步失败：" + ex.Message;
        }
        finally
        {
            SyncMindMapNowButton.IsEnabled = true;
        }
    }

    // ===== 重置 =====

    private async void DeleteAllTasks_Click(object? sender, RoutedEventArgs e)
    {
        DeleteAllTasksButton.IsEnabled = false;
        try
        {
            var ok = await ConfirmAsync(
                "一键删除所有任务",
                "确定要删除数据库中的所有任务吗？\n本操作会清空今日 / 本周 / 日历格 / 备份里的全部任务，且无法恢复。\n继续之前请确认这些任务不再需要。");
            if (!ok)
            {
                return;
            }

            var count = DeleteAllTasksRequested?.Invoke() ?? 0;
            DeleteAllTasksStatus.Text = $"已删除 {count} 条任务。请点保存以应用其他修改（或直接关闭）。";
        }
        finally
        {
            DeleteAllTasksButton.IsEnabled = true;
        }
    }

    // ===== 节假日 =====

    private async Task LoadHolidayStatusAsync()
    {
        try
        {
            var status = await _holidayService.GetCacheStatusAsync();
            if (!status.HasCacheFile && status.OnlineYears.Count == 0)
            {
                HolidayStatusText.Text = "尚未联网拉取过节假日数据（首次启动会拉一次）。";
            }
            else
            {
                var years = status.OnlineYears.Count == 0
                    ? "（从未成功联网过，仅本地缓存/兜底）"
                    : string.Join("、", status.OnlineYears.OrderBy(y => y)) + " 年";
                var sizeKb = status.CacheFileSize / 1024.0;
                HolidayStatusText.Text =
                    $"数据源：{(string.IsNullOrEmpty(status.Source) ? "本地兜底 + 缓存" : status.Source)}\n" +
                    $"已联网拉取的年份：{years}\n" +
                    $"上次更新：{status.UpdatedAt:yyyy-MM-dd HH:mm}\n" +
                    $"缓存大小：{sizeKb:F1} KB";
            }

            HolidayRefreshStatus.Text = string.Empty;
        }
        catch (Exception ex)
        {
            HolidayStatusText.Text = "读取节假日缓存失败：" + ex.Message;
        }
    }

    private async void HolidayRefreshNow_Click(object? sender, RoutedEventArgs e)
    {
        HolidayRefreshNowButton.IsEnabled = false;
        HolidayRefreshStatus.Text = "正在刷新…";
        try
        {
            await _holidayService.LoadAndRefreshAsync(DateTime.Now.Year);
            HolidayRefreshStatus.Text = "刷新完成 ✓";
            HolidayRefreshRequested?.Invoke();
            await LoadHolidayStatusAsync();
        }
        catch (Exception ex)
        {
            HolidayRefreshStatus.Text = "刷新失败：" + ex.Message;
        }
        finally
        {
            HolidayRefreshNowButton.IsEnabled = true;
        }
    }

    private void HolidayClearCache_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            _holidayService.ClearCache();
            HolidayRefreshStatus.Text = "本地缓存已清空。下次切换年份时会重新联网拉取。";
            _ = LoadHolidayStatusAsync();
        }
        catch (Exception ex)
        {
            HolidayRefreshStatus.Text = "清空失败：" + ex.Message;
        }
    }

    // ===== 取消 / 保存 =====

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            await SaveCoreAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "SettingsWindow.Save");
            await ShowInfoAsync("错误", $"保存设置时出错：{ex.Message}");
        }
    }

    private async Task SaveCoreAsync()
    {
        if (!int.TryParse((ApiPortBox.Text ?? string.Empty).Trim(), out var port) || port <= 0 || port > 65535)
        {
            await ShowInfoAsync("输入错误", "端口必须是 1-65535 之间的数字");
            return;
        }

        var timeText = (ReminderTimeBox.Text ?? string.Empty).Trim();
        if (ReminderEnabledBox.IsChecked == true && !TimeOnly.TryParse(timeText, out _))
        {
            await ShowInfoAsync("输入错误", "提醒时间格式应为 HH:mm，例如 09:00");
            return;
        }

        var backupTimeText = (BackupTimeBox.Text ?? string.Empty).Trim();
        if (BackupEnabledBox.IsChecked == true && !TimeOnly.TryParse(backupTimeText, out _))
        {
            await ShowInfoAsync("输入错误", "备份时间格式应为 HH:mm，例如 23:00");
            return;
        }

        if (!int.TryParse((McpPortBox.Text ?? string.Empty).Trim(), out var mcpPort) || mcpPort <= 0 || mcpPort > 65535)
        {
            await ShowInfoAsync("输入错误", "MCP 端口必须是 1-65535 之间的数字");
            return;
        }

        if (!IsValidWebhookUrl(FeishuWebhookBox.Text) || !IsValidWebhookUrl(WeComWebhookBox.Text))
        {
            await ShowInfoAsync("输入错误", "飞书 / 企业微信 webhook 必须是 http:// 或 https:// 开头的完整地址（留空表示不启用）。");
            return;
        }

        _config.ApiEnabled = ApiEnabledBox.IsChecked == true;
        _config.ApiPort = port;
        _config.ApiToken = (ApiTokenBox.Text ?? string.Empty).Trim();
        _config.ReminderEnabled = ReminderEnabledBox.IsChecked == true;
        _config.ReminderTime = timeText;
        _config.FeishuWebhook = (FeishuWebhookBox.Text ?? string.Empty).Trim();
        _config.WeComWebhook = (WeComWebhookBox.Text ?? string.Empty).Trim();
        _config.BackupEnabled = BackupEnabledBox.IsChecked == true;
        _config.BackupTime = backupTimeText;
        _config.BackupSendToFeishu = BackupSendFeishuBox.IsChecked == true;
        _config.BackupSendToWeCom = BackupSendWeComBox.IsChecked == true;
        _config.McpEnabled = McpEnabledBox.IsChecked == true;
        _config.McpPort = mcpPort;
        _config.SyncMyMindMapEnabled = SyncMyMindMapBox.IsChecked == true;
        _config.MindMapBaseUrl = (MindMapBaseUrlBox.Text ?? string.Empty).Trim();
        _config.MyMindMapToken = (MyMindMapTokenBox.Text ?? string.Empty).Trim();
        _config.EmbedDesktop = EmbedDesktopBox.IsChecked == true;
        _config.LockWindow = LockWindowBox.IsChecked == true;

        if (!TryApplyReportValues(showErrors: true))
        {
            return;
        }

        await _configStore.SaveAsync(_config);
        ApplyRequested?.Invoke(_config);
        Close();
    }

    // ===== 工具方法 =====

    private static bool IsValidWebhookUrl(string? text)
        => string.IsNullOrWhiteSpace(text) || IsHttpUrl(text.Trim());

    private static bool IsHttpUrl(string text)
        => Uri.TryCreate(text, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static void OpenExternal(string pathOrUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error(ex, "SettingsWindow.OpenExternal");
        }
    }

    /// <summary>信息提示对话框（替代 WPF MessageBox）。</summary>
    private async Task ShowInfoAsync(string title, string message)
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
        await win.ShowDialog(this);
    }

    /// <summary>确认对话框（替代 WPF MessageBox YesNo/OKCancel）。</summary>
    private async Task<bool> ConfirmAsync(string title, string message)
    {
        var result = false;
        var yes = new Button { Content = "确定", MinWidth = 72 };
        var no = new Button { Content = "取消", MinWidth = 72 };
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
}
