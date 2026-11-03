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
        // ---------------- 能點到的區域 ----------------
        Canvas hit;
        static readonly Brush HitBrush = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        string lastHitRects;
        async Task UpdateHitRects()
        {
            if (hit == null || !ready || Mouse.LeftButton == MouseButtonState.Pressed) return;
            var json = await Eval("hitRects()");
            if (json == lastHitRects || json == "null") return;
            lastHitRects = json;
            if (!(Store.Json.DeserializeObject(Store.Json.Deserialize<string>(json)) is object[] list)) return;
            var zoom = Window.Width / pageW;   // 網頁畫素 → 視窗座標（畫布可能因為文字大小比 260 寬）
            hit.Children.Clear();
            foreach (var item in list)
            {
                if (!(item is object[] r) || r.Length != 4) continue;
                var box = new System.Windows.Shapes.Rectangle { Fill = HitBrush, Width = Convert.ToDouble(r[2]) * zoom, Height = Convert.ToDouble(r[3]) * zoom };
                Canvas.SetLeft(box, Convert.ToDouble(r[0]) * zoom);
                Canvas.SetTop(box, Convert.ToDouble(r[1]) * zoom);
                hit.Children.Add(box);
            }
        }

        // ---------------- 滑鼠：單擊摸頭、拖動換位置、右鍵選單 ----------------
        void WireMouse(UIElement hit)
        {
            Point down = default; bool pressed = false;
            // 按下時抓住滑鼠：能點的只有桌寵那幾塊，拖的時候滑鼠出了這幾塊也要繼續收到移動
            hit.MouseLeftButtonDown += (_, e) => { down = e.GetPosition(Window); pressed = true; hit.CaptureMouse(); };
            hit.MouseMove += (_, e) =>
            {
                if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(Window);
                if (Math.Abs(p.X - down.X) + Math.Abs(p.Y - down.Y) <= 4) return;
                pressed = false;
                hit.ReleaseMouseCapture();
                Undock();
                // 甩：拖動時橫向來回反轉方向（記住這個方向上的最遠點，往回超過 18 算一次反轉），0.9 秒內兩次以上就算在甩（惹她生氣）
                bool shook = false; double swingX = Window.Left, swingDir = 0; var swings = new List<DateTime>(); var lastShake = DateTime.MinValue;
                EventHandler onMove = (_, __) =>
                {
                    double x = Window.Left;
                    if (swingDir == 0) { if (Math.Abs(x - swingX) > 18) { swingDir = x > swingX ? 1 : -1; swingX = x; } }
                    else if ((x - swingX) * swingDir > 0) swingX = x;
                    else if ((swingX - x) * swingDir > 18)
                    {
                        swingDir = -swingDir; swingX = x;
                        swings.Add(DateTime.UtcNow);
                        swings.RemoveAll(t => (DateTime.UtcNow - t).TotalSeconds > 0.9);
                        if (swings.Count >= 2 && (DateTime.UtcNow - lastShake).TotalSeconds > 0.3) { shook = true; lastShake = DateTime.UtcNow; Js("shaken()"); }
                    }
                };
                Window.LocationChanged += onMove;
                try { Window.DragMove(); } catch { }
                Window.LocationChanged -= onMove;
                SavePosition();
                if (!shook) CheckDock();   // 甩她的時候鬆手不貼邊
            };
            hit.MouseLeftButtonUp += (_, e) =>
            {
                hit.ReleaseMouseCapture();
                if (!pressed) return;
                pressed = false;
                // 點的位置換成網頁座標（CSS 畫素，和可點區域一樣按 視窗寬 / 畫布寬 換算；不用 ZoomFactor：縮放被自動修正過時它不等於人物大小）：
                // 網頁要知道是不是點在「新功能」卡片的按鈕上
                var p = e.GetPosition(hit); var z = Math.Max(Window.Width / pageW, 0.01);
                Js($"handleClick('pat', {Num(Math.Round(p.X / z, 1))}, {Num(Math.Round(p.Y / z, 1))})");
            };
            hit.MouseRightButtonUp += (_, __) =>
            {
                openMenu?.Close();
                var menu = new Forms.ContextMenuStrip();
                // 按住 Shift 右鍵才出現「開發者控制台」（macOS 版是 ⌥）
                FillMenu(menu, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                // 選單靠「程式失去焦點」來關，可桌寵視窗從不拿焦點（WS_EX_NOACTIVATE），點別的程式它收不到，選單一直掛著。
                // 開著時自己看：滑鼠在選單（含展開的子選單）外面按下就關
                var watch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                watch.Tick += (_, ___) =>
                {
                    if (Forms.Control.MouseButtons != Forms.MouseButtons.None && !MenuCovers(menu, Forms.Cursor.Position)) menu.Close();
                };
                menu.Opened += (_, ___) => watch.Start();
                menu.Closed += (_, ___) =>
                {
                    watch.Stop();
                    if (openMenu == menu) openMenu = null;
                    Window.Dispatcher.BeginInvoke(new Action(menu.Dispose));
                };
                openMenu = menu;
                menu.Show(Forms.Cursor.Position);
            };
        }

        /// <summary>開著的右鍵選單：藏起來（全屏、快捷鍵）時一起關掉，不然桌寵藏了、選單還單獨掛在螢幕上</summary>
        Forms.ContextMenuStrip openMenu;

        static bool MenuCovers(Forms.ToolStripDropDown d, System.Drawing.Point p) =>
            d.Visible && (d.Bounds.Contains(p) || d.Items.OfType<Forms.ToolStripDropDownItem>().Any(i => i.HasDropDownItems && MenuCovers(i.DropDown, p)));
    }
}
