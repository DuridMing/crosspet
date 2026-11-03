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

        void RefreshQuota()
        {
            if (!ready) return;
            if (Store.Flag("agyQuota")) _ = RefreshGeminiQuota();
        }

        // ---- GPT 額度：什麼時候拿、從哪拿（來源見 CodexQuota.cs，規則見 docs/superpowers/specs/2026-10-12-codex-quota-design.md）----
        enum CodexWhy { Now, TurnEnd, Tick }
        DateTime codexLiveAt = DateTime.MinValue;       // 上次問服務的時間（不管成沒成）
        DateTime? codexBusyAt;                           // Codex 最近一次幹活的事件（不是 Stop）；Stop 後清掉。幹活時額度查得勤一點
        bool codexInFlight, sessionLocked;
        Dictionary<string, object> codexLast; DateTime codexLastAt;   // 最後一次問到的額度：問不到時接著顯示它，並標上時間
        string codexNote = "";                           // 從沒問到過時的原因，設定裡那個開關下面顯示
        string lastGptQuota = "還沒讀過", lastGptLogKey;

        /// 開啟額度顯示 / 頁面就緒：馬上問；Codex 回完一輪：隔 20 秒以上才問；定時（每分鐘）：見 CodexTickDue。藏著、鎖屏時定時的不查
        /// （和 macOS 版 App+Quota.swift 一致）
        /// <summary>定時到點時要不要問：Codex 在幹活（10 分鐘內有不是 Stop 的事件，長命令中途可能好幾分鐘沒事件）1 分鐘一次，
        /// 閒著 2 分鐘一次。實測一次約 0.15 秒 CPU（macOS），每分鐘一次平均約 0.25% 一個核心。定時器每 60 秒一跳有一兩秒誤差，門檻各留 5 秒。
        /// 和 macOS 的 App.codexTickDue 一樣（那邊有測試 tests/codex_quota_probe.sh）</summary>
        static bool CodexTickDue(double since, double? busyAgo) => since >= (busyAgo.HasValue && busyAgo.Value < 600 ? 55 : 115);

        async void RefreshCodexQuota(CodexWhy why)
        {
            if (!ready || !Store.Flag("gptQuota") || codexInFlight) return;
            if (why == CodexWhy.Tick && (userHidden || sessionLocked)) return;
            var since = (DateTime.UtcNow - codexLiveAt).TotalSeconds;
            var ask = why == CodexWhy.Now || (why == CodexWhy.TurnEnd && since >= 20) || (why == CodexWhy.Tick && CodexTickDue(since, codexBusyAt.HasValue ? (DateTime.UtcNow - codexBusyAt.Value).TotalSeconds : (double?)null));
            if (!ask) return;
            // 找程式（列舉程式）、問服務（可能要經過 WSL）都放到後臺，別卡住桌寵；同一時間只跑一個
            codexInFlight = true; codexLiveAt = DateTime.UtcNow;
            try
            {
                var bin = await Task.Run(() => CodexQuota.FindBinary());
                (Dictionary<string, object> rl, string err, int ms) r = (null, "沒找到 codex 程式", 0);
                var via = "";
                if (bin != null)
                {
                    r = await Task.Run(() => CodexQuota.QueryLive(bin));
                    if (r.err != null && r.err.StartsWith("起不來")) CodexQuota.Forget();   // 程式可能被更新挪走了，下次重新找
                }
                if (r.rl == null)
                {
                    // Windows 這邊問不到（沒裝、沒登入）：WSL 開著的話在 WSL 裡問
                    var w = await Task.Run(() => CodexQuota.QueryWsl());
                    if (w.rateLimits != null || w.error != null) { r = (w.rateLimits, w.rateLimits != null ? null : $"{r.err}；{CodexQuota.WslNote}：{w.error}", w.ms); via = CodexQuota.WslNote; }
                }
                ApplyCodex(r.rl, r.err, r.ms, via);
            }
            catch (Exception e) { Store.Log("GPT 額度: " + e.Message); }
            finally { codexInFlight = false; }
        }

        /// 交給網頁：問到了就是 live；沒問到、但以前問到過，就接著顯示最後一次的數（stale，網頁標上是幾點的資料）；從沒問到過就不顯示
        void ApplyCodex(Dictionary<string, object> rl, string error, int ms, string via)
        {
            if (!Store.Flag("gptQuota")) return;   // 查的途中關掉了額度顯示
            if (rl != null) { codexLast = rl; codexLastAt = DateTime.Now; codexNote = ""; }
            else if (codexLast == null) codexNote = "問不到 Codex：" + (error ?? "原因不明");
            var tag = rl != null ? $"Codex 服務{(via != "" ? "（" + via + "）" : "")}，{ms / 1000.0:0.0} 秒" : "沒問到：" + (error ?? "原因不明");
            lastGptQuota = $"{DateTime.Now:HH:mm:ss} （{tag}）" + (codexLast != null ? (rl == null ? $"沿用 {codexLastAt:HH:mm} 的數：" : "") + CodexQuota.Brief(codexLast) : "");
            // 來源或出錯原因變了才記日誌（每 5 分鐘查一次，照記會刷屏）：反饋附帶的日誌裡能看出額度從哪來、為什麼沒問到
            var logKey = (rl != null) + "|" + via + "|" + error;
            if (logKey != lastGptLogKey) { lastGptLogKey = logKey; Store.Log("GPT 額度：" + lastGptQuota.Substring(9)); }
            PushSettings();
            if (codexLast == null) { Js("setCodexQuota(null)"); return; }
            var p = CodexQuota.Payload(rl != null ? "live" : "stale", codexLast, error, ms);
            p["at"] = new DateTimeOffset(codexLastAt).ToUnixTimeSeconds();
            Js($"setCodexQuota({Store.Json.Serialize(p)})");
        }
        bool askingGemini;
        async Task RefreshGeminiQuota()
        {
            if (askingGemini) return;
            askingGemini = true;
            try
            {
                var q = await Task.Run(AntigravityQuota.Read);   // 程式查詢比較慢，放到後臺
                Js($"setQuota('gemini', {(q == null ? "null" : Store.Json.Serialize(q))})");   // Antigravity 沒開就不顯示
            }
            catch (Exception e) { Store.Log("Gemini 額度: " + e.Message); }
            finally { askingGemini = false; }
        }
    }
}
