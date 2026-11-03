using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CrossPet
{
    /// <summary>設定、反饋視窗預設開到內容基本放得下的高度（反饋的四步一頁排開、角色頁的卡片牆），螢幕矮就縮到放得下。和 macOS 的 fitHeight 一致</summary>
    static class WindowSize
    {
        public static double FitHeight(double h) => Math.Min(h, SystemParameters.WorkArea.Height - 40);
    }

    /// <summary>
    /// 開發者控制台（Shift + 右鍵開啟）：載入和 macOS 共用的 app/web/devconsole.html，
    /// 頁面通過 chrome.webview.postMessage 發命令，這裡轉給桌寵頁面執行，和 macOS 版 handleDev 一樣。
    /// </summary>
    sealed class DevConsole
    {
        readonly Pet pet;
        readonly Window window;
        readonly WebView2 web;
        public event Action Closed;

        public DevConsole(Pet pet, CoreWebView2Environment env)
        {
            this.pet = pet;
            web = new WebView2();
            window = new Window { Title = "CrossPet 開發者控制台", Width = 1060, Height = 760, Content = web, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            window.Closed += (_, __) => Closed?.Invoke();
            window.Loaded += async (_, __) =>
            {
                await web.EnsureCoreWebView2Async(env);
                Pet.Map(web.CoreWebView2);
                web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                web.CoreWebView2.WebMessageReceived += (_, e) => Handle(e.WebMessageAsJson);
                web.CoreWebView2.NavigationCompleted += (_, ___) =>
                {
                    var info = Store.Json.Serialize(new Dictionary<string, object> { ["version"] = Store.Version + " Windows", ["feedbackPath"] = Store.Feedback });
                    Js($"setAppInfo({info})");
                    Boot();
                };
                web.CoreWebView2.Navigate(Store.PageUrl("devconsole.html"));
            };
        }

        public void Show() { window.Show(); window.Activate(); }
        public void Js(string code) { try { web.CoreWebView2?.ExecuteScriptAsync(code); } catch { } }
        public void Boot() => Js($"boot({pet.ManifestJson})");

        async void Handle(string json)
        {
            if (!(Store.Json.DeserializeObject(json) is Dictionary<string, object> msg) || !(msg.TryGetValue("cmd", out var c) && c is string cmd)) return;
            switch (cmd)
            {
                case "pet":     // 在桌寵頁面裡執行一段程式碼（控制台自己的本地頁面發來的）
                    if (msg.TryGetValue("code", out var code) && code is string s) pet.Js(s);
                    break;
                case "status":  // ExecuteScriptAsync 返回的是 JSON 編碼後的結果：JSON.stringify 的字串再套一層引號
                    var raw = await pet.Eval("JSON.stringify(devStatus())");
                    if (raw != "null") Js($"onPetStatus(JSON.parse({raw}))");
                    break;
                case "pause":
                    pet.Paused = msg.TryGetValue("on", out var on) && on is bool b && b;
                    break;
                case "save":
                    var text = msg.TryGetValue("text", out var t) ? t as string ?? "" : "";
                    File.WriteAllText(Store.Feedback, text);
                    try { Clipboard.SetText(text); } catch { }
                    Js($"onSaved({Pet.Q(Store.Feedback)})");
                    break;
                case "reveal":
                    if (File.Exists(Store.Feedback)) Process.Start("explorer.exe", $"/select,\"{Store.Feedback}\"");
                    break;
                case "reload":
                    pet.Reload();
                    break;
            }
        }
    }

    /// <summary>
    /// 設定視窗（右鍵「設定…」）：載入和 macOS 共用的 app/web/settings.html，
    /// 頁面通過 chrome.webview.postMessage 發 {cmd, ...}，Pet.HandleSettings 改完設定後用 Push 把最新狀態推回頁面
    /// </summary>
    sealed class SettingsWindow
    {
        readonly Pet pet;
        readonly Window window;
        readonly WebView2 web;
        public event Action Closed;

        /// <summary>page：設定頁 settings.html，或者換角色視窗 characters.html（同一套狀態和命令，只是單獨一個大視窗）</summary>
        public SettingsWindow(Pet pet, CoreWebView2Environment env, string page = "settings.html", string title = "CrossPet 設定", double width = 560, double height = 780)
        {
            this.pet = pet;
            web = new WebView2();
            window = new Window { Title = title, Width = width, Height = WindowSize.FitHeight(height), MinWidth = 420, Content = web, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            window.Closed += (_, __) => Closed?.Invoke();
            window.Loaded += async (_, __) =>
            {
                await web.EnsureCoreWebView2Async(env);
                Pet.Map(web.CoreWebView2);
                web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                web.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    if (Store.Json.DeserializeObject(e.WebMessageAsJson) is Dictionary<string, object> msg)
                        try { pet.HandleSettings(msg); } catch (Exception ex) { Store.Log("設定出錯: " + ex); }
                };
                web.CoreWebView2.NavigationCompleted += (_, __) =>
                {
                    loaded = true;
                    if (pendingTab != null) { var t = pendingTab; pendingTab = null; ShowTab(t); }
                };
                web.CoreWebView2.Navigate(Store.PageUrl(page));
            };
        }

        bool loaded; string pendingTab;
        /// <summary>切到某個標籤頁（「新功能」卡片的「去看看」）；頁面還沒載入完就等載入完再切</summary>
        public void ShowTab(string tab)
        {
            if (!loaded || web.CoreWebView2 == null) { pendingTab = tab; return; }
            try { web.CoreWebView2.ExecuteScriptAsync($"showTab({Store.Json.Serialize(tab)})"); } catch { }
        }

        public void Show() { window.Show(); window.Activate(); }
        public Size Size => new Size(window.ActualWidth, window.ActualHeight);
        public void Push()
        {
            try { web.CoreWebView2?.ExecuteScriptAsync($"setState({Store.Json.Serialize(pet.SettingsState())})"); } catch { }
        }
    }

    /// <summary>
    /// 反饋視窗（右鍵「反饋問題…」）：載入和 macOS 共用的 app/web/feedback.html，
    /// 頁面發 {cmd: get / send}，Pet.HandleFeedback 處理；Run 在頁面裡執行一段程式碼（setInfo / onSent）
    /// </summary>
    sealed class FeedbackWindow
    {
        readonly WebView2 web;
        readonly Window window;
        public event Action Closed;

        public FeedbackWindow(Pet pet, CoreWebView2Environment env)
        {
            web = new WebView2();
            window = new Window { Title = "CrossPet 反饋問題", Width = 560, Height = WindowSize.FitHeight(700), Content = web, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            window.Closed += (_, __) => Closed?.Invoke();
            window.Loaded += async (_, __) =>
            {
                await web.EnsureCoreWebView2Async(env);
                Pet.Map(web.CoreWebView2);
                web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                web.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    if (Store.Json.DeserializeObject(e.WebMessageAsJson) is Dictionary<string, object> msg)
                        try { pet.HandleFeedback(msg); } catch (Exception ex) { Store.Log("反饋出錯: " + ex); }
                };
                web.CoreWebView2.Navigate(Store.PageUrl("feedback.html"));
            };
        }

        public void Show() { window.Show(); window.Activate(); }
        public void Run(string code) { try { web.CoreWebView2?.ExecuteScriptAsync(code); } catch { } }
    }
}
