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
        // ---------------- 設定視窗（web/settings.html，和 macOS 版共用） ----------------
        static double Scale => Store.Settings.TryGetValue("size", out var v) && (v is double || v is int || v is decimal) &&
                               Convert.ToDouble(v) >= 0.6 && Convert.ToDouble(v) <= 1.6 ? Convert.ToDouble(v) : 1;
        static bool ShowName => !(Store.Settings.TryGetValue("showName", out var v) && v is bool b && !b);
        static bool EggsEnabled => !(Store.Settings.TryGetValue("eggs", out var v) && v is bool b && !b);
        /// <summary>文字大小（只在解鎖時用）和鎖：沒存過 = 鎖著，文字跟著人物，和以前一樣</summary>
        static double TextScale => Store.Settings.TryGetValue("textSize", out var v) && (v is double || v is int || v is decimal) &&
                                   Convert.ToDouble(v) >= 0.6 && Convert.ToDouble(v) <= 1.6 ? Convert.ToDouble(v) : 1;
        static bool TextLinked => !(Store.Settings.TryGetValue("textLinked", out var v) && v is bool b && !b);
        static string Num(double d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>換大小時視窗錨在哪（和 macOS 的 App.Anchor 一致，那邊有測試 tests/window_place_probe.sh）：
        /// Corner = 啟動 / 網頁過載，存下的位置本來就是這個畫布的視窗，左上角不動只換大小（不然解鎖的人每次啟動都往旁邊漂）；
        /// Bottom = 改人物大小，底邊中點不動，和以前一樣（螢幕上的底欄高 = 62 × 文字大小，沒變）；
        /// Feet = 改文字大小，底欄在螢幕上變高 / 變矮多少，視窗底邊就往下 / 往上挪多少，人物不動</summary>
        enum Anchor { Corner, Bottom, Feet }

        /// <summary>純計算，物理畫素、y 向下。oldFoot / newFoot：底欄在螢幕上的高</summary>
        static (int, int) PlaceWindow(Native.RECT old, int w, int h, double oldFoot, double newFoot, Anchor anchor)
        {
            if (anchor == Anchor.Corner) return (old.Left, old.Top);
            int x = (old.Left + old.Right) / 2 - w / 2;
            if (anchor == Anchor.Feet) return (x, old.Bottom + (int)Math.Round(newFoot - oldFoot) - h);
            return (x, old.Bottom - h);
        }

        // ---- 網頁大小核對：記日誌；系統縮放被算了兩遍時自動修正（見 ZoomCheck.Fix）----
        double shownZoom;   // 視窗現在按的人物大小
        double zoomFix = 1;   // 給網頁的縮放 = 人物大小 × 這個係數；只有 ZoomCheck.Fix 認出「系統縮放算了兩遍」時才不是 1
        int zoomFixes;   // 這次執行修正過幾次：最多 3 次，免得來回改
        DispatcherTimer zoomCheckTimer;
        /// <summary>調完大小 / 換完縮放停 1 秒再量（拖滑塊時每一幀都在變，別每幀都量）</summary>
        void CheckZoomSoon()
        {
            if (zoomCheckTimer == null)
            {
                zoomCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                zoomCheckTimer.Tick += (_, __) => { zoomCheckTimer.Stop(); _ = CheckZoom(); };
            }
            zoomCheckTimer.Stop(); zoomCheckTimer.Start();
        }
        async Task<(double, double)?> PageSize()
        {
            var json = await Eval("JSON.stringify([innerWidth, innerHeight])");
            if (!(Store.Json.DeserializeObject(Store.Json.Deserialize<string>(json)) is object[] a) || a.Length != 2) return null;
            return (Convert.ToDouble(a[0]), Convert.ToDouble(a[1]));
        }
        /// <summary>隔 0.5 秒量兩次網頁實際排出來的大小，兩次一樣又和應有的畫布對不上，記一行日誌</summary>
        async Task CheckZoom()
        {
            if (!ready || web?.CoreWebView2 == null || shownZoom <= 0) return;
            try
            {
                var first = await PageSize();
                await Task.Delay(500);
                var second = await PageSize();
                if (first == null || second == null) return;
                var (w1, h1) = first.Value; var (w2, h2) = second.Value;
                if (!ZoomCheck.Off(pageW, pageH, w1, h1, w2, h2)) return;
                var dpi = VisualTreeHelper.GetDpi(Window).DpiScaleX;
                // #28：那臺機器這之後 4~5 秒渲染執行緒就掛了，這行是先兆，寫成警告；再逼網頁控制元件按新縮放對齊，最多兩次，還不行就走下面的老路
                if (dpiFrom > 0 && dpiResyncs < 2 && ZoomCheck.StuckAtOldDpi(pageW, pageH, w2, h2, dpiFrom, dpi))
                {
                    dpiResyncs++;
                    Store.Log($"警告：換縮放後網頁區域還停在舊縮放上：網頁 {Math.Round(w2)}×{Math.Round(h2)}，應為 {Math.Round(pageW)}×{Math.Round(pageH)}，{Math.Round(dpiFrom * 100)}% → {Math.Round(dpi * 100)}%，再對齊一次（第 {dpiResyncs} 次）");
                    await ResizeWebBounds();
                    CheckZoomSoon();
                    return;
                }
                var fix = zoomFixes < 3 ? ZoomCheck.Fix(pageW, pageH, w2, h2, dpi, zoomFix) : null;
                Store.Log($"網頁大小和畫布對不上{(fix == null ? "（只記錄）" : "")}：網頁 {Math.Round(w2)}×{Math.Round(h2)}，應為 {Math.Round(pageW)}×{Math.Round(pageH)}，縮放 {Num(Math.Round(web.ZoomFactor, 3))}，DPI {Math.Round(dpi * 100)}%");
                if (fix == null) return;
                zoomFixes++; zoomFix = fix.Value;
                web.ZoomFactor = shownZoom * zoomFix;
                lastHitRects = null; dockCut.Clear();
                Store.Log($"系統縮放像是被算了兩遍：網頁縮放自動改成 {Num(Math.Round(web.ZoomFactor, 3))}（人物大小 {Num(shownZoom)} × {Num(Math.Round(zoomFix, 3))}），再量一次");
                CheckZoomSoon();
            }
            catch (Exception e) { Store.Log("檢查網頁大小出錯: " + e.Message); }
        }

        /// <summary>系統縮放變了（改了縮放、拖到縮放不同的另一塊螢幕）：WebView2CompositionControl 只在自己的大小（DIP）變時
        /// 才按新 DPI 重算網頁區域的物理畫素（它的 SizeChanged → SyncControllerBounds），換 DPI 時 DIP 大小不變，網頁區域就停在舊的
        /// 畫素尺寸上，網頁卻按新縮放畫，結果裁掉一截，重啟才好（1.3.5 測試在 100%→150% 復現）。這裡把它的寬度縮 1 再放回，逼它重算</summary>
        double resyncedDpi;   // 上次對齊時的縮放：同一次換縮放 DpiChanged 會來兩遍（1.3.5 測試日誌裡每次兩行），只對齊一次
        double dpiFrom;   // 最近一次換縮放前的縮放（0 = 這次執行沒換過）：CheckZoom 靠它認出「網頁區域還停在舊縮放上」
        int dpiResyncs;   // 這次換縮放後補對齊了幾次
        void ResyncWebForDpi(double oldDpi, double newDpi)
        {
            if (web == null || Math.Abs(newDpi - resyncedDpi) < 0.001) return;
            resyncedDpi = newDpi;
            dpiFrom = oldDpi; dpiResyncs = 0;
            Store.Log($"系統縮放變了：{Math.Round(oldDpi * 100)}% → {Math.Round(newDpi * 100)}%，網頁區域重新對齊");
            web.Width = Math.Max(1, web.ActualWidth - 1); web.UpdateLayout();   // 縮小（放大會超出視窗，不一定真的變）
            web.Width = double.NaN; web.UpdateLayout();
            lastHitRects = null; dockCut.Clear();   // 可點區域、貼邊切口都按新畫素重新量
            CheckZoomSoon();   // 再量一次網頁大小：還停在舊縮放上就補對齊（StuckAtOldDpi），別的對不上記日誌
        }

        /// <summary>補對齊：上面那招在 #28 那臺機器（虛擬顯示器 + 混合顯示卡）上沒成功，可能是縮 1 再放回在同一幀裡被合併了。
        /// 這次縮得多一點，並且等它真的按縮小的大小畫完一幀再放回</summary>
        async Task ResizeWebBounds()
        {
            if (web == null) return;
            web.Width = Math.Max(1, Math.Round(web.ActualWidth * 0.9)); web.UpdateLayout();
            await Window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
            await Task.Delay(100);
            if (web == null) return;
            web.Width = double.NaN; web.UpdateLayout();
            lastHitRects = null; dockCut.Clear();
        }

        /// <summary>改大小：視窗 = 畫布 × 人物大小，網頁按人物大小縮放；放在哪見 Anchor</summary>
        void ApplyScale(double scale, Anchor anchor = Anchor.Bottom)
        {
            Native.GetWindowRect(hwnd, out var old);
            Window.Width = pageW * scale; Window.Height = pageH * scale;
            Window.UpdateLayout();
            if (web != null) web.ZoomFactor = scale * zoomFix;
            shownZoom = scale;
            CheckZoomSoon();
            Native.GetWindowRect(hwnd, out var now);
            int w = now.Right - now.Left, h = now.Bottom - now.Top;
            double footPx = pageFoot * w / pageW;   // 網頁畫素 → 物理畫素（含系統縮放）
            var (x, y) = PlaceWindow(old, w, h, shownFootPx > 0 ? shownFootPx : footPx, footPx, anchor);
            shownFootPx = footPx;
            if (dock == null) (x, y) = Clamp(x, y, w, h);   // 貼著邊時不夾：視窗本來就有一截在螢幕外，之後 ApplyDock 會重新對齊
            Native.MoveTo(hwnd, x, y);
            SavePosition();
        }

        /// <summary>人物大小 / 文字大小變了：交給網頁算畫布（規則在 app/web/text-size.js），按回來的尺寸設視窗。
        /// target：人物大小要動畫到哪（底邊不動）；null = 文字變了（Feet）或網頁剛載入完（Corner），按現在的人物大小重設一次</summary>
        async void ApplySizes(double? target = null, Anchor anchor = Anchor.Feet)
        {
            if (ready && web?.CoreWebView2 != null)   // 網頁還沒載入完：先按舊畫布，載入完（NavigationCompleted）會再調一次
            {
                try
                {
                    var json = await Eval($"setSizes({Num(target ?? Scale)}, {Num(TextScale)}, {(TextLinked ? "true" : "false")})");
                    if (Store.Json.DeserializeObject(Store.Json.Deserialize<string>(json)) is Dictionary<string, object> o)
                    {
                        double w = Convert.ToDouble(o["w"]), h = Convert.ToDouble(o["h"]), foot = Convert.ToDouble(o["foot"]);
                        if (w != pageW || h != pageH || foot != pageFoot) dockCut.Clear();   // 畫布變了：貼邊切口要重新量
                        pageW = w; pageH = h; pageFoot = foot;
                    }
                }
                catch (Exception e) { Store.Log("文字大小：算畫布失敗 " + e.Message); }
            }
            if (target is double t) { AnimateScale(t); return; }
            ApplyScale(shownScale > 0 ? shownScale : Scale, anchor);
            if (anchor == Anchor.Feet && dock != null) ApplyDock();   // 貼著邊時改了文字：視窗變寬了，重新對齊螢幕邊
        }

        /// <summary>拖滑塊時每次只變一點，直接跟手；一下子變很多（點「還原」）時用 0.2 秒過渡過去</summary>
        DispatcherTimer scaleTimer;
        double shownScale;
        void AnimateScale(double target)
        {
            scaleTimer?.Stop();
            var from = shownScale > 0 ? shownScale : Scale;
            if (Math.Abs(target - from) <= 0.06) { shownScale = target; ApplyScale(target); return; }
            var start = DateTime.Now;
            scaleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            scaleTimer.Tick += (_, __) =>
            {
                var k = Math.Min(1, (DateTime.Now - start).TotalSeconds / 0.2);
                shownScale = from + (target - from) * (1 - Math.Pow(1 - k, 3));
                ApplyScale(shownScale);
                if (k >= 1) { scaleTimer.Stop(); if (dock != null) ApplyDock(); }   // 貼著邊時調完大小重新對齊邊緣
            };
            scaleTimer.Start();
        }

        /// <summary>tab：停在哪個標籤頁（null = 上次看的那頁）；視窗已經開著就切過去</summary>
        void OpenSettings(string tab = null)
        {
            if (settings == null) { settings = new SettingsWindow(this, env); settings.Closed += () => settings = null; }
            settings.Show();
            if (tab != null) settings.ShowTab(tab);
        }

        /// <summary>換角色視窗（右鍵「換角色…」、設定裡的按鈕）：大立繪 + 衣服卡 + 角色卡，可以拖大小，記住上次的大小</summary>
        void OpenCharacterWindow()
        {
            if (characterWindow == null)
            {
                var size = Store.Settings.TryGetValue("characterWindow", out var v) && v is Dictionary<string, object> d ? d : null;
                double W(string k, double def) => size != null && size.TryGetValue(k, out var x) && x != null ? Convert.ToDouble(x) : def;
                characterWindow = new SettingsWindow(this, env, "characters.html", "CrossPet 換角色", W("w", 760), W("h", 740));
                characterWindow.Closed += () =>
                {
                    var s = characterWindow.Size;
                    if (s.Width > 0 && s.Height > 0) { Store.Settings["characterWindow"] = new Dictionary<string, object> { ["w"] = Math.Round(s.Width), ["h"] = Math.Round(s.Height) }; Store.SaveSettings(); }
                    characterWindow = null;
                };
            }
            characterWindow.Show();
        }

        /// <summary>設定視窗、換角色視窗開著哪個就推給哪個</summary>
        void PushSettings() { settings?.Push(); characterWindow?.Push(); }

        List<string> integrated;   // 已接入的 AI（設定視窗開啟時查一次，接入 / 撤銷後再查）
    }
}
