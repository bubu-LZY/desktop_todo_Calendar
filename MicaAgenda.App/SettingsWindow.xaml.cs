using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MicaAgenda.App.Models;
using MicaAgenda.App.Services;

namespace MicaAgenda.App;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly AppConfigStore _configStore;
    private readonly Func<string> _apiBaseProvider;
    private readonly Func<string> _mcpEndpointProvider;
    private readonly BackupService _backupService;
    private readonly ChinaHolidayService _holidayService;
    private readonly ReportService _reportService;

    /// <summary>保存后触发，参数为更新后的配置。</summary>
    public event Action<AppConfig>? ApplyRequested;

    /// <summary>导入任务后触发，用于刷新主界面。</summary>
    public event Action? TasksImported;

    /// <summary>手动触发节假日刷新时通知主窗体重新拉数据。</summary>
    public event Action? HolidayRefreshRequested;

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
        EmbedDesktopBox.IsChecked = _config.EmbedDesktop;
        LockWindowBox.IsChecked = _config.LockWindow;
        AutoStartBox.IsChecked = _config.AutoStart;
        HighPriorityBox.IsChecked = _config.HighPriorityStartup;
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

    /// <summary>按当前选中的周期启用对应的下拉框，避免两个下拉都能选造成歧义。</summary>
    private void SyncScheduleControls()
    {
        var monthly = ReportMonthlyRadio.IsChecked == true;
        ReportDayOfWeekBox.IsEnabled = !monthly;
        ReportDayOfMonthBox.IsEnabled = monthly;
    }

    private void ReportSchedule_Changed(object sender, RoutedEventArgs e) => SyncScheduleControls();

    private void ReportPreview_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReportPreviewBox.Text = _reportService.Preview();
            ReportPreviewBox.Visibility = Visibility.Visible;
            ReportActionStatus.Text = "预览已生成（内容取自当前数据，未推送）。";
        }
        catch (Exception ex)
        {
            App.LogError(ex, "SettingsWindow.ReportPreview");
            ReportActionStatus.Text = "预览失败：" + ex.Message;
        }
    }

    private async void ReportSendNow_Click(object sender, RoutedEventArgs e)
    {
        // 立即发送用的是「界面上还没保存的值」，所以先把报告相关的字段写回内存配置，
        // 免得用户填了 webhook、勾了渠道，点发送却按旧配置走空渠道。
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
            ReportActionStatus.Foreground = sent
                ? System.Windows.Media.Brushes.Green
                : System.Windows.Media.Brushes.IndianRed;
        }
        catch (Exception ex)
        {
            App.LogError(ex, "SettingsWindow.ReportSendNow");
            ReportActionStatus.Text = "推送异常：" + ex.Message;
        }
        finally
        {
            ReportSendNowButton.IsEnabled = true;
        }
    }

    /// <summary>把报告相关控件的值写回 _config；返回是否通过校验。</summary>
    private bool TryApplyReportValues(bool showErrors)
    {
        var timeText = ReportTimeBox.Text.Trim();
        if (ReportEnabledBox.IsChecked == true && !TimeOnly.TryParse(timeText, out _))
        {
            if (showErrors)
            {
                MessageBox.Show(this, "报告发送时间格式应为 HH:mm，例如 18:00", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                ReportActionStatus.Text = "报告发送时间格式应为 HH:mm，例如 18:00";
            }

            return false;
        }

        var customHook = ReportCustomWebhookBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(customHook))
        {
            // 只要求是 http/https 的完整地址：内网自建的中转服务也常见，不强制 https
            var isUrl = Uri.TryCreate(customHook, UriKind.Absolute, out var parsed)
                        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);
            if (!isUrl)
            {
                const string message = "自定义 webhook 必须是 http:// 或 https:// 开头的完整地址。";
                if (showErrors)
                {
                    MessageBox.Show(this, message, "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    ReportActionStatus.Text = message;
                }

                return false;
            }
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

    private void UpdateMcpEndpoint()
    {
        McpEndpointBox.Text = _mcpEndpointProvider();
    }

    private void UpdateApiHint()
    {
        var baseUrl = _apiBaseProvider();
        ApiHint.Text = $"Agent 调用示例：curl -H \"X-Auth-Token: <token>\" {baseUrl}/api/tasks?range=today";
    }

    private void CopyToken_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(ApiTokenBox.Text))
        {
            try
            {
                Helpers.ClipboardHelper.SetText(ApiTokenBox.Text);
                Helpers.ToastHelper.Show("Token 已复制", this);
            }
            catch (Exception ex)
            {
                Helpers.ToastHelper.Show($"复制失败：{ex.Message}", this);
            }
        }
    }

    private void RegenerateToken_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "重新生成 Token 后，旧的 Token 将立即失效，需要重新告知所有调用方。确定重新生成？",
            "确认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            ApiTokenBox.Text = Guid.NewGuid().ToString("N");
        }
    }

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        BackupNowButton.IsEnabled = false;
        try
        {
            var result = await _backupService.RunBackupAsync();
            var sb = new System.Text.StringBuilder();
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

            MessageBox.Show(this, sb.ToString(), "备份", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            BackupNowButton.IsEnabled = true;
        }
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var backupDir = Services.BackupService.GetBackupDirectory();
            if (!System.IO.Directory.Exists(backupDir))
            {
                System.IO.Directory.CreateDirectory(backupDir);
            }
            System.Diagnostics.Process.Start("explorer.exe", backupDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打开文件夹失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportJson_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出任务 JSON",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"micaagenda-tasks-{DateTime.Now:yyyyMMdd}.json"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var json = _backupService.ExportYearJson(DateTime.Now.Year);
            System.IO.File.WriteAllText(dialog.FileName, json, new System.Text.UTF8Encoding(false));
            Helpers.ToastHelper.Show("导出成功", this);
        }
        catch (Exception ex)
        {
            Helpers.ToastHelper.Show($"导出失败：{ex.Message}", this);
        }
    }

    private void ImportJson_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入任务 JSON",
            Filter = "JSON 文件 (*.json)|*.json"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var json = System.IO.File.ReadAllText(dialog.FileName);
            var added = _backupService.ImportJson(json);
            TasksImported?.Invoke();
            Helpers.ToastHelper.Show($"导入完成，新增 {added} 条任务", this);
        }
        catch (Exception ex)
        {
            Helpers.ToastHelper.Show($"导入失败：{ex.Message}", this);
        }
    }

    private void CopySkillDoc_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Helpers.ClipboardHelper.SetText(McpDocsProvider.BuildSkillDoc(McpEndpointBox.Text.Trim(), _config.ApiToken));
            Helpers.ToastHelper.Show("Skill 文档已复制", this);
        }
        catch (Exception ex)
        {
            Helpers.ToastHelper.Show($"复制失败：{ex.Message}", this);
        }
    }

    private void CopyMcpConfig_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Helpers.ClipboardHelper.SetText(McpDocsProvider.BuildMcpConfigJson(McpEndpointBox.Text.Trim(), _config.ApiToken));
            Helpers.ToastHelper.Show("MCP 配置已复制", this);
        }
        catch (Exception ex)
        {
            Helpers.ToastHelper.Show($"复制失败：{ex.Message}", this);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void DeleteAllTasks_Click(object sender, RoutedEventArgs e)
    {
        DeleteAllTasksButton.IsEnabled = false;
        try
        {
            var answer = MessageBox.Show(
                this,
                "确定要删除数据库中的所有任务吗？\n" +
                "本操作会清空今日 / 本周 / 日历格 / 备份里的全部任务，且无法恢复。\n" +
                "继续之前请确认这些任务不再需要。",
                "一键删除所有任务",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);

            if (answer != MessageBoxResult.OK)
            {
                return;
            }

            // 点击 handler 就在 UI 线程上，没必要走 TaskCompletionSource。
            // 直接同步调订阅者即可；没订阅者时给 0，不再像之前那样挂起。
            var count = DeleteAllTasksRequested?.Invoke() ?? 0;
            DeleteAllTasksStatus.Text = $"已删除 {count} 条任务。请点保存以应用其他修改（或直接关闭）。";
        }
        finally
        {
            DeleteAllTasksButton.IsEnabled = true;
        }
    }

    /// <summary>通知主窗体执行清空所有任务；返回删除的任务数。</summary>
    public event Func<int>? DeleteAllTasksRequested;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // 历史 bug：这里曾经写了两遍 SaveCore()，第一次已把 DialogResult 设为 true
            // 并 Close()，第二次再设 DialogResult 就抛"只能在创建 Window 并且作为对话框
            // 显示之后才能设置 DialogResult"，导致每次保存都弹错误框。
            SaveCore();
        }
        catch (Exception ex)
        {
            // 任何异常都必须就地消化：设置面板出错不应导致整个程序退出
            App.LogError(ex, "SettingsWindow.Save");
            try
            {
                MessageBox.Show(this, $"保存设置时出错：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // 连提示都失败时不再处理
            }
        }
    }

    private void SaveCore()
    {
        // 校验端口
        if (!int.TryParse(ApiPortBox.Text.Trim(), out var port) || port <= 0 || port > 65535)
        {
            MessageBox.Show(this, "端口必须是 1-65535 之间的数字", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 校验提醒时间格式
        var timeText = ReminderTimeBox.Text.Trim();
        if (ReminderEnabledBox.IsChecked == true && !TimeOnly.TryParse(timeText, out _))
        {
            MessageBox.Show(this, "提醒时间格式应为 HH:mm，例如 09:00", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 校验备份时间格式
        var backupTimeText = BackupTimeBox.Text.Trim();
        if (BackupEnabledBox.IsChecked == true && !TimeOnly.TryParse(backupTimeText, out _))
        {
            MessageBox.Show(this, "备份时间格式应为 HH:mm，例如 23:00", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 校验 MCP 端口
        if (!int.TryParse(McpPortBox.Text.Trim(), out var mcpPort) || mcpPort <= 0 || mcpPort > 65535)
        {
            MessageBox.Show(this, "MCP 端口必须是 1-65535 之间的数字", "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Webhook 只允许 http/https，避免 file:// 或其它 scheme 造成意外本地行为。
        if (!IsValidWebhookUrl(FeishuWebhookBox.Text) || !IsValidWebhookUrl(WeComWebhookBox.Text))
        {
            MessageBox.Show(this, "飞书 / 企业微信 webhook 必须是 http:// 或 https:// 开头的完整地址（留空表示不启用）。",
                "输入错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 写回配置
        _config.ApiEnabled = ApiEnabledBox.IsChecked == true;
        _config.ApiPort = port;
        _config.ApiToken = ApiTokenBox.Text.Trim();
        _config.ReminderEnabled = ReminderEnabledBox.IsChecked == true;
        _config.ReminderTime = timeText;
        _config.FeishuWebhook = FeishuWebhookBox.Text.Trim();
        _config.WeComWebhook = WeComWebhookBox.Text.Trim();
        _config.BackupEnabled = BackupEnabledBox.IsChecked == true;
        _config.BackupTime = backupTimeText;
        _config.BackupSendToFeishu = BackupSendFeishuBox.IsChecked == true;
        _config.BackupSendToWeCom = BackupSendWeComBox.IsChecked == true;
        _config.McpEnabled = McpEnabledBox.IsChecked == true;
        _config.McpPort = mcpPort;
        _config.EmbedDesktop = EmbedDesktopBox.IsChecked == true;
        _config.LockWindow = LockWindowBox.IsChecked == true;

        // 报告相关字段单独校验并写回（校验不通过时直接 return，不落盘、不关闭窗口）
        if (!TryApplyReportValues(showErrors: true))
        {
            return;
        }

        var newAutoStart = AutoStartBox.IsChecked == true;
        var autoStartChanged = newAutoStart != _config.AutoStart;
        if (autoStartChanged)
        {
            _config.AutoStart = newAutoStart;
        }

        var newHighPriority = HighPriorityBox.IsChecked == true;
        var highPriorityChanged = newHighPriority != _config.HighPriorityStartup;
        if (highPriorityChanged)
        {
            _config.HighPriorityStartup = newHighPriority;
        }

        _ = _configStore.SaveAsync(_config);
        ApplyRequested?.Invoke(_config);

        DialogResult = true;
        Close();

        // 开机启动项要走注册表与 schtasks（后者会同步等待进程退出，最多 5 秒）。
        // 放到后台线程执行，避免"点保存后界面卡住几秒"。
        if (autoStartChanged || highPriorityChanged)
        {
            _ = Task.Run(() => ApplyStartupOptions(newAutoStart, newHighPriority));
        }
    }

    private static bool IsValidWebhookUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static void ApplyStartupOptions(bool autoStart, bool highPriority)
    {
        try
        {
            Services.AutoStartService.SetEnabled(autoStart);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "AutoStartService");
        }

        try
        {
            Services.HighPriorityStartupService.SetEnabled(highPriority);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "HighPriorityStartupService");
        }
    }

    // ===== 节假日数据状态 =====

    private async Task LoadHolidayStatusAsync()
    {
        try
        {
            var status = await _holidayService.GetCacheStatusAsync();
            if (HolidayStatusText is null) return;
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
            if (HolidayStatusText is not null)
            {
                HolidayStatusText.Text = "读取节假日缓存失败：" + ex.Message;
            }
        }
    }

    private async void HolidayRefreshNow_Click(object sender, RoutedEventArgs e)
    {
        if (HolidayRefreshNowButton is null) return;
        HolidayRefreshNowButton.IsEnabled = false;
        HolidayRefreshStatus.Text = "正在刷新…";
        try
        {
            // 直接调用 Service 的 LoadAndRefreshAsync（当前可见年份取自 SelectedDate，这里用最近一年兜底）
            var year = DateTime.Now.Year;
            await _holidayService.LoadAndRefreshAsync(year);
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
            if (HolidayRefreshNowButton is not null) HolidayRefreshNowButton.IsEnabled = true;
        }
    }

    private void HolidayClearCache_Click(object sender, RoutedEventArgs e)
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
}
