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
        // ---------------- 和網頁打交道 ----------------
        public void Js(string code)
        {
            try { web?.CoreWebView2?.ExecuteScriptAsync(code); } catch (Exception e) { Store.Log("JS 出錯: " + e.Message); }
        }
        public async Task<string> Eval(string code)
        {
            try { return web?.CoreWebView2 == null ? "null" : await web.CoreWebView2.ExecuteScriptAsync(code); }
            catch { return "null"; }
        }
        public void Reload() { ++followEventGeneration; ready = false; web?.CoreWebView2?.Reload(); }
        public static string Q(string s) => Store.Json.Serialize(s);

        /// <summary>換衣服（皮膚）：記下來，重新載入立繪；正在顯示的就是她的話馬上換上</summary>
        void Wear(string id, string skin)
        {
            if (!(Store.Settings.TryGetValue("skins", out var sv) && sv is Dictionary<string, object> sd)) Store.Settings["skins"] = sd = new Dictionary<string, object>();
            if ((sd.TryGetValue(id, out var cur) ? cur as string ?? "" : "") == skin) return;
            if (skin == "") sd.Remove(id); else sd[id] = skin;
            Store.SaveSettings();
            LoadCharacters();
            Js($"init({ManifestJson}); refreshLook({Q(id)})");
            PushSettings();   // 設定頁開著（角色頁的衣服卡）時高亮跟著變：按時段自動換衣服也走這裡
        }

        /// <summary>why：為什麼換（front 跟隨前臺 / event 跟隨事件 / manual 選單或設定 / skin 換衣服），記進換角色記錄（反饋「角色換得不對」用）</summary>
        void SwitchTo(string id, string why, string via = "")
        {
            ++followEventGeneration;  // 同一個角色也算新的手動選擇，不能讓舊的非同步跟隨請求蓋回來。
            if (!Characters().Any(c => c.id == id)) return;
            var skin = Store.Skins != null && Store.Skins.TryGetValue(id, out var w) ? w as string ?? "" : "";
            Ledger.Append(SwitchesFile, new Dictionary<string, object> { ["from"] = current ?? "", ["to"] = id, ["skin"] = skin, ["why"] = why, ["via"] = via });
            if (id != current) Store.Log($"換角色 {current} → {id}（{why}{(via == "" ? "" : " " + via)}）");
            current = id;
            if (dock != null && !dockOut) After(800, ApplyDock);   // 換了角色，切口位置跟著變
            Store.Settings["character"] = id; Store.SaveSettings();
            Js($"setCharacter({Q(id)})");
            PushSettings();
        }

        /// <summary>讀 AI 寫來的狀態 / 額度檔案（state 目錄），有變化就交給網頁。和 macOS 版 pollStates 一樣</summary>
        void Poll()
        {
            if (!ready) return;
            // 前臺換成 / 離開桌面：Win+D「顯示桌面」不一定發前臺切換事件，這裡兜一下（見 ZOrderRule.Top）
            var shell = Native.ForegroundIsShell();
            if (shell != frontIsShell) { frontIsShell = shell; ApplyLayer(); }
            // 多模型宿主（DeepSeek Harness、WorkBuddy、ZCode，以及接了別家介面的 Claude Code、Codex）裡換了模型：
            // 它正在前臺的話馬上換成那個模型的角色
            if (HostRoutesChanged()) FollowForeground("host");
            var latestActive = (id: (string)null, ts: 0.0);
            // current：多模型宿主（WorkBuddy）裡沒有對應角色的模型，由當前角色來演；它只有狀態，沒有額度
            var known = Characters().Select(c => c.id).Concat(new[] { "current" }).ToList();
            foreach (var id in known.Concat(StateFiles.OtherHosts(Store.State, known)))   // maid fork：沒有對應角色的 AI 也交給網頁，寫了 reactTo 的角色才接得到
                foreach (var kind in new[] { "state", "quota", "sessions" })   // sessions：好幾個對話同時幹活時各自的狀態
                {
                    if (id == "current" && kind == "quota") continue;
                    if (id == "deepseek" && kind == "quota" && !DsQuotaEnabled) continue;
                    var file = Path.Combine(Store.State, $"{id}-{kind}.json");
                    FileInfo fi;
                    try { fi = new FileInfo(file); if (!fi.Exists || fi.Length > 1_000_000) continue; } catch { continue; }
                    var stamp = fi.LastWriteTimeUtc.Ticks + ":" + fi.Length;
                    var key = id + "-" + kind;
                    if (stamps.TryGetValue(key, out var old) && old == stamp) continue;
                    if (kind == "state" && (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalMinutes > 10) { stamps[key] = stamp; NotePet(id, null, "skipped", "stale"); continue; }  // 只認 10 分鐘內的
                    var value = Store.ReadJson(file);
                    if (value == null) continue;   // 可能正被另一邊寫一半，下次再讀
                    stamps[key] = stamp;
                    var json = Store.Json.Serialize(value);
                    if (kind == "sessions") { if (!Paused) Js($"setSessions({Q(id)}, {json})"); continue; }
                    dev?.Js($"logEvent({Q(id)}, {Q(kind)}, {json}, {(Paused ? "true" : "false")})");
                    if (Paused) { if (kind == "state") NotePet(id, value, "skipped", "paused"); continue; }
                    if (kind == "state")
                    {
                        NotePet(id, value, "played");   // 交給網頁了；網頁自己的判斷（不是當前角色、正在演彩蛋……）在 diagnoseDisplay 裡
                        // 在終端裡用 AI 時沒有對應的前臺程式：跟著最新一條工作事件換角色
                        var pose = value.TryGetValue("pose", out var p) ? p as string : null;
                        var ts = value.TryGetValue("ts", out var t) ? Convert.ToDouble(t) : 0;
                        lastPose[id] = (pose ?? "", DateTime.UtcNow);
                        if (id != "current" && pose != "idle" && pose != "sleeping" && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts < 30 && ts > latestActive.ts) latestActive = (id, ts);
                        Js($"applyCharState({Q(id)}, {json})");
                        // Codex 回完一輪：額度記錄剛寫進會話，馬上讀一次（不然要等每分鐘一次的定時重新整理）
                        if (id == "gpt")
                        {
                            if (value.TryGetValue("event", out var ev) && ev as string == "Stop") { codexBusyAt = null; After(1500, () => RefreshCodexQuota(CodexWhy.TurnEnd)); }
                            else codexBusyAt = DateTime.UtcNow;   // 在幹活：定時查額度改成 1 分鐘一次（CodexTickDue）
                        }
                    }
                    else Js($"setQuota({Q(id)}, {json})");
                }
            if (latestActive.id != null) _ = FollowEvent(latestActive.id);
            ApplyLayer();
            UpdateDock();
        }
    }
}
