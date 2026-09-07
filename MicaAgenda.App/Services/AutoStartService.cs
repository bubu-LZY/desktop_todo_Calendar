using Microsoft.Win32;

namespace MicaAgenda.App.Services;

public static class AutoStartService
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string ValueName = "MicaAgenda";

    public static bool IsEnabled()
    {
        return IsEnabled(RunKeyPath, ValueName);
    }

    public static void SetEnabled(bool enabled)
    {
        SetEnabled(enabled, RunKeyPath, ValueName, Environment.ProcessPath);
    }

    internal static bool IsEnabled(string runKeyPath, string valueName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, false);
        return key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    internal static void SetEnabled(bool enabled, string runKeyPath, string valueName, string? executablePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, true)
            ?? Registry.CurrentUser.CreateSubKey(runKeyPath, true);

        if (enabled)
        {
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                return;
            }

            key.SetValue(valueName, BuildRunCommand(executablePath));
        }
        else
        {
            key.DeleteValue(valueName, false);
        }
    }

    internal static string BuildRunCommand(string executablePath)
    {
        return $"\"{executablePath}\"";
    }
}
