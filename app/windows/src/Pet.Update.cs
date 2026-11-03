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
        // ---------------- 檢查更新：每 3 小時和電腦醒來時，只讀 GitHub 上公開的版本號，不傳送任何資料 ----------------
        static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        static readonly HttpClient download = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        async Task CheckForUpdate(bool manual = false)
        {
            // maid fork：預設不自動檢查更新，只在設定裡手動按「檢查更新」時查；要恢復自動檢查：settings.json 加 "autoUpdateCheck": true
            if (!manual && !Store.Flag("autoUpdateCheck")) return;
            try
            {
                Dictionary<string, object> body = null;
                string tag = null;
                var fromPage = false;
                // 裝的是測試版：也收下一個測試版（releases/latest 不含測試版）。看釋出頁訂閱源，挑比現在新、Windows 安裝包真的在的最新一個
                if (Store.Version.Contains("-")) { tag = await NewerFromFeed(); fromPage = tag != null; }
                if (tag == null)
                {
                var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
                req.Headers.UserAgent.ParseAdd("CrossPet/" + Store.Version);
                var res = await http.SendAsync(req);
                try { body = Store.Json.DeserializeObject(await res.Content.ReadAsStringAsync()) as Dictionary<string, object>; } catch { }
                tag = body != null && body.TryGetValue("tag_name", out var t) ? t as string : null;
                if (tag == null)
                {
                    // 介面限流（同一個網路每小時 60 次）或者返回了報錯：改看網頁 releases/latest 跳轉到哪個版本（不限流，get.sh 也這麼查）
                    Store.Log($"檢查更新：介面沒給版本號（HTTP {(int)res.StatusCode}），改查網頁");
                    var page = await http.GetAsync($"https://github.com/{Repo}/releases/latest");
                    var final = page.RequestMessage?.RequestUri?.AbsolutePath ?? "";
                    var i = final.LastIndexOf("/tag/", StringComparison.Ordinal);
                    if (i >= 0) { tag = Uri.UnescapeDataString(final.Substring(i + 5)); fromPage = true; }
                }
                }
                if (tag != null && Newer(tag, Store.Version))
                {
                    var url = body != null && body.TryGetValue("html_url", out var u) && u is string s && s.StartsWith("https://github.com/") ? s : $"https://github.com/{Repo}/releases/tag/{tag}";
                    var first = latestRelease?.tag != tag;
                    latestRelease = (tag, url);
                    lastUpdateCheck = $"{DateTime.Now:MM-dd HH:mm} 最新 {tag}";
                    updateZip = null;
                    if (fromPage) updateZip = $"https://github.com/{Repo}/releases/download/{tag}/CrossPet-Windows.zip";   // 包名固定（一鍵更新按名字找）
                    else if (body.TryGetValue("assets", out var a) && a is object[] assets)
                        foreach (var asset in assets.OfType<Dictionary<string, object>>())
                            if (asset.TryGetValue("name", out var n) && n as string == "CrossPet-Windows.zip" &&
                                asset.TryGetValue("browser_download_url", out var d) && d is string dl &&
                                dl.StartsWith($"https://github.com/{Repo}/releases/download/{tag}/")) updateZip = dl;
                    // 手動檢查時也讓她在氣泡裡說、設定裡出現「更新」按鈕（和 macOS 一樣），不跳到網頁；
                    // 只有新版本沒有 Windows 安裝包、沒法一鍵更新時才打開下載頁
                    if (first || manual) Js($"notifyUpdate({Q(tag)})");
                    PushSettings();
                    if (manual && updateZip == null) Open(url);
                }
                else
                {
                    lastUpdateCheck = $"{DateTime.Now:MM-dd HH:mm} " + (tag == null ? "沒拿到版本號" : $"已是最新（當前 {Store.Version}，釋出頁最新正式版 {tag}）");
                    if (manual) MessageBox.Show(tag == null ? "暫時連不上 GitHub，稍後再試。" : $"已經是最新版本（{Store.Version}）。", "CrossPet");
                }
            }
            catch (Exception e)
            {
                Store.Log("檢查更新失敗: " + e.Message);
                lastUpdateCheck = $"{DateTime.Now:MM-dd HH:mm} 失敗：{e.Message}";
                if (manual) MessageBox.Show("檢查更新失敗：" + e.Message, "CrossPet");
            }
        }
        /// <summary>
        /// 一鍵更新：下載新版安裝包 → 解壓到臨時資料夾 → 執行新包裡的 install.ps1（覆蓋安裝、按新版重新整理已接入的 AI、重新開啟）。
        /// 設定、角色、接入都在資料目錄裡，不受影響。失敗了就開啟下載頁，舊版照常能用
        /// </summary>
        async Task SelfUpdate()
        {
            if (updating || !(latestRelease is var (tag, page)) || updateZip == null) return;
            if (MessageBox.Show($"更新到 {tag}？\n\n會自動下載（約 20 MB）、安裝並重新開啟，設定、角色和 AI 接入都保留。",
                    "CrossPet 更新", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            updating = true;
            try
            {
                Js($"setUpdateProgress(0, {Q("正在下載新版本…")})");
                var dir = Path.Combine(Path.GetTempPath(), "CrossPet-update", tag);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                var zip = Path.Combine(dir, "CrossPet-Windows.zip");
                using (var res = await download.GetAsync(updateZip, HttpCompletionOption.ResponseHeadersRead))
                {
                    res.EnsureSuccessStatusCode();
                    // 邊下邊在氣泡裡報進度（GitHub 在有些網路下很慢，沒有進度看起來像卡住了）
                    var total = res.Content.Headers.ContentLength ?? 0;
                    using (var src = await res.Content.ReadAsStreamAsync())
                    using (var f = File.Create(zip))
                    {
                        var buf = new byte[81920]; long got = 0; int read, shown = -1;
                        while ((read = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                        {
                            await f.WriteAsync(buf, 0, read);
                            got += read;
                            var pct = total > 0 ? (int)(got * 100 / total) : -1;
                            if (pct >= 0 && pct != shown && (pct - shown >= 2 || pct == 100)) { shown = pct; Js($"setUpdateProgress({pct}, {Q(pct >= 100 ? "下載好了，正在安裝…" : $"正在下載新版本 {pct}%…")})"); }
                        }
                    }
                }
                Js($"setUpdateProgress(100, {Q("下載好了，正在安裝…")})");
                await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir));
                var root = Path.Combine(dir, "CrossPet");
                var script = Path.Combine(root, "install.ps1");
                var version = Store.ReadText(Path.Combine(root, "VERSION"))?.Trim();
                if (!File.Exists(Path.Combine(root, "CrossPet.exe")) || !File.Exists(script) || version != tag.TrimStart('v', 'V'))
                    throw new Exception("下載的安裝包不完整");
                Store.Log($"更新：{Store.Version} → {version}");
                Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" -Update")
                    { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root });
                Quit();   // install.ps1 等這邊退出後再覆蓋檔案
            }
            catch (Exception e)
            {
                updating = false;
                Store.Log("一鍵更新失敗: " + e);
                Js("endUpdate()");
                if (MessageBox.Show("自動更新沒成功：" + e.Message + "\n\n要開啟下載頁手動更新嗎？", "CrossPet 更新",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) Open(page);
            }
        }

        /// <summary>語義化版本比較：先比 主.次.補丁；一樣時正式版比測試版新（1.2.4 &lt; 1.2.5-beta.1 &lt; 1.2.5-beta.2 &lt; 1.2.5）</summary>
        /// <summary>釋出頁訂閱源（releases.atom，含測試版、不限流）裡比現在新、Windows 安裝包在的最新版本；沒有返回 null。
        /// 測試版釋出頁刪了、標籤還留著的，訂閱源裡也有，所以要看安裝包在不在</summary>
        async Task<string> NewerFromFeed()
        {
            try
            {
                var text = await http.GetStringAsync($"https://github.com/{Repo}/releases.atom");
                var tags = System.Text.RegularExpressions.Regex.Matches(text, "/releases/tag/([^\"<]+)").Cast<System.Text.RegularExpressions.Match>()
                    .Select(m => Uri.UnescapeDataString(m.Groups[1].Value)).Distinct().Where(t => Newer(t, Store.Version)).ToList();
                tags.Sort((x, y) => Newer(x, y) ? -1 : Newer(y, x) ? 1 : 0);
                foreach (var t in tags)
                {
                    var head = new HttpRequestMessage(HttpMethod.Head, $"https://github.com/{Repo}/releases/download/{t}/CrossPet-Windows.zip");
                    var r = await http.SendAsync(head);
                    if (r.IsSuccessStatusCode) return t;
                }
            }
            catch (Exception e) { Store.Log("檢查測試版更新失敗: " + e.Message); }
            return null;
        }

        public static bool Newer(string tag, string cur)
        {
            (int[] core, string[] pre) P(string v)
            {
                var parts = v.Trim().TrimStart('v', 'V').Split(new[] { '-' }, 2);
                return (parts[0].Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray(),
                        parts.Length > 1 ? parts[1].Split('.') : new string[0]);
            }
            var a = P(tag); var b = P(cur);
            for (int i = 0; i < Math.Max(3, Math.Max(a.core.Length, b.core.Length)); i++)
            {
                var x = i < a.core.Length ? a.core[i] : 0; var y = i < b.core.Length ? b.core[i] : 0;
                if (x != y) return x > y;
            }
            if (a.pre.Length == 0 || b.pre.Length == 0) return a.pre.Length == 0 && b.pre.Length > 0;   // 正式版 > 同號測試版
            for (int i = 0; i < Math.Max(a.pre.Length, b.pre.Length); i++)
            {
                if (i >= a.pre.Length) return false;
                if (i >= b.pre.Length) return true;
                string x = a.pre[i], y = b.pre[i];
                if (x == y) continue;
                bool xn = int.TryParse(x, out var nx), yn = int.TryParse(y, out var ny);
                if (xn && yn) return nx > ny;
                if (xn) return false;   // 數字段比文欄位小（semver 規則）
                if (yn) return true;
                return string.CompareOrdinal(x, y) > 0;
            }
            return false;
        }
    }
}
