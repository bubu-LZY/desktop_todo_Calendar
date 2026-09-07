using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;

namespace MicaAgenda.App.Helpers;

/// <summary>
/// 剪贴板访问辅助类。
///
/// 背景：WPF 的 <see cref="Clipboard.SetText(string)"/> 在剪贴板被其他进程短暂占用时
/// 会抛出 COMException 0x800401D0 (CLIPBRD_E_CANT_OPEN)，表现为"点一下卡顿然后报错"。
/// 这里直接用 Win32 原生剪贴板 API（OpenClipboard / SetClipboardData）写入，配合
/// 带退避的重试，能绕开 WPF 的 COM 层、稳定完成写入；仅在原生路径不可用时才回退到
/// WPF 实现。返回 bool 表示是否成功，失败不抛异常，由调用方给出可手动复制的兜底。
/// </summary>
public static class ClipboardHelper
{
    // CF_UNICODETEXT：以 UTF-16 结尾 \0 的文本
    private const uint CF_UNICODETEXT = 13;
    // GMEM_MOVEABLE
    private const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    /// <summary>
    /// 写入剪贴板，返回是否成功。失败不抛异常。
    /// </summary>
    public static bool TrySetText(string text)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return TrySetTextCore(text);
        }

        // 后台线程没有 STA：切到 STA 线程执行，join 拿到结果
        var result = false;
        var thread = new Thread(() => result = TrySetTextCore(text));
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    /// <summary>
    /// 兼容旧调用：成功时静默；失败时抛出异常，由调用方提示。
    /// </summary>
    public static void SetText(string text)
    {
        if (!TrySetText(text))
        {
            throw new System.Runtime.InteropServices.COMException(
                "无法访问剪贴板（可能被其他程序占用），请稍后重试。", unchecked((int)0x800401D0));
        }
    }

    private static bool TrySetTextCore(string text)
    {
        if (text is null) text = string.Empty;

        // 先走原生路径（最稳），带退避重试
        if (TrySetViaWin32(text))
        {
            return true;
        }

        // 原生路径失败（例如被远程桌面/剪贴板管理器长时间锁定）时回退 WPF，仍带重试
        return TrySetViaWpf(text);
    }

    private static bool TrySetViaWin32(string text)
    {
        // 提前分配内存，尽量缩短持有剪贴板锁的时间
        var bytes = Encoding.Unicode.GetBytes(text + '\0');
        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(bytes.Length));
        if (hMem == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var pMem = GlobalLock(hMem);
            if (pMem == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                Marshal.Copy(bytes, 0, pMem, bytes.Length);
            }
            finally
            {
                GlobalUnlock(hMem);
            }

            // OpenClipboard 失败时退避重试；间隔从 10ms 起逐步加大，总耗时 < 1s，不卡 UI
            if (!OpenClipboardWithRetry())
            {
                return false;
            }

            try
            {
                EmptyClipboard();
                if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
                {
                    return false;
                }

                // 成功后内存所有权移交系统，置零避免 finally 里重复释放
                var transferred = hMem;
                hMem = IntPtr.Zero;
                return transferred != IntPtr.Zero;
            }
            finally
            {
                CloseClipboard();
            }
        }
        finally
        {
            if (hMem != IntPtr.Zero)
            {
                GlobalFree(hMem);
            }
        }
    }

    private static bool OpenClipboardWithRetry()
    {
        var delays = new[] { 10, 20, 40, 80, 120, 160, 200, 250, 300, 350 };
        foreach (var ms in delays)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            // 0x800401D0 对应的 GetLastError 通常为 ERROR_ACCESS_DENIED(5)
            var err = Marshal.GetLastWin32Error();
            if (err != 0 && err != 5)
            {
                return false;
            }

            Thread.Sleep(ms);
        }

        return false;
    }

    private static bool TrySetViaWpf(string text)
    {
        const int maxRetries = 5;
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40 * (i + 1));
            }
        }

        return false;
    }
}
