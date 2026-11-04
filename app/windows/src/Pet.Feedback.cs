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
        // ---------------- 反饋問題 ----------------
        // 點選「哪方面」+ 必填一句「具體是哪裡」，附上版本、系統、接入情況、最近日誌，
        // 由頁面拼成卡片再送出。maid fork：拿掉上游作者的飛書端點與入口，預設不送到任何地方；要測試才在 settings.json 用 "feedbackURL" 指定
        const string FeedbackEndpoint = "";

        void OpenFeedback()
        {
            if (feedback == null) { feedback = new FeedbackWindow(this, env); feedback.Closed += () => feedback = null; }
            feedback.Show();
        }

        /// <summary>每臺機器一個隨機 id，只用來讓服務端限流、合併同一個人的多條反饋</summary>
        static string InstallId
        {
            get
            {
                if (Store.Settings.TryGetValue("installId", out var v) && v is string s && s != "") return s;
                var id = Guid.NewGuid().ToString();
                Store.Settings["installId"] = id; Store.SaveSettings();
                return id;
            }
        }

        /// <summary>去掉日誌裡的隱私：使用者目錄換成 ~，路徑裡的使用者名稱、像 API Key 的字串打碼，順便去掉終端顏色控制符</summary>
        static string Redact(string text)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var s = string.IsNullOrEmpty(home) ? text : text.Replace(home, "~");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\x1B\[[0-9;]*[A-Za-z]", "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(\\Users\\|/Users/)[^\\/\s]+", "$1<user>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, @"sk-[A-Za-z0-9_\-]{8,}", "sk-***");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(?i)(bearer|token|key|secret|password)([\s""':=]+)[A-Za-z0-9_\-\.]{8,}", "$1$2***");
            return s;
        }

        // ---- 記錄（反饋用，格式見 docs/架构.md「記錄」）----
        static string EventsDir => Path.Combine(Store.Data, "events");
        static string PetEventsFile => Path.Combine(EventsDir, "pet.jsonl");
        static string SwitchesFile => Path.Combine(Store.Data, "switches.jsonl");
        static void NotePet(string id, Dictionary<string, object> state, string result, string detail = "") =>
            Ledger.Append(PetEventsFile, new Dictionary<string, object> { ["char"] = id, ["ev"] = state != null && state.TryGetValue("event", out var e) ? e as string ?? "" : "",
                ["pose"] = state != null && state.TryGetValue("pose", out var p) ? p as string ?? "" : "", ["result"] = result, ["detail"] = detail });

        /// <summary>反饋視窗第一步就要的：開頭、第二步的選項（已接入的 AI、角色、開啟了的額度）；sections 是這一類要附的段落（見 web/feedback-plan.js）</summary>
        Dictionary<string, object> FeedbackFacts(Dictionary<string, string> sections, string key = null)
        {
            var skin = Store.Skins != null && current != null && Store.Skins.TryGetValue(current, out var w) ? w as string ?? "" : "";
            var quotas = new List<string>();
            if (Store.Flag("gptQuota")) quotas.Add("gpt");
            if (Store.Flag("agyQuota")) quotas.Add("gemini");
            if (DsQuotaEnabled) quotas.Add("deepseek");
            return new Dictionary<string, object>
            {
                ["header"] = new Dictionary<string, object> { ["version"] = Store.Version, ["platform"] = "windows",
                    ["os"] = $"{Environment.OSVersion.VersionString} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}",
                    ["install"] = InstallId, ["character"] = current ?? "", ["skin"] = skin, ["uptimeMin"] = (int)(DateTime.Now - launchedAt).TotalMinutes },
                ["integrations"] = integrated ?? new List<string>(),
                ["characters"] = Characters().Select(c => new Dictionary<string, object> { ["id"] = c.id, ["name"] = c.name }).ToList(),
                ["quotas"] = quotas,
                ["sections"] = sections.ToDictionary(kv => kv.Key, kv => (object)Redact(kv.Value)),
                ["key"] = key,   // 收集的結果帶回 collect 時的 key，頁面據此丟掉上一類遲到的結果
            };
        }
        void PushFacts(Dictionary<string, string> sections, string key = null) => feedback?.Run($"setFacts({Store.Json.Serialize(FeedbackFacts(sections, key))})");

        string lastUpdateCheck = "還沒查過", diagText = "";
        readonly DateTime launchedAt = DateTime.Now;
        /// <summary>收集反饋要的段落（want 來自 web/feedback-plan.js 的 sectionsFor）。網頁 2 秒不回就寫「網頁沒有回應」；接入診斷在後臺跑</summary>
        async Task<Dictionary<string, string>> CollectFeedback(List<string> want)
        {
            var o = new Dictionary<string, string>();
            string Read(string f) => Store.ReadText(f) ?? "";
            foreach (var id in want)
            {
                try
                {
                    switch (id)
                    {
                        case "window":
                            var rect = "";
                            if (hwnd != IntPtr.Zero && Native.GetWindowRect(hwnd, out var r))
                            {
                                var on = Forms.Screen.AllScreens.Any(sc => sc.WorkingArea.IntersectsWith(new System.Drawing.Rectangle(r.Left + 40, r.Top + 40, Math.Max(1, r.Right - r.Left - 80), Math.Max(1, r.Bottom - r.Top - 80))));
                                rect = $"位置 {r.Left},{r.Top} {r.Right - r.Left}×{r.Bottom - r.Top}{(on ? "" : "（不在任何螢幕裡）")}；";
                            }
                            o[id] = $"{rect}顯示中 {(Window?.IsVisible == true && !concealed ? "是" : "否")}，透明度 {Num(Math.Round(Window?.Opacity ?? 0, 2))}，置頂 {(Window?.Topmost == true ? "是" : "否")}；層級 {LayerMode}；貼邊 {dock ?? "無"}{(dock != null ? (dockOut ? "·跑出來了" : "·縮著") : "")}；大小 {(int)(Scale * 100)}%；網頁 {(ready ? "就緒" : "沒就緒")}";
                            break;
                        case "displays": o[id] = DisplaysSummary(); break;
                        case "dpi":
                            o[id] = DisplaysSummary() + $"\n系統縮放 {(Window != null ? Math.Round(VisualTreeHelper.GetDpi(Window).DpiScaleX * 100) : 0)}%，網頁縮放 {Num(Math.Round(web?.ZoomFactor ?? 0, 3))}{(Math.Abs(zoomFix - 1) > 0.001 ? $"（自動修正 ×{Num(Math.Round(zoomFix, 3))}）" : "")}，"
                                + $"畫布 {Math.Round(pageW)}×{Math.Round(pageH)}，人物大小 {(int)(Scale * 100)}%，文字 {(TextLinked ? "跟著" : (int)(TextScale * 100) + "%")}";
                            break;
                        case "front": o[id] = $"前臺 {Native.ForegroundExe()}，是桌面或工作列 {(Native.ForegroundIsShell() ? "是" : "否")}；被別的視窗擋住 {(hwnd != IntPtr.Zero && Native.Covered(hwnd) ? "是" : "否")}"; break;
                        case "hidden": o[id] = $"因全屏藏著 {(hiddenForFullscreen ? "是" : "否")}，快捷鍵藏著 {(userHidden ? "是" : "否")}，從選單退出的標記 {(File.Exists(UserQuitFlag) ? "有" : "無")}，鎖屏 {(sessionLocked ? "是" : "否")}"; break;
                        case "renderRestart": o[id] = File.Exists(Path.Combine(Store.Data, "render-restart.txt")) ? "記錄：" + Read(Path.Combine(Store.Data, "render-restart.txt")).Trim() : "沒有記錄"; break;
                        case "install":
                            var missing = InstallCheck.Missing(Store.Install);
                            string wv = "?"; try { wv = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
                            o[id] = $"裝在 {Store.Install}；缺少 {(missing.Count == 0 ? "無" : string.Join("、", missing))}；資料 {Store.Data}；隨包 Python {(File.Exists(Store.Python) ? "在" : "不在")}；WebView2 {wv}；檢查更新 {lastUpdateCheck}";
                            break;
                        case "log": o[id] = Ledger.Tail(Path.Combine(Store.Data, "windows.log"), 400).Replace("\r", ""); break;
                        case "updateLog": o[id] = Ledger.Tail(Path.Combine(Store.Data, "update.log"), 100).Replace("\r", ""); break;
                        case "seen": o[id] = Store.Json.Serialize(SeenKeys.ToDictionary(kv => kv.Key, kv => (object)(SeenAt(kv.Value) ?? 0))); break;
                        case "petEvents": o[id] = Ledger.Tail(PetEventsFile, 200); break;
                        case "switches": o[id] = Ledger.Tail(SwitchesFile, 200); break;
                        case "settings":
                            var skins = Store.Settings.TryGetValue("skins", out var sv) ? Store.Json.Serialize(sv) : "{}";
                            o[id] = $"跟隨前臺 {(Store.Flag("followApps") ? "開" : "關")}，跟隨事件 {(Store.Flag("followEvents") ? "開" : "關")}，AI 幹活自動出現 {(AIAutostart ? "開" : "關")}，衣服 {skins}，"
                                + $"峰谷換裝 {(PeakSkins.Count == 0 ? "無" : Store.Json.Serialize(PeakSkins))}，省電 {(PowerSave ? "開" : "關")}，彩蛋 {(EggsEnabled ? "開" : "關")}，額度合併 {(QuotaAggregate ? "開" : "關")}，額度樣式 {QuotaStyle}，重置提醒 {(CodexResets ? "開" : "關")}";
                            break;
                        case "characters": o[id] = CharacterFolders(); break;
                        case "quota:gpt": o[id] = $"最近一次 {lastGptQuota}\n額度檔案 {Read(Path.Combine(Store.State, "gpt-quota.json"))}"; break;
                        case "quota:gemini": o[id] = $"找服務 {AntigravityQuota.LastNote}\n額度檔案 {Read(Path.Combine(Store.State, "gemini-quota.json"))}"; break;
                        case "quota:deepseek": o[id] = $"外掛自檢 {Read(Path.Combine(Store.State, "deepseek-plugin.json"))}\n餘額檔案 {Read(Path.Combine(Store.State, "deepseek-quota.json"))}"; break;
                        case "perf":
                            var me = Process.GetCurrentProcess();
                            var webviews = Process.GetProcessesByName("msedgewebview2");
                            o[id] = $"CrossPet 程式：CPU 時間 {(int)me.TotalProcessorTime.TotalSeconds} 秒（執行 {(int)(DateTime.Now - launchedAt).TotalMinutes} 分鐘），記憶體 {me.WorkingSet64 / 1048576} MB；"
                                + $"網頁程式（所有程式的 WebView2 合計）{webviews.Length} 個、記憶體 {webviews.Sum(x => { try { return x.WorkingSet64; } catch { return 0L; } }) / 1048576} MB；省電 {(PowerSave ? "開" : "關")}；"
                                + $"GPT 額度 {(Store.Flag("gptQuota") ? "開" : "關")}，Gemini 額度 {(Store.Flag("agyQuota") ? "開" : "關")}，DeepSeek 餘額 {(DsQuotaEnabled ? "開" : "關")}";
                            break;
                        case "page":
                            var eval = Eval("JSON.stringify(diagnoseDisplay())");
                            var raw = await Task.WhenAny(eval, Task.Delay(2000)) == eval ? eval.Result : "null";
                            o[id] = Store.Json.DeserializeObject(raw) as string ?? "網頁沒有回應";
                            break;
                        case "diag":
                            // 診斷要跑 python，卡住的話整份反饋都等不來：最多等 3 秒，到點先寫「診斷超時」（跑完的結果留著下次用）
                            if (diagText == "" && File.Exists(Store.Python))
                            {
                                var run = Task.Run(() => { var t = RunIntegrate("diagnose").Item1.Trim(); diagText = t; return t; });
                                o[id] = await Task.WhenAny(run, Task.Delay(3000)) == run ? run.Result : "診斷超時（3 秒內沒跑完）";
                            }
                            else o[id] = diagText;
                            break;
                        default:
                            if (id.StartsWith("events:")) o[id] = Ledger.Tail(Path.Combine(EventsDir, id.Substring(7) + ".jsonl"), 200);
                            else if (id.StartsWith("state:"))
                            {
                                // state:<角色>|<宿主>：狀態、多會話按角色記；輪次、按模型換角色按宿主名記（見 web/feedback-plan.js）
                                var parts = id.Substring(6).Split('|');
                                var c = parts[0]; var host = parts.Length > 1 ? parts[1] : c;
                                o[id] = string.Join("\n", new[] { "state", "sessions", "turns", "host" }.Select(k => (k, t: Read(Path.Combine(Store.State, $"{(k == "turns" || k == "host" ? host : c)}-{k}.json")).Trim()))
                                    .Where(x => x.t != "").Select(x => $"{x.k}：{(x.t.Length > 800 ? x.t.Substring(0, 800) : x.t)}"));
                            }
                            break;
                    }
                }
                catch (Exception e) { o[id] = "收集出錯：" + e.Message; }
            }
            return o;
        }

        /// <summary>角色資料夾對照表：安裝目錄和資料目錄裡每個資料夾一行（json 能不能讀、有沒有待機圖、幾張立繪、載入了沒有）</summary>
        string CharacterFolders()
        {
            var loaded = new HashSet<string>(Characters().Select(c => c.id));
            var skinIds = new HashSet<string>(Store.SkinChoices.Values.SelectMany(l => l.Select(x => x.id)));
            var rows = new List<string>();
            foreach (var (place, dir) in new[] { ("安裝目錄", Path.Combine(Store.Install, "characters")), ("資料目錄", Store.Characters) })
            {
                if (!Directory.Exists(dir)) { rows.Add($"{place}：沒有 characters 資料夾"); continue; }
                foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(d);
                    var jsonOk = Store.ReadJson(Path.Combine(d, "character.json")) != null;
                    var sprites = Directory.GetFiles(d).Where(f => f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName).ToList();
                    var idle = sprites.Any(f => f.StartsWith("idle.") || f.StartsWith("default."));
                    var state = loaded.Contains(name) ? "已載入" : skinIds.Contains(name) ? "已載入（衣服）" : name.StartsWith("egg-") ? "彩蛋" : !jsonOk ? "沒載入：character.json 讀不出" : !idle ? "沒載入：缺待機圖" : "沒載入";
                    rows.Add($"{place} {name}：json {(jsonOk ? "能讀" : "讀不出")}，待機圖 {(idle ? "有" : "無")}，立繪 {sprites.Count} 張，{state}");
                }
            }
            return string.Join("\n", rows);
        }
        /// <summary>反饋附帶的顯示資訊：幾塊顯示器、各自大小和相對主屏的位置、桌寵在哪塊、顯示層級 / 全屏隱藏 / 貼邊</summary>
        string DisplaysSummary()
        {
            try
            {
                var all = Forms.Screen.AllScreens;
                var main = Forms.Screen.PrimaryScreen.Bounds;
                var mine = hwnd == IntPtr.Zero ? null : Forms.Screen.FromHandle(hwnd);
                var parts = all.Select(s =>
                {
                    var b = s.Bounds;
                    var where = s.Primary ? "主屏" : b.Left >= main.Right ? "右側" : b.Right <= main.Left ? "左側" : b.Bottom <= main.Top ? "上方" : b.Top >= main.Bottom ? "下方" : "重疊";
                    return $"{b.Width}×{b.Height} {where}{(mine != null && s.DeviceName == mine.DeviceName ? "（桌寵在這）" : "")}";
                });
                var flags = new List<string> { "層級 " + LayerMode, HideFullscreen ? "全屏隱藏" : "全屏不隱藏" };
                if (dock != null) flags.Add(dock == "left" ? "貼左邊" : "貼右邊");
                if (hiddenForFullscreen) flags.Add("此刻因全屏藏著");
                if (hwnd != IntPtr.Zero && Native.GetWindowRect(hwnd, out var wr))
                {
                    var onAny = all.Any(sc => sc.WorkingArea.IntersectsWith(new System.Drawing.Rectangle(wr.Left + 40, wr.Top + 40, Math.Max(1, wr.Right - wr.Left - 80), Math.Max(1, wr.Bottom - wr.Top - 80))));
                    flags.Add($"視窗 {wr.Left},{wr.Top} {wr.Right - wr.Left}×{wr.Bottom - wr.Top}{(onAny ? "" : "（不在任何螢幕裡）")}{(Window.IsVisible && !concealed ? "" : "（隱藏）")}");
                }
                return $"{all.Length} 塊：{string.Join("、", parts)}；{string.Join("，", flags)}";
            }
            catch { return ""; }
        }

        public void HandleFeedback(Dictionary<string, object> msg)
        {
            var cmd = msg.TryGetValue("cmd", out var c) ? c as string : null;
            if (cmd == "collect")   // 選好了型別（和補選）：按 web/feedback-plan.js 給的段落清單收集
            {
                var want = (msg.TryGetValue("want", out var wv) ? wv as object[] : null)?.OfType<string>().ToList() ?? new List<string>();
                var key = msg.TryGetValue("key", out var kv) ? kv as string : null;
                _ = Window.Dispatcher.InvokeAsync(async () =>
                {
                    try { PushFacts(await CollectFeedback(want), key); }
                    catch (Exception e) { Store.Log("收集反饋資訊出錯: " + e); }   // 不然異常進了沒人看的 Task，什麼痕跡都不留
                });
                return;
            }
            if (cmd != "send")   // get：反饋視窗剛開啟，先給開頭和第二步的選項
            {
                diagText = "";   // 每次開啟反饋都重新診斷一次（要用時再跑）
                PushFacts(new Dictionary<string, string>());
                if (integrated == null) RefreshIntegrated(() => PushFacts(new Dictionary<string, string>()));
                return;
            }
            var endpoint = Store.Settings.TryGetValue("feedbackURL", out var u) && u is string us && us != "" ? us : FeedbackEndpoint;
            void Done(bool ok, string text)
            {
                Window.Dispatcher.BeginInvoke(new Action(() =>
                {
                    feedback?.Run($"onSent({(ok ? "true" : "false")}, {Q(text)})");
                    if (ok) Js("show('happy', true); say('收到反饋啦，謝謝你！')");
                }));
            }
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var url)) { Done(false, "反饋暫時關閉了"); return; }
            var body = msg.TryGetValue("body", out var b) ? b as string : null;
            if (body == null || Encoding.UTF8.GetByteCount(body) > 20000) { Done(false, "內容太長了，刪短一點再發"); return; }
            // 本機限流：一小時最多 5 條（飛書群機器人的地址是公開的，不能讓一臺機器刷屏）
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var recent = ((Store.Settings.TryGetValue("feedbackTimes", out var ft) ? ft as object[] : null) ?? new object[0])
                .Select(x => { try { return (long)Convert.ToDouble(x, System.Globalization.CultureInfo.InvariantCulture); } catch { return 0L; } }).Where(x => now - x < 3600).ToList();
            if (recent.Count >= 5) { Done(false, "發得有點多啦，過一會兒再發"); return; }
            Task.Run(async () =>
            {
                try
                {
                    var res = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
                    // 飛書不論成敗都回 HTTP 200，看返回裡的 code：0 才是發到了群裡
                    var reply = Store.Json.DeserializeObject(await res.Content.ReadAsStringAsync()) as Dictionary<string, object>;
                    var code = reply != null && reply.TryGetValue("code", out var c2) ? Convert.ToInt32(c2) : -1;
                    if (code == 0)
                    {
                        await Window.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            recent.Add(now);
                            Store.Settings["feedbackTimes"] = recent.Cast<object>().ToArray(); Store.SaveSettings();
                        }));
                        Done(true, "");
                    }
                    else if (code == 11232) Done(false, "反饋的人有點多，過一會兒再發");
                    else { Store.Log("傳送反饋失敗: " + code); Done(false, $"沒發出去（{code}），稍後再試試"); }
                }
                catch (Exception e) { Store.Log("傳送反饋失敗: " + e.Message); Done(false, "連不上網路，稍後再試試"); }
            });
        }
    }
}
