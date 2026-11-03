using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace CrossPet
{
    /// <summary>
    /// Gemini 額度（可選，預設關）：和 macOS 版一樣，向本機正在執行的 Antigravity 後臺服務（language_server）問一次額度——
    /// Antigravity 自己的介面就是這樣拿額度的。令牌從它的啟動引數裡取（每次啟動隨機生成、只在本機有效，不是賬號憑據），
    /// 只在記憶體裡用，只發給 127.0.0.1；只信任本機的自簽名證書。Antigravity 沒開就不顯示。
    /// </summary>
    static partial class AntigravityQuota
    {
        static readonly HttpClient http = new HttpClient(new HttpClientHandler
        {
            // 只信任本機 127.0.0.1 的自簽名證書（Antigravity 後臺服務用的是自簽名 HTTPS）
            ServerCertificateCustomValidationCallback = (req, cert, chain, errors) => req.RequestUri.Host == "127.0.0.1",
        }) { Timeout = TimeSpan.FromSeconds(5) };
        static int cachedPid, cachedPort;
        /// <summary>最近一次找服務的結果（反饋「額度不對」用）</summary>
        public static string LastNote = "還沒查過";

        public static async Task<Dictionary<string, object>> Read()
        {
            var server = FindServer();
            if (server == null) { LastNote = $"{DateTime.Now:MM-dd HH:mm} 沒找到 Antigravity 的服務程式"; return null; }
            var (pid, token) = server.Value;
            var ports = cachedPid == pid ? new List<int> { cachedPort } : ListeningPorts(pid);
            LastNote = $"{DateTime.Now:MM-dd HH:mm} 找到服務程式，監聽埠 {ports.Count} 個";
            // 每個埠先問「額度彙總」（和 Antigravity 的「View Usage」同一份資料，剛對話完就更新），舊版沒有這個介面時退回「使用者狀態」
            // （裡面的額度是快取，會慢幾分鐘；2026-10-14 實測 View Usage 已經 98%，使用者狀態裡還是 100%）。和 macOS 版 askAntigravity 一致
            foreach (var port in ports)
            foreach (var method in new[] { "RetrieveUserQuotaSummary", "GetUserStatus" })
            {
                try
                {
                    var req = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/{method}")
                    {
                        Content = new StringContent("{\"metadata\":{\"ideName\":\"antigravity\",\"extensionName\":\"antigravity\",\"locale\":\"en\"}}", Encoding.UTF8, "application/json"),
                    };
                    req.Headers.Add("X-Codeium-Csrf-Token", token);
                    var res = await http.SendAsync(req);
                    if (res.StatusCode != HttpStatusCode.OK) continue;
                    var q = Parse(Store.Json.DeserializeObject(await res.Content.ReadAsStringAsync()));
                    if (q == null) continue;
                    cachedPid = pid; cachedPort = port;
                    return q;
                }
                catch { }
            }
            cachedPid = 0;
            return null;
        }

        /// <summary>找到 Antigravity 的 language_server 程式，返回 (pid, 令牌)</summary>
        static (int, string)? FindServer()
        {
            try
            {
                using (var search = new ManagementObjectSearcher("SELECT ProcessId, CommandLine, ExecutablePath FROM Win32_Process WHERE Name LIKE 'language_server%'"))
                    foreach (ManagementObject p in search.Get())
                    {
                        var path = p["ExecutablePath"] as string ?? "";
                        var cmd = p["CommandLine"] as string ?? "";
                        if (path.IndexOf("antigravity", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var token = Arg(cmd, "--csrf_token");
                        if (token != null) return (Convert.ToInt32(p["ProcessId"]), token);
                    }
            }
            catch (Exception e) { Store.Log("查詢 Antigravity 後臺服務失敗: " + e.Message); }
            return null;
        }

        static string Arg(string cmd, string name)
        {
            var parts = cmd.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim('"')).ToArray();
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].StartsWith(name + "=")) return parts[i].Substring(name.Length + 1);
                if (parts[i] == name && i + 1 < parts.Length) return parts[i + 1];
            }
            return null;
        }

        // ---- 某個程式在 127.0.0.1 上監聽的埠（系統的 TCP 連線表）----
        [DllImport("iphlpapi.dll")]
        static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int af, int tableClass, uint reserved);
        const int AF_INET = 2, TCP_TABLE_OWNER_PID_LISTENER = 3;

        static List<int> ListeningPorts(int pid)
        {
            var ports = new List<int>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
            var buf = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0) return ports;
                var count = Marshal.ReadInt32(buf);
                for (int i = 0; i < count; i++)
                {
                    // MIB_TCPROW_OWNER_PID：state, localAddr, localPort, remoteAddr, remotePort, owningPid，各 4 位元組
                    var row = buf + 4 + i * 24;
                    var addr = (uint)Marshal.ReadInt32(row + 4);
                    var port = ((Marshal.ReadByte(row + 8) << 8) | Marshal.ReadByte(row + 9));
                    var owner = Marshal.ReadInt32(row + 20);
                    if (owner == pid && addr == 0x0100007F) ports.Add(port);   // 127.0.0.1（網路位元組序）
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return ports;
        }
    }
}
