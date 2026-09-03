using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace MicaAgenda.App.Helpers;

/// <summary>
/// 剪贴板访问辅助类，解决 WPF 剪贴板在其它进程临时占用时访问失败的问题。
/// </summary>
public static class ClipboardHelper
{
    /// <summary>
    /// 在 UI 线程写入剪贴板，失败时异步重试，避免同步 Sleep 卡住界面。
    /// 0x800401D0（CLIPBRD_E_CANT_OPEN）通常表示剪贴板正被其它进程打开，
    /// 稍等片刻重试即可成功。
    /// </summary>
    public static async Task SetTextAsync(string text)
    {
        Exception? last = null;
        const int maxRetries = 15;
        const int retryDelayMs = 100;

        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                Clipboard.SetText(text, TextDataFormat.UnicodeText);
                return;
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x800401D0)
            {
                // 剪贴板被占用：yield 回消息循环，不阻塞 UI，稍后再试
                last = ex;
                await Task.Delay(retryDelayMs);
            }
        }

        throw last ?? new COMException("无法访问剪贴板。", unchecked((int)0x800401D0));
    }
}
