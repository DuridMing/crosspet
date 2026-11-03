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
    /// <summary>
    /// 桌寵本體。和 macOS 版（app/mac/）做的事一樣：一個透明、置頂、不搶焦點的小視窗裡放桌寵網頁（app/web/index.html），
    /// 讀 AI 寫來的狀態檔案交給網頁演，跟著前臺程式換角色，右鍵選單 / 托盤 / 接入 AI / 額度 / 檢查更新。
    /// 網頁用 WebView2CompositionControl：它把網頁畫面當圖片放進 WPF 透明視窗，才能做到逐畫素半透明（柔和邊緣、光暈）。
    /// </summary>
    sealed partial class Pet
    {
        const string Repo = "DuridMing/crosspet";   // maid fork：更新來源指向自己的 fork，不會被上游覆蓋
        CoreWebView2Environment env;   // 瀏覽器程式崩潰後要換一個新的（見 RecoverWeb）
        Grid grid;
        public Window Window { get; private set; }
        WebView2CompositionControl web;
        IntPtr hwnd;
        bool ready;
        string current;
        bool paused;
        public bool Paused                     // 開發者控制台測試時，先不把真實 AI 的狀態交給桌寵
        {
            get => paused;
            set { if (paused != value) ++followEventGeneration; paused = value; }
        }
        public string ManifestJson { get; private set; } = "{}";
        readonly Dictionary<string, string> stamps = new Dictionary<string, string>();
        (string tag, string url)? latestRelease;
        string updateZip;    // 新版本的 Windows 安裝包下載地址（只認本倉庫 Releases 裡的）
        bool updating;
        Forms.NotifyIcon tray;
        Native.ForegroundWatcher foreground;
        DispatcherTimer appSwitch;
        DevConsole dev;
        SettingsWindow settings, characterWindow;   // 設定視窗、換角色視窗（同一套狀態，見 PushSettings）
        FeedbackWindow feedback;
        const double BaseWidth = 260, BaseHeight = 362;
        // 文字大小：網頁按「人物大小、文字大小」算出的畫布（網頁 CSS 尺寸），視窗 = 畫布 × 人物大小（app/web/text-size.js）
        double pageW = BaseWidth, pageH = BaseHeight;
        double pageFoot = 62;      // 畫布底欄高（網頁畫素）
        double shownFootPx;        // 視窗現在的底欄在螢幕上多高（物理畫素）；0 = 還沒按畫布設過

        public Pet(CoreWebView2Environment env) { this.env = env; }

        // ---------------- 啟動 ----------------
        public void Start()
        {
            Store.SyncResources();
            current = Store.Settings.TryGetValue("character", out var c) && c is string s ? s : "claude";

            Window = new Window
            {
                Title = "CrossPet", WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                Width = BaseWidth * Scale, Height = BaseHeight * Scale, Topmost = LayerMode != "normal", ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
                Left = -10000, Top = -10000,   // 先放螢幕外，拿到視窗控制程式碼後按畫素擺到儲存的位置
            };
            Window.SourceInitialized += (_, __) =>
            {
                hwnd = new WindowInteropHelper(Window).Handle;
                Native.MakeToolWindow(hwnd);
                RestorePosition();
                HwndSource.FromHwnd(hwnd)?.AddHook(HotKeyHook);
                RegisterHideHotKey();
            };

            grid = new Grid();
            web = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.Transparent, IsHitTestVisible = false };
            grid.Children.Add(web);
            // 透明的接滑鼠層：拖動 / 單擊 / 右鍵由視窗自己處理（和 macOS 版的 DragView 一樣），網頁只負責畫
            // 只在桌寵本體、氣泡、名牌、額度上鋪：視窗其餘地方完全透明，點選直接落到後面的視窗
            // （視窗比桌寵大，是給動作、氣泡留的空間；透明視窗裡全透明的畫素本來就不接滑鼠）
            hit = new Canvas();
            grid.Children.Add(hit);
            Window.Content = grid;
            WireMouse(hit);
            Every(TimeSpan.FromMilliseconds(250), () => { _ = UpdateHitRects(); });
            Every(TimeSpan.FromMilliseconds(80), UpdateDockHover);

            // 換 DPI 時 WPF 先按新縮放排好視窗再發這個事件；等這一輪排版走完再讓網頁控制元件對齊（原因見 ResyncWebForDpi）
            Window.DpiChanged += (_, e) => Window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => ResyncWebForDpi(e.OldDpi.DpiScaleX, e.NewDpi.DpiScaleX)));
            Window.Loaded += async (_, __) => await InitWeb();
            Window.Show();

            tray = new Forms.NotifyIcon { Text = "CrossPet", Visible = true };
            try { tray.Icon = new System.Drawing.Icon(Path.Combine(Store.Install, "AppIcon.ico")); } catch { tray.Icon = System.Drawing.SystemIcons.Application; }
            var trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Opening += (_, e) => { FillMenu(trayMenu, false); e.Cancel = false; };
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += (_, __) => Recenter();

            // 前臺程式一變：重新置頂（別的置頂視窗啟用後會蓋過來），停留半秒再按對照表換角色（啟動時視窗會來回搶焦點）
            appSwitch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            appSwitch.Tick += (_, __) => { appSwitch.Stop(); FollowForeground(); };
            foreground = new Native.ForegroundWatcher(() =>
            {
                ++followEventGeneration;
                fullscreenOverride = false;   // 前臺視窗換了：雙擊托盤臨時叫出來的作廢，重新按全屏規則來
                if (hwnd != IntPtr.Zero) Raise();
                appSwitch.Stop(); appSwitch.Start();
            });

            Every(TimeSpan.FromMilliseconds(300), Poll);
            Every(TimeSpan.FromSeconds(60), () => { RefreshQuota(); RefreshCodexQuota(CodexWhy.Tick); });
            Every(TimeSpan.FromSeconds(3), WatchClaudeConfig);
            // 鎖屏時動畫全停（看不見還在畫，白白耗電）
            SystemEvents.SessionSwitch += (_, e) =>
            {
                if (e.Reason == SessionSwitchReason.SessionLock || e.Reason == SessionSwitchReason.SessionUnlock)
                    Window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var locked = e.Reason == SessionSwitchReason.SessionLock;
                        var wasLocked = sessionLocked; sessionLocked = locked;
                        UpdatePageHidden();
                        if (wasLocked && !locked) RefreshCodexQuota(CodexWhy.Tick);   // 鎖屏時沒查：解鎖補一次（自帶間隔，不會多問）
                    }));
            };
            // 檢查更新：啟動後 20 秒、之後每 3 小時，電腦從睡眠中醒來時也查一次（很多人的電腦一直不關機，只是合蓋）
            Every(TimeSpan.FromHours(3), () => _ = CheckForUpdate());
            Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
                var wake = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };   // 等網路連上
                wake.Tick += (__, ___) => { wake.Stop(); _ = CheckForUpdate(); };
                wake.Start();
            };
            var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            first.Tick += (_, __) => { first.Stop(); _ = CheckForUpdate(); };
            first.Start();
        }

        static void Every(TimeSpan t, Action a)
        {
            var timer = new DispatcherTimer { Interval = t };
            timer.Tick += (_, __) => { try { a(); } catch (Exception e) { Store.Log("定時任務出錯: " + e); } };
            timer.Start();
        }

        int imageErrors;
        async Task InitWeb()
        {
            try
            {
                await web.EnsureCoreWebView2Async(env);
                var core = web.CoreWebView2;
                Map(core);
                core.NavigationStarting += (_, a) => { if (!a.Cancel) { ++followEventGeneration; ready = false; } };
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                web.ZoomFactor = Scale * zoomFix;
                // 網頁程式 / 瀏覽器程式崩潰（顯示卡驅動、系統睡眠醒來、記憶體不夠時偶爾會）：不處理的話她就一直停住，
                // 之後每次調網頁都報「控制元件已失效」
                core.ProcessFailed += (_, a) => Window.Dispatcher.BeginInvoke(new Action(() => _ = RecoverWeb(a.ProcessFailedKind)));
                core.NavigationCompleted += async (_, a) =>
                {
                    ready = false;
                    if (!a.IsSuccess) { Store.Log("桌寵頁面載入失敗: " + a.WebErrorStatus); return; }
                    LoadCharacters();
                    await core.ExecuteScriptAsync($"init({ManifestJson}); setCharacter({Q(current)}, true); setAuraMode({Q(AuraMode)}); setShowName({(ShowName ? "true" : "false")}); setEggsEnabled({(EggsEnabled ? "true" : "false")}); setPowerSave({(PowerSave ? "true" : "false")}); setQuotaAggregate({(QuotaAggregate ? "true" : "false")}); setCodexResets({(CodexResets ? "true" : "false")}); setPeakStyle({Q(PeakStyle)}); setQuotaStyle({Q(QuotaStyle)}); setPeakSkins({Store.Json.Serialize(PeakSkins)})");
                    if (Store.Settings.TryGetValue("dock", out var dk) && dk is string side && EdgeDockEnabled) After(1500, () => SetDock(side));   // 上次是貼著邊的
                    // 剛更新完（上次開啟的是別的版本）：讓她說一聲
                    var last = Store.Settings.TryGetValue("lastVersion", out var lv) ? lv as string : null;
                    // 「新功能」頁看到哪一版了（設定裡的小紅點用）：沒記過時，老使用者按上次的版本算（有沒看過的就亮），新安裝按當前版本（不亮）。要在下面改掉 lastVersion 之前記
                    if (!(Store.Settings.TryGetValue("whatsNewSeen", out var wn) && wn is string)) { Store.Settings["whatsNewSeen"] = last ?? Store.Version; Store.SaveSettings(); }
                    if (last != Store.Version)
                    {
                        Store.Settings["lastVersion"] = Store.Version; Store.SaveSettings();
                        // 帶上上次的版本：這版（跳過的幾版合在一起）有新功能就彈「新功能」卡片（見 whats-new.js）
                        if (last != null && Newer(Store.Version, last)) Js($"justUpdated({Q("v" + Store.Version)}, {Q(last)})");
                    }
                    ready = true;
                    if (RenderRecovery.Ready()) tray?.ShowBalloonTip(6000, "CrossPet", "上次畫面出了問題，現已重新開啟。若再次消失，請提交反饋。", Forms.ToolTipIcon.Info);
                    ApplySizes(anchor: Anchor.Corner);   // 解鎖過文字大小的：按畫布重設視窗，左上角不動（鎖著的尺寸不變）
                    After(1000, PassThroughBrowserWindows); After(5000, PassThroughBrowserWindows);   // 背後的瀏覽器視窗不接滑鼠（見 PassThroughBrowserWindows）
                    stamps.Clear();
                    Poll();
                    RefreshQuota();
                    RefreshCodexQuota(CodexWhy.Now);
                    RefreshPlugins(tellPet: true);   // 外掛舊了：等她閒下來提醒一句
                    FollowForeground("startup");
                };
                // 立繪載入失敗時網頁發訊息過來：記進日誌（每次啟動最多 5 條），反饋裡能看出是哪張圖、為什麼
                core.WebMessageReceived += (_, a) =>
                {
                    try
                    {
                        if (!(Store.Json.DeserializeObject(a.WebMessageAsJson) is Dictionary<string, object> m)) return;
                        // 「新功能」卡片點了「去看看」：開啟設定停在「新功能」頁
                        if (m.TryGetValue("openSettings", out var tab) && tab is string t) { if (t == "character") OpenCharacterWindow(); else OpenSettings(t); return; }
                        // 按時段換衣服（網頁層判斷好了時段）：只換這個角色已有的衣服，不切換當前角色（和 macOS 的 handlePet 一致）
                        if (m.TryGetValue("wear", out var wv) && wv is Dictionary<string, object> w && w.TryGetValue("id", out var wi) && wi is string wid
                            && w.TryGetValue("skin", out var ws) && ws is string wskin && Characters().Any(c => c.id == wid)
                            && (wskin == "" || (Store.SkinChoices.TryGetValue(wid, out var wl) && wl.Any(x => x.id == wskin))))
                        { Wear(wid, wskin); return; }
                        if (imageErrors >= 5 || !m.TryGetValue("imageError", out var src)) return;
                        imageErrors++;
                        Store.Log($"立繪載入失敗: {src}");
                    }
                    catch { }
                };
                core.Navigate(Store.PageUrl("index.html"));
            }
            catch (Exception e)
            {
                Store.Log("WebView2 初始化失敗: " + e);
                MessageBox.Show("桌寵畫面啟動失敗：\n" + e.Message + "\n\n日誌：" + Path.Combine(Store.Data, "windows.log"), "CrossPet");
            }
        }

        readonly List<DateTime> crashes = new List<DateTime>();
        /// <summary>WebView2 崩潰後恢復：網頁程式掛了就重新載入；瀏覽器程式掛了控制元件就廢了，換新的環境和控制元件重來。
        /// 顯示卡程式掛了 WebView2 會自己重啟，不用管。10 分鐘內最多恢復 3 次，免得一直崩一直重來</summary>
        async Task RecoverWeb(CoreWebView2ProcessFailedKind kind)
        {
            Store.Log("WebView2 程式崩潰: " + kind);
            if (kind == CoreWebView2ProcessFailedKind.GpuProcessExited) return;
            crashes.RemoveAll(t => (DateTime.UtcNow - t).TotalMinutes > 10);
            if (crashes.Count >= 3) { Store.Log("10 分鐘內崩潰太多次，不再自動恢復"); return; }
            crashes.Add(DateTime.UtcNow);
            ++followEventGeneration;
            ready = false;
            try
            {
                if (kind != CoreWebView2ProcessFailedKind.BrowserProcessExited && web?.CoreWebView2 != null)
                {
                    web.CoreWebView2.Reload();
                    return;
                }
                var old = web;
                web = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.Transparent, IsHitTestVisible = false };
                grid.Children.Remove(old);
                grid.Children.Insert(0, web);
                try { old.Dispose(); } catch { }
                env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Store.Data, "webview2"));
                await InitWeb();
                Store.Log("WebView2 已恢復");
            }
            catch (Exception e) { Store.Log("WebView2 恢復失敗: " + e); }
        }

        /// <summary>兩個虛擬域名：程式自帶的網頁（安裝目錄\web，資料夾對映）、角色立繪（資料目錄\characters，程式自己讀檔案，
        /// 不能用資料夾對映，原因見 SpriteResources）。只在網頁的域名裡導航，外部網頁一律不開</summary>
        public static void Map(CoreWebView2 core)
        {
            core.SetVirtualHostNameToFolderMapping(Store.AppHost, Path.Combine(Store.Install, "web"), CoreWebView2HostResourceAccessKind.Allow);
            SpriteResources.Attach(core);
            core.NavigationStarting += (_, a) => { if (!a.Uri.StartsWith($"https://{Store.AppHost}/")) a.Cancel = true; };
            core.NewWindowRequested += (_, a) => a.Handled = true;
        }

        List<(string id, string name)> characters = new List<(string id, string name)>();   // 可切換的角色（不含彩蛋）
        public void LoadCharacters()
        {
            ++followEventGeneration;
            var m = Store.Manifest();
            ManifestJson = Store.Json.Serialize(m);
            var chars = (Dictionary<string, object>)m["characters"];
            characters = chars.Where(kv => !IsEgg(kv.Value))
                              .Select(kv => (kv.Key, (kv.Value as Dictionary<string, object>)?["name"] as string ?? kv.Key)).ToList();
            if (!characters.Any(c => c.id == current)) current = characters.FirstOrDefault().id ?? "claude";
        }
        static bool IsEgg(object info) => info is Dictionary<string, object> d && d.TryGetValue("egg", out var e) && e is bool b && b;
        List<(string id, string name)> Characters() => characters;

    }
}
