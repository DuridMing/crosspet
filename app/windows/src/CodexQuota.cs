using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CrossPet
{
    /// <summary>
    /// GPT（Codex）額度來源，和 macOS 版 CodexQuotaSource 同一套規則（docs/superpowers/specs/2026-10-12-codex-quota-design.md）：
    /// 起 `codex app-server` 問 account/rateLimits/read（Codex 用自己的登入查即時額度，不對話、不耗額度；CrossPet 不碰憑據）。
    /// Windows 這邊的 Codex 問不到（沒裝、沒登入）時，WSL 開著的話在 WSL 裡問。
    /// 不再讀會話記錄：Codex 回覆了也不一定往裡寫額度，讀出來的常常是舊數還看著像新的（1.3.3 測試裡實測）。
    /// 排版在網頁 codex-quota.js 裡做，這裡只交原始資料
    /// </summary>
    static class CodexQuota
    {
        static string cached;

        public static string FindBinary()
        {
            var o = Environment.GetEnvironmentVariable("CROSSPET_CODEX_BIN");
            if (!string.IsNullOrEmpty(o)) return File.Exists(o) ? o : null;
            if (cached != null && File.Exists(cached)) return cached;
            cached = null;
            // Codex 桌面版開著時，它自帶的命令列 codex.exe 在跑：用它（裝在受保護目錄裡也能拿到路徑），記下來下次直接用。
            // 桌面版的圖形介面程式可能也叫 Codex.exe（名字不分大小寫），起它會彈視窗：認出來跳過
            try
            {
                foreach (var p in Process.GetProcessesByName("codex"))
                    using (p)
                        try
                        {
                            var f = p.MainModule?.FileName;
                            if (f == null || !File.Exists(f) || !LooksLikeCli(f)) continue;
                            if (SavedBin() != f) OnUi(() => { Store.Settings["codexBin"] = f; Store.SaveSettings(); });
                            return cached = f;
                        }
                        catch { }
            }
            catch { }
            var saved = SavedBin();
            if (saved != null)
            {
                if (File.Exists(saved) && LooksLikeCli(saved)) return cached = saved;
                OnUi(() => { Store.Settings.Remove("codexBin"); Store.SaveSettings(); });
            }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                foreach (var name in new[] { "codex.exe", "codex.cmd" })
                    try { var f = Path.Combine(dir.Trim(), name); if (File.Exists(f)) return cached = f; } catch { }
            var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd");
            return File.Exists(npm) ? cached = npm : null;
        }

        // FindBinary 在後臺執行緒跑：設定只在介面執行緒改，這裡讀的時候出錯就當沒有
        static string SavedBin() { try { return Store.Settings.TryGetValue("codexBin", out var v) ? v as string : null; } catch { return null; } }
        static void OnUi(Action a) { try { System.Windows.Application.Current?.Dispatcher.BeginInvoke(a); } catch { } }

        /// 圖形介面程式（Electron 做的桌面版）旁邊有這些檔案；命令列 codex.exe 旁邊沒有
        static bool LooksLikeCli(string exe)
        {
            var dir = Path.GetDirectoryName(exe) ?? "";
            return !(File.Exists(Path.Combine(dir, "resources.pak")) || File.Exists(Path.Combine(dir, "ffmpeg.dll"))
                     || File.Exists(Path.Combine(dir, "icudtl.dat")) || File.Exists(Path.Combine(dir, "chrome_100_percent.pak")));
        }

        /// 起不來時調：程式可能被更新挪走了，下次重新找
        public static void Forget() { cached = null; }

        /// 結束整棵程式樹：npm 裝的 codex.cmd 是 cmd.exe → node.exe → codex.exe，.NET Framework 的 Kill 只殺最外面一層
        static void KillTree(Process p)
        {
            try
            {
                if (p.HasExited) return;
                using (var k = Process.Start(new ProcessStartInfo("taskkill.exe", $"/T /F /PID {p.Id}") { UseShellExecute = false, CreateNoWindow = true }))
                    k?.WaitForExit(3000);
            }
            catch { }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        public static Task<(Dictionary<string, object> rateLimits, string error, int ms)> QueryLive(string binary, int timeoutMs = 15000)
        {
            var cmd = binary.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            return Query(cmd ? "cmd.exe" : binary, cmd ? $"/d /s /c \"\"{binary}\" app-server\"" : "app-server", timeoutMs);
        }

        static async Task<(Dictionary<string, object> rateLimits, string error, int ms)> Query(string file, string args, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            var psi = new ProcessStartInfo
            {
                FileName = file, Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
            };
            Process p;
            try { p = Process.Start(psi); } catch (Exception e) { return (null, "起不來 Codex：" + e.Message, (int)sw.ElapsedMilliseconds); }
            try
            {
                p.ErrorDataReceived += (_, __) => { }; p.BeginErrorReadLine();   // 不讀 stderr 會把管道塞滿
                var stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                stdin.WriteLine("{\"id\":1,\"method\":\"initialize\",\"params\":{\"clientInfo\":{\"name\":\"crosspet\",\"version\":\"1\"}}}");
                stdin.WriteLine("{\"method\":\"initialized\"}");
                stdin.WriteLine("{\"id\":2,\"method\":\"account/rateLimits/read\"}");
                var read = Task.Run(() =>
                {
                    string line;
                    while ((line = p.StandardOutput.ReadLine()) != null)
                    {
                        Dictionary<string, object> msg;
                        try { msg = Store.Json.DeserializeObject(line) as Dictionary<string, object>; } catch { continue; }
                        if (msg == null || !(msg.TryGetValue("id", out var id) && Convert.ToString(id) == "2")) continue;
                        if (msg.TryGetValue("error", out var e) && e is Dictionary<string, object> ed)
                            return ((Dictionary<string, object>)null, ed.TryGetValue("message", out var m) ? m as string ?? "Codex 服務返回錯誤" : "Codex 服務返回錯誤");
                        var r = msg.TryGetValue("result", out var rv) ? rv as Dictionary<string, object> : null;
                        Dictionary<string, object> rl = null;
                        if (r != null && r.TryGetValue("rateLimitsByLimitId", out var by) && by is Dictionary<string, object> byd && byd.TryGetValue("codex", out var c)) rl = c as Dictionary<string, object>;
                        if (rl == null && r != null && r.TryGetValue("rateLimits", out var rr)) rl = rr as Dictionary<string, object>;
                        return (rl, rl == null ? "Codex 服務沒給額度" : null);
                    }
                    return ((Dictionary<string, object>)null, "Codex 服務提前退出");
                });
                var winner = await Task.WhenAny(read, Task.Delay(timeoutMs));
                if (winner != read) return (null, $"超時（{timeoutMs / 1000} 秒沒回）", (int)sw.ElapsedMilliseconds);
                var (rl2, err) = read.Result;
                return (rl2, err, (int)sw.ElapsedMilliseconds);
            }
            catch (Exception e) { return (null, "問 Codex 出錯：" + e.Message, (int)sw.ElapsedMilliseconds); }
            finally { KillTree(p); p.Dispose(); }
        }

        /// 給網頁的 payload（rateLimits 原樣，不含賬號標識——accountId 在 result 裡，不在 rateLimits 裡）
        public static Dictionary<string, object> Payload(string source, Dictionary<string, object> rateLimits, string error, int ms)
        {
            var d = new Dictionary<string, object> { ["source"] = source, ["ms"] = ms, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            if (rateLimits != null) d["rateLimits"] = rateLimits;
            if (error != null) d["error"] = error;
            return d;
        }

        /// 執行狀態裡的簡寫：5小時已用 3%，本週已用 40%
        public static string Brief(Dictionary<string, object> rl)
        {
            var parts = new List<string>();
            foreach (var k in new[] { "primary", "secondary" })
            {
                if (!(rl.TryGetValue(k, out var w) && w is Dictionary<string, object> win)) continue;
                var used = win.TryGetValue("usedPercent", out var u) ? u : null;
                var mins = win.TryGetValue("windowDurationMins", out var m) ? m : null;
                if (!IsNumber(used)) continue;
                var mm = IsNumber(mins) ? Convert.ToInt32(mins) : 0;
                parts.Add($"{(mm >= 10080 ? "本週" : mm >= 1440 ? "今日" : $"{Math.Max(1, mm / 60)}小時")}已用 {(int)Math.Round(Convert.ToDouble(used), MidpointRounding.AwayFromZero)}%");
            }
            return string.Join("，", parts);
        }

        // ---------------- WSL 裡的 Codex ----------------
        // 在 WSL 裡用 Codex 的人（含 Codex 桌面版「在 WSL 裡執行」），登入狀態在 WSL 那邊，Windows 這邊問不到：
        // 通過 wsl.exe 在 WSL 裡起 `codex app-server` 問。只在 WSL 已經開著時問（不把它拉起來，WSL 沒開也不可能正在用它）；
        // 在哪個發行版、codex 在哪，找一次快取 10 分鐘
        static (string distro, string codex) wsl;
        static DateTime wslCheckedAt = DateTime.MinValue;
        public static string WslNote = "";

        public static async Task<(Dictionary<string, object> rateLimits, string error, int ms)> QueryWsl(int timeoutMs = 15000)
        {
            var (distro, codex) = FindWslCodex();
            if (distro == null) return (null, null, 0);   // 沒有開著的 WSL / WSL 裡沒有 codex：不算出錯
            // bash -lic：登入 + 互動，npm / nvm 裝的 codex 才在 PATH 裡；.bashrc 往輸出裡打的東西會被當成非 JSON 跳過
            return await Query("wsl.exe", $"-d {distro} -e bash -lic \"exec '{codex}' app-server\"", timeoutMs);
        }

        static (string distro, string codex) FindWslCodex()
        {
            if (!(Running("vmmemWSL") || Running("vmmem"))) { WslNote = ""; return (null, null); }
            if ((DateTime.UtcNow - wslCheckedAt).TotalMinutes < 10) return wsl;
            wslCheckedAt = DateTime.UtcNow; wsl = (null, null); WslNote = "";
            foreach (var d in Run("wsl.exe", "-l --running -q", Encoding.Unicode).Replace("\0", "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0))
            {
                var path = Run("wsl.exe", $"-d {d} -e bash -lic \"command -v codex\"", Encoding.UTF8)
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).LastOrDefault(x => x.StartsWith("/"));
                if (path == null) continue;
                wsl = (d, path); WslNote = $"WSL {d}";
                break;
            }
            return wsl;
        }

        /// 跑一個小命令拿輸出（最多等 8 秒）
        static string Run(string file, string args, Encoding enc)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = enc };
                using (var p = Process.Start(psi))
                {
                    p.ErrorDataReceived += (_, __) => { }; p.BeginErrorReadLine();
                    var t = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit(8000)) KillTree(p);
                    return t.Wait(1000) ? t.Result : "";
                }
            }
            catch { return ""; }
        }

        static bool Running(string name)
        {
            var ps = Process.GetProcessesByName(name);
            foreach (var p in ps) p.Dispose();
            return ps.Length > 0;
        }

        static bool IsNumber(object v) => v is int || v is long || v is double || v is decimal;
    }
}
