using System.Threading;
using System.Windows;

namespace MicaAgenda.App.Helpers;

/// <summary>
/// 剪贴板访问辅助类，解决 WPF 剪贴板在某些情况下访问失败的问题。
/// </summary>
public static class ClipboardHelper
{
    /// <summary>
    /// 在 STA 线程上执行剪贴板操作，失败时自动重试。
    /// </summary>
    public static void SetText(string text)
    {
        // 如果当前已是 STA 线程，直接尝试
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            TrySetText(text);
            return;
        }

        // 否则在新的 STA 线程上执行
        var thread = new Thread(() => TrySetText(text));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }

    private static void TrySetText(string text)
    {
        const int maxRetries = 3;
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                if (i < maxRetries - 1)
                {
                    Thread.Sleep(50);
                }
                else
                {
                    throw;
                }
            }
        }
    }
}
