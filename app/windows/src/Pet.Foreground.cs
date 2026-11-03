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
        long followEventGeneration;

        /// <summary>why：記進換角色記錄的原因（front 切了前臺 / host 前臺程式換了模型 / startup 剛啟動），和 macOS 一致</summary>
        void FollowForeground(string why = "front")
        {
            var exe = Native.ForegroundExe();
            var map = Store.ReadJson(Store.AppMap);
            var match = exe == "" ? null : map?.FirstOrDefault(kv => string.Equals(kv.Key, exe, StringComparison.OrdinalIgnoreCase)).Value as string;
            frontIsAI = !string.IsNullOrWhiteSpace(match);
            frontIsShell = Native.ForegroundIsShell();
            ApplyLayer();
            if (!ready || Paused || !Store.Flag("followApps") || exe == "") return;
            if (string.IsNullOrWhiteSpace(match)) return;   // 對照表裡沒有，或者值留空：這個程式不跟隨
            match = HostCharacter(match);
            if (match != current) SwitchTo(match, why, exe);
        }

        async Task FollowEvent(string id)
        {
            // 即使新事件屬於當前角色，也必須退休還在等網頁答覆的舊選擇。
            var generation = ++followEventGeneration;
            if (!ready || Paused || !Store.Flag("followEvents") || id == current) return;
            var exe = Native.ForegroundExe();
            var front = ForegroundCharacter(exe);
            var currentAt = current;
            var apps = Store.Flag("followApps");
            var events = Store.Flag("followEvents");
            var input = Store.Json.Serialize(new { eventId = id, foregroundId = front, followApps = apps, followEvents = events, characters = Characters().Select(c => c.id).ToArray() });
            var allowed = await Eval($"FollowPolicy.eventTarget({input}) === {Q(id)}");
            // 網頁答覆前可能切了視窗、手動換人或關了跟隨；舊請求不能覆蓋新選擇。
            if (generation != followEventGeneration || !ready || Paused || current != currentAt || Native.ForegroundExe() != exe
                || Store.Flag("followApps") != apps || Store.Flag("followEvents") != events || ForegroundCharacter(exe) != front) { NotePet(id, null, "skipped", "answer-stale"); return; }
            if (allowed != "true") { NotePet(id, null, "skipped", "front-ai"); return; }
            if (id != current) SwitchTo(id, "event");
        }

        string ForegroundCharacter(string exe)
        {
            var map = Store.ReadJson(Store.AppMap);
            var host = exe == "" ? null : map?.FirstOrDefault(kv => string.Equals(kv.Key, exe, StringComparison.OrdinalIgnoreCase)).Value as string;
            return string.IsNullOrWhiteSpace(host) ? null : HostCharacter(host);
        }

        // 首次寫路由也算變化：Cursor 前臺時橋的第一條角色記錄不能等切窗口才生效。
        bool HostRoutesChanged()
        {
            var changed = false;
            foreach (var host in new[] { "deepseek", "workbuddy", "zcode", "claude", "gpt", "qwen", "cursor" })
            {
                try
                {
                    var key = host + "-host";
                    var fi = new FileInfo(Path.Combine(Store.State, host + "-host.json"));
                    if (!fi.Exists) { if (stamps.Remove(key)) changed = true; continue; }
                    var stamp = fi.LastWriteTimeUtc.Ticks + ":" + fi.Length;
                    if (stamps.TryGetValue(key, out var old) && old == stamp) continue;
                    stamps[key] = stamp;
                    changed = true;
                }
                catch { }
            }
            return changed;
        }

        /// <summary>
        /// 一個程式裡能用好幾家模型（比如 DeepSeek Harness）：它的接入外掛把當前角色寫在 &lt;角色&gt;-host.json，
        /// 切到這個程式時換成那個角色。一天內寫的才算，角色不存在就還用對照表裡的
        /// </summary>
        static readonly Dictionary<string, string> HostDefaults = new Dictionary<string, string> { ["zcode"] = "glm", ["qwen"] = "qwen" };
        string HostCharacter(string id)
        {
            var file = Path.Combine(Store.State, id + "-host.json");
            try
            {
                // 還不知道它在用哪個模型（剛裝好、還沒對話過）：先按它預設的模型換，比如 ZCode 預設是 GLM
                if (!File.Exists(file) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(file)).TotalHours > 24)
                    return HostDefaults.TryGetValue(id, out var d) && Characters().Any(x => x.id == d) ? d : id;
            }
            catch { return id; }
            var host = Store.ReadJson(file);
            var c = host != null && host.TryGetValue("character", out var v) ? v as string : null;
            return c != null && Characters().Any(x => x.id == c) ? c : id;   // 認不出的模型（寫的 null）：不換人
        }
    }
}
