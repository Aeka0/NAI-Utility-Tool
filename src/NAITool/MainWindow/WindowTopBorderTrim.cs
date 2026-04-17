using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace NAITool;

/// <summary>
/// 通过 WndProc 子类化把客户区的上边界向上扩 1 像素，用来遮盖
/// WinUI 3 + DesktopAcrylicBackdrop + 自定义标题栏组合下残留的顶端
/// 1px 不透明非客户区线。
///
/// 这条线本质是系统绘制的 resize 边框最上方一行：
/// 它位于 XAML 客户区之上、由 DWM 接管绘制，既不会被
/// <see cref="Microsoft.UI.Windowing.AppWindowTitleBar.BackgroundColor"/>
/// 的透明值盖住，也不会跟随应用内 <c>RequestedTheme</c> 切换而刷新。
/// 解决方案是直接在 WM_NCCALCSIZE 里让客户区向上多吃 1px，
/// 再交给 Acrylic 渲染，原本那条不透明线就被客户区内容彻底遮住了。
/// </summary>
internal static class WindowTopBorderTrim
{
    private const int GWLP_WNDPROC = -4;
    private const uint WM_NCCALCSIZE = 0x0083;
    private const uint WM_NCDESTROY = 0x0082;

    [UnmanagedFunctionPointer(CallingConvention.Winapi, SetLastError = true)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    // NCCALCSIZE_PARAMS 布局参考（顺序）：
    //   RECT NewWindow;   // 对应 rgrc[0]
    //   RECT OldWindow;   // 对应 rgrc[1]
    //   RECT OldClient;   // 对应 rgrc[2]
    //   PWINDOWPOS lppos;
    // RECT = { int Left; int Top; int Right; int Bottom; }
    // 因此 NewWindow.Top 位于 lParam 起始处 + 4 字节偏移。

    private sealed class HookedWindow
    {
        public IntPtr Hwnd;
        public IntPtr OriginalWndProc;
        public WndProcDelegate ManagedDelegate = null!;
        public IntPtr ManagedWndProc;
    }

    // 以 hwnd 为键缓存已安装的 hook，避免重复安装；同时强引用住托管委托，
    // 防止其被 GC 回收导致原生回调指向已释放内存。
    private static readonly Dictionary<IntPtr, HookedWindow> s_hooks = new();
    private static readonly object s_lock = new();

    /// <summary>
    /// 对窗口安装顶端 1px 遮盖 hook；重复调用安全（幂等）。
    /// </summary>
    public static void Install(Window window)
    {
        if (window == null) return;

        IntPtr hwnd;
        try
        {
            hwnd = WindowNative.GetWindowHandle(window);
        }
        catch
        {
            return;
        }
        if (hwnd == IntPtr.Zero) return;

        lock (s_lock)
        {
            if (s_hooks.ContainsKey(hwnd)) return;

            var entry = new HookedWindow { Hwnd = hwnd };
            entry.ManagedDelegate = (h, m, w, l) => SubclassedWndProc(entry, h, m, w, l);
            entry.ManagedWndProc = Marshal.GetFunctionPointerForDelegate(entry.ManagedDelegate);

            IntPtr prev = SetWindowLongPtrSafe(hwnd, GWLP_WNDPROC, entry.ManagedWndProc);
            if (prev == IntPtr.Zero)
            {
                return;
            }
            entry.OriginalWndProc = prev;
            s_hooks[hwnd] = entry;

            // 触发一次 WM_NCCALCSIZE 重新计算，让客户区立刻扩展 1px。
            const uint SWP_NOMOVE = 0x0002;
            const uint SWP_NOSIZE = 0x0001;
            const uint SWP_NOZORDER = 0x0004;
            const uint SWP_NOACTIVATE = 0x0010;
            const uint SWP_FRAMECHANGED = 0x0020;
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }

    private static IntPtr SubclassedWndProc(HookedWindow entry, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero && lParam != IntPtr.Zero)
        {
            // 先让默认处理算出标准的客户区，再把上边界上移 1px，
            // 使 Acrylic 客户区内容覆盖系统绘制的顶端 1px NC 边线。
            IntPtr result = CallWindowProc(entry.OriginalWndProc, hwnd, msg, wParam, lParam);
            try
            {
                // NCCALCSIZE_PARAMS 前三个字段都是 RECT{Left,Top,Right,Bottom}。
                // NewWindow (rgrc[0]) 的 Top 字段位于偏移 4 字节处。
                const int newWindowTopOffset = 4;
                int top = Marshal.ReadInt32(lParam, newWindowTopOffset);
                Marshal.WriteInt32(lParam, newWindowTopOffset, top - 1);
            }
            catch
            {
                // 极端情况下访问异常，忽略并按默认值继续。
            }
            return result;
        }

        if (msg == WM_NCDESTROY)
        {
            IntPtr res = CallWindowProc(entry.OriginalWndProc, hwnd, msg, wParam, lParam);
            lock (s_lock)
            {
                s_hooks.Remove(hwnd);
            }
            return res;
        }

        return CallWindowProc(entry.OriginalWndProc, hwnd, msg, wParam, lParam);
    }

    private static IntPtr SetWindowLongPtrSafe(IntPtr hwnd, int nIndex, IntPtr newLong)
    {
        // 64 位进程使用 SetWindowLongPtrW，32 位使用 SetWindowLongW。
        if (IntPtr.Size == 8)
            return SetWindowLongPtrW(hwnd, nIndex, newLong);
        return new IntPtr(SetWindowLongW(hwnd, nIndex, newLong.ToInt32()));
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
}
