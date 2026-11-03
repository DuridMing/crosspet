using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CrossPet
{
    /// <summary>用到的幾個 Windows 系統呼叫：視窗樣式、置頂、按畫素擺位置、監聽前臺視窗切換。</summary>
    static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 0x2;
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);

        /// <summary>不搶焦點、不出現在 Alt+Tab 裡</summary>
        public static void MakeToolWindow(IntPtr hwnd) =>
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        /// <summary>重新放到最頂層（不搶焦點、不挪位置）。別的置頂視窗啟用後會蓋過來，每次前臺切換都調一次</summary>
        public static void BringToTop(IntPtr hwnd) =>
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        /// <summary>桌寵中間那塊有沒有被它上面的視窗擋住（順著 Z 序往上看可見、沒最小化、沒被系統藏起來的視窗）</summary>
        public static bool Covered(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out var me)) return false;
            int w = me.Right - me.Left, h = me.Bottom - me.Top;
            int left = me.Left + w / 4, right = me.Right - w / 4, top = me.Top + h / 5, bottom = me.Bottom - h / 5;
            for (var other = GetWindow(hwnd, 3); other != IntPtr.Zero; other = GetWindow(other, 3))  // GW_HWNDPREV：Z 序裡在它上面的
            {
                if (!IsWindowVisible(other) || IsIconic(other)) continue;
                if (DwmGetWindowAttribute(other, 14, out var cloaked, 4) == 0 && cloaked != 0) continue;  // DWMWA_CLOAKED（別的桌面、掛起的應用）
                if (!GetWindowRect(other, out var r) || r.Right - r.Left <= 0 || r.Bottom - r.Top <= 0) continue;
                if (r.Left < right && r.Right > left && r.Top < bottom && r.Bottom > top) return true;
            }
            return false;
        }

        static readonly uint MyPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        /// <summary>取消置頂（變成普通視窗，會被別的視窗擋住）</summary>
        /// 取消置頂後系統會把它放在所有普通視窗最前面，還是壓在你正在用的視窗上，要等那個視窗再啟用一次才蓋過來：
        /// 所以再把它挪到當前前臺視窗的後面（就像你剛點了一下那個視窗）
        public static void DropFromTop(IntPtr hwnd)
        {
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            var front = GetForegroundWindow();
            if (front == IntPtr.Zero || front == hwnd) return;
            var root = GetAncestor(front, 2); if (root != IntPtr.Zero) front = root;   // GA_ROOT
            var cls = new StringBuilder(64); GetClassName(front, cls, 64);
            GetWindowThreadProcessId(front, out var pid);
            if (ZOrderRule.TuckBelow(cls.ToString(), pid == MyPid))   // 桌面、工作列、自己的選單下面不插（見 ZOrderRule）
                SetWindowPos(hwnd, front, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        // 桌寵所在的那塊螢幕上，是不是有全屏程式（全屏影片 / 遊戲 / 演示）。
        // 以前只用系統的「勿擾」狀態（SHQueryUserNotificationState）：它是整臺電腦的，另一塊螢幕全屏、
        // 或者某些程式讓它一直掛著，副屏上的桌寵也被藏起來、怎麼都不出來。現在要求前臺視窗真的鋪滿桌寵這塊螢幕
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
        public static bool InFullscreen(IntPtr pet)
        {
            try
            {
                var front = GetForegroundWindow();
                if (front == IntPtr.Zero || front == pet) return false;
                front = GetAncestor(front, 2) is var root && root != IntPtr.Zero ? root : front;   // GA_ROOT
                var cls = new StringBuilder(64); GetClassName(front, cls, 64);
                var c = cls.ToString();
                if (ZOrderRule.IsShell(c)) return false;   // 桌面、工作列
                var mon = MonitorFromWindow(pet, 2);   // MONITOR_DEFAULTTONEAREST
                if (mon != MonitorFromWindow(front, 2)) return false;   // 全屏的是另一塊螢幕
                var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (!GetMonitorInfo(mon, ref info) || !GetWindowRect(front, out var r)) return false;
                var m = info.rcMonitor;
                var covers = r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;   // 蓋住整塊螢幕（連工作列）
                if (covers) return true;
                // D3D 獨佔全屏 / 演示模式：系統狀態說了算，但也得是這塊螢幕上的前臺視窗
                return SHQueryUserNotificationState(out var s) == 0 && (s == 3 || s == 4) && r.Right - r.Left >= m.Right - m.Left - 2;
            }
            catch { return false; }
        }

        public static void MoveTo(IntPtr hwnd, int x, int y) =>
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        /// <summary>前臺是不是桌面或工作列（Win+D、點了桌面空白處）</summary>
        public static bool ForegroundIsShell()
        {
            var front = GetForegroundWindow();
            if (front == IntPtr.Zero) return false;
            var root = GetAncestor(front, 2); if (root != IntPtr.Zero) front = root;   // GA_ROOT
            var cls = new StringBuilder(64); GetClassName(front, cls, 64);
            return ZOrderRule.IsShell(cls.ToString());
        }

        /// <summary>前臺視窗所屬程式的檔名（小寫，如 codex.exe）；取不到返回空串。只看程式名，不讀視窗標題和內容</summary>
        public static string ForegroundExe()
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "";
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return "";
            var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? Path.GetFileName(sb.ToString()).ToLowerInvariant() : "";
            }
            finally { CloseHandle(h); }
        }

        /// <summary>前臺視窗一變就回調（系統事件推送，不用輪詢）。返回值要一直留著，否則回撥會被回收</summary>
        public sealed class ForegroundWatcher : IDisposable
        {
            readonly WinEventProc proc;
            readonly IntPtr hook;
            public ForegroundWatcher(Action changed)
            {
                proc = (h, e, w, o, c, t, time) => changed();
                hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, proc, 0, 0,
                    WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            }
            public void Dispose() { if (hook != IntPtr.Zero) UnhookWinEvent(hook); }
        }
    }
}
