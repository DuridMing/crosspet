using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CrossPet
{
    sealed partial class Pet
    {
        // ---------------- 顯示層級 ----------------
        // always：始終在最上層；ai：前臺是 AI 應用、AI 等你回答、剛乾完活時在最上層，其他時候是普通視窗（會被擋住）；
        // normal：普通視窗。另外全屏程式（影片 / 遊戲 / 演示）在前臺時整個隱藏
        static string LayerMode => Store.Settings.TryGetValue("layer", out var v) && v is string s && (s == "ai" || s == "normal") ? s : "always";
        static bool HideFullscreen => !(Store.Settings.TryGetValue("hideFullscreen", out var v) && v is bool b && !b);
        readonly Dictionary<string, (string pose, DateTime at)> lastPose = new Dictionary<string, (string, DateTime)>();
        bool frontIsAI, frontIsShell, hiddenForFullscreen;

        bool AIActive()
        {
            if (frontIsAI) return true;
            var now = DateTime.UtcNow;
            // 等你回答：一直等到你回答（最長 10 分鐘）；剛乾完活：浮上來一會兒。
            // AI 在後臺幹活時不浮上來：選這個模式的人就是嫌擋路，幹活時往往正是你切去忙別的
            return lastPose.Values.Any(p => p.pose == "asking" ? (now - p.at).TotalMinutes < 10
                : (now - p.at).TotalSeconds < 8 && (p.pose == "happy" || p.pose == "proud"));
        }

        // ---- 藏起來 / 露出來 ----
        // 不用 Window.Hide() / Show()：透明分層視窗裡嵌著顯示卡渲染的 WebView2，反覆藏和放有機率讓 WPF 的渲染執行緒掛掉
        // （0x88980406 UCEERR_RENDERTHREADFAILURE），之後畫面凍住、摸頭沒反應，只有網頁還在跑（1.3.2 使用者反饋）。
        // 改成「隱身」：視窗一直開著，不透明度 0、滑鼠穿透、取消置頂（不壓在全屏遊戲 / 影片上面），網頁動畫暫停
        bool concealed;
        void SetConcealed(bool c)
        {
            if (Window == null || concealed == c) return;
            concealed = c;
            if (c) { openMenu?.Close(); Window.Opacity = 0; if (hwnd != IntPtr.Zero) Native.DropFromTop(hwnd); }
            else { Window.Opacity = 1; Raise(); }
            // 藏著時滑鼠穿過整個視窗（只靠不透明度 0 點不透，原因見 ConcealStyle.ClickThrough）
            if (hwnd != IntPtr.Zero) Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ConcealStyle.ClickThrough(Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE), c));
            PassThroughBrowserWindows();
            UpdatePageHidden();
        }

        /// <summary>網頁控制元件（WebView2CompositionControl）把網頁畫進我們的視窗，但背後還留著真正的瀏覽器視窗（msedgewebview2 程式，
        /// Chrome_WidgetWin_1）：位置大小和桌寵視窗一樣，排在所有程式下面、桌面上面，看不見卻會接住桌面上的點選和拖放——
        /// 藏起來後原來那塊點不透、拖檔案過去變成「複製」（1.3.4 測出來的，macOS 的網頁控制元件沒有這個視窗）。
        /// 網頁從來不用收滑鼠（摸頭、拖動、右鍵都是外殼的 hit 層處理），給它也加上滑鼠穿透。
        /// 它屬於另一個程式、建好的時間不定：網頁載入完、藏起來 / 叫回來時各補一次，加過的不再動</summary>
        void PassThroughBrowserWindows()
        {
            if (hwnd == IntPtr.Zero || env == null) return;
            HashSet<uint> pids;
            try { pids = new HashSet<uint>(env.GetProcessInfos().Select(p => (uint)p.ProcessId)); } catch { return; }
            Native.GetWindowRect(hwnd, out var me);
            int n = 0;
            Native.EnumWindows((h, _) =>
            {
                // 只動我們自己的 WebView2 程式裡、和桌寵視窗重疊的頂層視窗（別的程式也用 msedgewebview2，不能碰）
                if (h != hwnd && Native.IsWindowVisible(h) && Native.GetWindowThreadProcessId(h, out var pid) != 0 && pids.Contains(pid)
                    && Native.GetWindowRect(h, out var r) && r.Left < me.Right && r.Right > me.Left && r.Top < me.Bottom && r.Bottom > me.Top)
                {
                    int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE), want = ConcealStyle.ClickThrough(ex, true);
                    if (want != ex) { Native.SetWindowLong(h, Native.GWL_EXSTYLE, want); n++; }
                }
                return true;
            }, IntPtr.Zero);
            if (n > 0) Store.Log($"給網頁控制元件背後的瀏覽器視窗加上滑鼠穿透（{n} 個）");
        }
        /// 置頂（層級要求置頂、又沒在隱身時）
        void Raise() { if (!concealed && Window.Topmost && hwnd != IntPtr.Zero) Native.BringToTop(hwnd); }
        void UpdatePageHidden() => Js($"setPageHidden({(concealed || sessionLocked ? "true" : "false")})");

        void ApplyLayer()
        {
            if (Window == null || hwnd == IntPtr.Zero) return;
            if (userHidden) return;   // 你用快捷鍵藏起來了：什麼都別把她翻出來
            // 全屏時隱藏；全屏的是 AI 程式（對照表裡有的，專心和 AI 幹活）照常顯示，和 macOS 一致
            var hide = HideFullscreen && !fullscreenOverride && !frontIsAI && Native.InFullscreen(hwnd);
            if (hide != hiddenForFullscreen)
            {
                hiddenForFullscreen = hide;
                Store.Log(hide ? $"全屏中，先藏起來（前臺 {Native.ForegroundExe()}）" : "退出全屏，出來了");
                SetConcealed(hide);
            }
            var top = ZOrderRule.Top(LayerMode, LayerMode == "ai" && AIActive(), frontIsShell);   // 「跟著 AI」時前臺是桌面也置頂（見 ZOrderRule.Top）
            if (Window.Topmost == top || surfacing) return;
            // 原本被別的視窗擋著、要浮上來：先藏起來，換完層級再淡入（沒被擋住就直接換，免得閃一下）
            if (top && !hiddenForFullscreen && Native.Covered(hwnd)) { _ = Surface(); return; }
            Window.Topmost = top;
            if (!top) Native.DropFromTop(hwnd); else Raise();
        }

        bool surfacing;
        async Task Surface()
        {
            surfacing = true;
            try
            {
                await Eval("sink()");
                await Task.Delay(50);
                Window.Topmost = true;
                Raise();
            }
            finally
            {
                surfacing = false;
                Js("surface()");
            }
        }
    }
}
