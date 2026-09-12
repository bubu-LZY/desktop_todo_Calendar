using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace MicaAgenda.Desktop.Services;

/// <summary>
/// macOS 桌面嵌入（原生 AppKit 机制）。
///
/// 与 Windows 的「Z 序底部 + 看门狗」策略不同，macOS 有正规的桌面小部件打法：
/// 1. 把 <c>NSWindow.level</c> 设为桌面图标层（<c>kCGDesktopIconWindowLevel</c>，
///    即 Swift 里的 <c>NSWindow.Level.desktopIcon</c>）——沉在所有普通窗口之下、贴桌面；
/// 2. <c>collectionBehavior = canJoinAllSpaces | stationary</c> —— 跨 Space 跟随、
///    不进 Mission Control / Exposé，不会随其他窗口一起被收走；
/// 3. 关阴影、去边框，看起来像桌面的一部分；隐藏 Dock 图标（accessory activation policy）。
///
/// Avalonia 没有暴露 window level 的高级 API，这里直接用 Objective-C 运行时发消息
/// （<c>objc_msgSend</c> + <c>sel_registerName</c>）。全部调用只在本类内、且仅当
/// <c>OperatingSystem.IsMacOS()</c> 为真时被触发，Windows / Linux 永不进入。
///
/// ⚠️ 本文件在 Windows 上只能编译不能运行：DllImport 只是元数据，真正的桥接需在
/// macOS 机器上冒烟验证。
/// </summary>
public static class MacDesktopEmbedService
{
    private const string LibObjC = "/usr/lib/libobjc.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // NSWindowCollectionBehavior 位标志（AppKit 公开常量）
    private const ulong CanJoinAllSpaces = 1UL << 0;
    private const ulong Stationary = 1UL << 4;

    // NSApplicationActivationPolicyAccessory：有窗口但无 Dock 图标
    private const ulong ActivationPolicyAccessory = 1;

    /// <summary>
    /// 对 Avalonia 窗口应用（或撤销）桌面嵌入。
    /// 仅在 macOS 上起作用；非 macOS 直接返回。
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static void ApplyTo(Window window, bool embed)
    {
        if (!OperatingSystem.IsMacOS() || window is null)
        {
            return;
        }

        var nsWindow = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (nsWindow == IntPtr.Zero)
        {
            return;
        }

        var setLevel = Sel("setLevel:");
        var setBehavior = Sel("setCollectionBehavior:");
        var setShadow = Sel("setHasShadow:");

        if (embed)
        {
            // 桌面图标层 = 沉底但不抢焦点，仍可交互
            var key = CFStringMakeConstantString("kCGDesktopIconWindowLevelKey");
            var level = CGWindowLevelForKey(key);
            Send(nsWindow, setLevel, (IntPtr)(long)level);

            Send(nsWindow, setBehavior, (IntPtr)(long)(CanJoinAllSpaces | Stationary));
            Send(nsWindow, setShadow, IntPtr.Zero);

            // 隐藏 Dock 图标（accessory）：桌面小部件不该常驻 Dock
            HideFromDock();
        }
        else
        {
            // 回到普通窗口：normal level(0) + 默认 collectionBehavior
            Send(nsWindow, setLevel, IntPtr.Zero);
            Send(nsWindow, setBehavior, IntPtr.Zero);
            Send(nsWindow, setShadow, new IntPtr(1));
        }
    }

    /// <summary>
    /// 彻底隐藏标准窗口右上角的红色关闭按钮（traffic light 的 close）。
    /// 桌面小部件不该能点 × 关闭，退出走托盘 / 设置。
    /// 通过 [window standardWindowButton:NSWindowCloseButton] 拿到按钮再 setHidden:YES。
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static void HideCloseButton(Window window)
    {
        if (!OperatingSystem.IsMacOS() || window is null)
        {
            return;
        }

        var nsWindow = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (nsWindow == IntPtr.Zero)
        {
            return;
        }

        // NSWindowCloseButton = 0（NSWindowButton 枚举第一个值）
        var button = SendIntPtrArg(nsWindow, Sel("standardWindowButton:"), IntPtr.Zero);
        if (button != IntPtr.Zero)
        {
            Send(button, Sel("setHidden:"), new IntPtr(1));
        }
    }

    /// <summary>把 NSApplication 切到 accessory 激活策略，去掉 Dock 图标。</summary>
    [SupportedOSPlatform("macos")]
    private static void HideFromDock()
    {
        var appClass = Class("NSApplication");
        if (appClass == IntPtr.Zero)
        {
            return;
        }

        var sharedApp = SendIntPtr(appClass, Sel("sharedApplication"));
        if (sharedApp != IntPtr.Zero)
        {
            Send(sharedApp, Sel("setActivationPolicy:"), (IntPtr)(long)ActivationPolicyAccessory);
        }
    }

    // ===== Objective-C 运行时桥接 =====

    private static IntPtr Class(string name) => objc_getClass(name);

    private static IntPtr Sel(string name) => sel_registerName(name);

    private static void Send(IntPtr receiver, IntPtr selector, IntPtr arg)
        => objc_msgSend_void(receiver, selector, arg);

    private static IntPtr SendIntPtr(IntPtr receiver, IntPtr selector)
        => objc_msgSend_ret(receiver, selector);

    private static IntPtr SendIntPtrArg(IntPtr receiver, IntPtr selector, IntPtr arg)
        => objc_msgSend_ret_arg(receiver, selector, arg);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_ret(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_ret_arg(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(LibObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(LibObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(CoreGraphics)]
    private static extern int CGWindowLevelForKey(IntPtr key);

    [DllImport(CoreFoundation, EntryPoint = "__CFStringMakeConstantString")]
    private static extern IntPtr CFStringMakeConstantString([MarshalAs(UnmanagedType.LPStr)] string cStr);
}