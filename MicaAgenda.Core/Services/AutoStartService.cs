using System.Runtime.Versioning;
using Microsoft.Win32;

namespace MicaAgenda.App.Services;

[SupportedOSPlatform("windows")]
public static class AutoStartService
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "MicaAgenda";

    /// <summary>
    /// 安装脚本（setup.iss）勾选「开机自启动」时写入的值名。程序侧过去只认 <see cref="ValueName"/>，
    /// 于是安装包写的这一条程序既看不见（设置里显示未开启）也删不掉（关掉自启后仍在开机拉起程序）。
    /// 保留此常量用于识别并清理它。
    /// </summary>
    internal const string LegacyValueName = "desktop_todo_Calendar";

    public static bool IsEnabled()
    {
        return IsEnabled(RunKeyPath, ValueName, LegacyValueName);
    }

    public static void SetEnabled(bool enabled)
    {
        SetEnabled(enabled, RunKeyPath, ValueName, LegacyValueName, Environment.ProcessPath);
    }

    internal static bool IsEnabled(string runKeyPath, string valueName)
    {
        return IsEnabled(runKeyPath, valueName, null);
    }

    internal static bool IsEnabled(string runKeyPath, string valueName, string? legacyValueName)
    {
        if (HasValue(runKeyPath, valueName))
        {
            return true;
        }

        return !string.IsNullOrEmpty(legacyValueName) && HasValue(runKeyPath, legacyValueName);
    }

    internal static void SetEnabled(bool enabled, string runKeyPath, string valueName, string? executablePath)
    {
        SetEnabled(enabled, runKeyPath, valueName, null, executablePath);
    }

    /// <summary>
    /// 写入 / 删除 <paramref name="valueName"/>，并一并清掉安装脚本留下的 <paramref name="legacyValueName"/>：
    /// 两个值名指向同一个程序时，开机启动会出现两个实例；用户关掉自启后也会残留一条删不掉的。
    /// </summary>
    internal static void SetEnabled(
        bool enabled,
        string runKeyPath,
        string valueName,
        string? legacyValueName,
        string? executablePath)
    {
        WriteValue(runKeyPath, valueName, enabled, executablePath);

        if (!string.IsNullOrEmpty(legacyValueName)
            && !string.Equals(legacyValueName, valueName, StringComparison.Ordinal))
        {
            WriteValue(runKeyPath, legacyValueName, false, null);
        }
    }

    internal static string BuildRunCommand(string executablePath)
    {
        return $"\"{executablePath}\"";
    }

    private static bool HasValue(string runKeyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, false);
        return key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    private static void WriteValue(string runKeyPath, string valueName, bool enabled, string? executablePath)
    {
        if (enabled && string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, true)
            ?? Registry.CurrentUser.CreateSubKey(runKeyPath, true);

        if (enabled)
        {
            key.SetValue(valueName, BuildRunCommand(executablePath!));
        }
        else
        {
            key.DeleteValue(valueName, false);
        }
    }
}
