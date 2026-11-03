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
        // ---------------- 貼邊 ----------------
        // 拖到螢幕左 / 右邊緣鬆手：她縮到螢幕外，只露半個身子扒著邊緣（額度、名牌、氣泡都藏起來）。
        // AI 幹活時（或在等你回答）自己跑出來，幹完過約 10 秒縮回去；滑鼠移到露出來的那截上她會探出來一點。
        // 拖離邊緣就不貼了。另一塊螢幕接在這邊時不算邊緣。座標都是物理畫素
        string dock;                 // "left" / "right"
        bool dockOut, dockHover, dockTucked;   // dockTucked：現在是縮著的樣子（之後換探出來不用先挪視窗）
        DateTime? dockLeaveAt, dockEnterAt;
        DateTime dockedAt = DateTime.MinValue;   // 剛拖到邊上：之前就在進行的工作不算，先縮排去給個反應，AI 有新動作再跑出來
        readonly Dictionary<string, double> dockCut = new Dictionary<string, double>();   // 切口位置（網頁畫素），按 邊 + 角色 記
        /// <summary>DeepSeek 餘額（預設開）：用別家訂閱（OpenCode Go……）跑 DeepSeek 模型時官方賬戶沒錢，她會一直沒精神，可以關掉</summary>
        /// <summary>省電模式（預設關）：特效每秒 15 幀、閒著 10 秒就停（平時 30 幀、30 秒）</summary>
        static bool PowerSave => Store.Settings.TryGetValue("powerSave", out var v) && v is bool b && b;
        /// <summary>額度合併顯示（預設關）：小窗把各家 AI 的額度排成幾行，而不是隻顯示當前角色的。怎麼排在網頁裡（quota-aggregate.js）</summary>
        static bool QuotaAggregate => Store.Flag("quotaAggregate");
        static string QuotaStyle => Store.Settings.TryGetValue("quotaStyle", out var v) && v is string s && s == "bars" ? "bars" : "text";   // 額度怎麼顯示：text 文字（預設）/ bars 額度條（網頁層 quota-bars.js）
        static string PeakStyle => Store.Settings.TryGetValue("peakStyle", out var v) && v is string s && (s == "text" || s == "off") ? s : "bar";   // 額度欄怎麼顯示峰谷時段：bar 時間條（預設）/ text 一行文字 / off（規則在網頁層 peak-hours.js）
        static bool CodexResets => Store.Flag("codexResets");   // Codex 重置提醒（預設關，資料來自 codex-resets.com）
        /// <summary>按時段換衣服：{ 角色: { peak: 皮膚, valley: 皮膚 } }（"" = 原版，沒寫 = 不換）。判斷在網頁層（peak-hours.js），這裡只存（和 macOS 的 cleanPeakSkins 一致）</summary>
        static Dictionary<string, object> CleanPeakSkins(object v)
        {
            var output = new Dictionary<string, object>();
            if (!(v is Dictionary<string, object> all)) return output;
            foreach (var kv in all)
            {
                if (!(kv.Value is Dictionary<string, object> d)) continue;
                var e = new Dictionary<string, object>();
                foreach (var k in new[] { "peak", "valley" }) if (d.TryGetValue(k, out var s) && s is string str) e[k] = str;
                if (e.Count > 0) output[kv.Key] = e;
            }
            return output;
        }
        static Dictionary<string, object> PeakSkins => CleanPeakSkins(Store.Settings.TryGetValue("peakSkins", out var v) ? v : null);
        static bool DsQuotaEnabled => !(Store.Settings.TryGetValue("dsQuota", out var v) && v is bool b && !b);
        static bool EdgeDockEnabled => !(Store.Settings.TryGetValue("edgeDock", out var v) && v is bool b && !b);
        static void After(int ms, Action a)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, __) => { t.Stop(); a(); };
            t.Start();
        }

        void Undock()
        {
            if (dock == null) return;
            dock = null; dockOut = dockHover = dockTucked = false;
            Store.Settings.Remove("dock"); Store.SaveSettings();
            Js("setTucked(null)");
        }
        void CheckDock()
        {
            if (!EdgeDockEnabled || hwnd == IntPtr.Zero) return;
            Native.GetWindowRect(hwnd, out var r);
            int w = r.Right - r.Left, mid = r.Left + w / 2;
            var scr = Forms.Screen.FromHandle(hwnd);
            var wa = scr.WorkingArea;
            double reach = w * (BaseWidth / pageW) * 0.3;   // 按人物那塊的寬算：畫布因為文字變寬時不該更容易貼邊
            bool Neighbor(bool left) => Forms.Screen.AllScreens.Any(o => !o.Equals(scr) && o.Bounds.Top < r.Bottom && o.Bounds.Bottom > r.Top &&
                (left ? Math.Abs(o.Bounds.Right - scr.Bounds.Left) < 2 : Math.Abs(o.Bounds.Left - scr.Bounds.Right) < 2));
            if (mid - wa.Left < reach && !Neighbor(true)) SetDock("left");
            else if (wa.Right - mid < reach && !Neighbor(false)) SetDock("right");
        }
        void SetDock(string side)
        {
            dock = side;
            dockedAt = DateTime.UtcNow;
            Store.Settings["dock"] = side; Store.SaveSettings();
            dockOut = DockBusy();
            ApplyDock();
        }
        /// <summary>AI 在幹活 / 等你回答 / 剛乾完（10 秒內）：跑出來</summary>
        bool DockBusy()
        {
            var now = DateTime.UtcNow;
            return lastPose.Values.Any(p =>
            {
                if (p.at <= dockedAt) return false;
                var age = (now - p.at).TotalSeconds;
                switch (p.pose)
                {
                    case "asking": return age < 600;
                    case "happy": case "proud": case "oops": case "surprised": return age < 10;
                    case "idle": case "sleeping": case "tired": return false;
                    default: return age < 180;
                }
            });
        }
        void UpdateDock()
        {
            if (dock == null) return;
            var busy = DockBusy();
            if (busy != dockOut) { dockOut = busy; dockHover = false; ApplyDock(); }
        }
        async void ApplyDock()
        {
            var side = dock;
            if (side == null || hwnd == IntPtr.Zero) return;
            hit?.Children.Clear(); lastHitRects = null;   // 換了樣子，可點區域重新量（舊的範圍會讓她誤以為滑鼠在身上，來回閃）
            After(500, () => { lastHitRects = null; _ = UpdateHitRects(); });   // 新圖換好後再量一次
            Native.GetWindowRect(hwnd, out var r);
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            var wa = Forms.Screen.FromHandle(hwnd).WorkingArea;
            int y = Math.Max(wa.Top, Math.Min(r.Top, wa.Bottom - (int)(h * 0.85)));
            int edgeX = side == "left" ? wa.Left : wa.Right - w;
            if (dockOut) { dockTucked = false; Js("setTucked(null)"); Slide(edgeX, y); return; }
            // 視窗還沒到邊上（剛拖過來鬆手 / 剛乾完活）：先整個滑到螢幕邊緣，滑完再縮排去。一次只做一個動作，不然視窗在滑、人物也在視窗裡滑，看著亂
            if (!dockTucked && (Math.Abs(r.Left - edgeX) > 1 || Math.Abs(r.Top - y) > 1))
            {
                Slide(edgeX, y);
                await Task.Delay(370);
                if (dock != side || dockOut) return;
            }
            dockTucked = true;
            // 滑鼠移上來：換成探出來更多的那張（edge_*_peek），切口照樣對齊螢幕邊緣（不能光把視窗往外挪，切口會露在螢幕中間）
            var peek = dockHover;
            Js($"setTucked({Q(side)}, {(peek ? "true" : "false")})");
            var key = side + (peek ? "-peek" : "") + current;
            if (!dockCut.TryGetValue(key, out var cut))
            {
                await Task.Delay(450);   // 換成貼邊立繪後再量切口（圖要先載入），量過的記下來
                if (dock != side || dockOut || dockHover != peek) return;
                var res = await Eval($"dockCut({Q(side)})");
                if (!double.TryParse(res, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cut)) return;
                dockCut[key] = cut;
            }
            if (dock != side || dockOut || dockHover != peek) return;
            double zoom = (double)w / pageW;   // 網頁畫素 → 物理畫素
            int tx = side == "left" ? wa.Left - (int)(cut * zoom) : wa.Right - (int)(cut * zoom);
            Native.GetWindowRect(hwnd, out var now);
            if (Math.Abs(now.Left - tx) > 1) Slide(tx, y);
        }
        DispatcherTimer slideTimer;
        void Slide(int x, int y)
        {
            Native.GetWindowRect(hwnd, out var r);
            int x0 = r.Left, y0 = r.Top;
            var start = DateTime.UtcNow;
            slideTimer?.Stop();
            slideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            slideTimer.Tick += (_, __) =>
            {
                var k = Math.Min(1, (DateTime.UtcNow - start).TotalSeconds / 0.35);
                var e = k < 0.5 ? 2 * k * k : 1 - Math.Pow(-2 * k + 2, 2) / 2;
                Native.MoveTo(hwnd, (int)(x0 + (x - x0) * e), (int)(y0 + (y - y0) * e));
                if (k >= 1) slideTimer.Stop();
            };
            slideTimer.Start();
        }
        /// <summary>縮著時滑鼠在露出來的那截上停 0.15 秒：探出來；移開 0.3 秒後縮回去</summary>
        void UpdateDockHover()
        {
            if (dock == null || dockOut || hwnd == IntPtr.Zero) return;
            if (hit != null && hit.Children.Count == 0) _ = UpdateHitRects();   // 剛換了圖：馬上量能點的範圍，探出來 / 縮回去才跟得上滑鼠
            bool inside = hit != null && hit.IsMouseOver;   // 只算露出來的那截（能點的區域），視窗其餘是透明的
            if (inside)
            {
                dockLeaveAt = null;
                if (dockHover) return;
                if (dockEnterAt == null) dockEnterAt = DateTime.UtcNow;   // 停一小會兒才探出來，路過不算
                else if ((DateTime.UtcNow - dockEnterAt.Value).TotalSeconds > 0.15) { dockEnterAt = null; dockHover = true; ApplyDock(); }
                return;
            }
            dockEnterAt = null;
            if (dockHover)
            {
                if (dockLeaveAt == null) dockLeaveAt = DateTime.UtcNow;
                else if ((DateTime.UtcNow - dockLeaveAt.Value).TotalSeconds > 0.3) { dockHover = false; dockLeaveAt = null; ApplyDock(); }
            }
        }
    }
}
