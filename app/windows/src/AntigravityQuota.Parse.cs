using System;
using System.Collections.Generic;
using System.Linq;

namespace CrossPet
{
    // 額度回覆的排版（不依賴 Windows，tests/windows/LogicTests 在 macOS 上測）。取額度的部分在 AntigravityQuota.cs
    static partial class AntigravityQuota
    {
        /// <summary>
        /// 額度按「額度池」共享：Gemini 模型一個池，Claude 和 GPT 模型一個池（Antigravity 的「View Usage」也是這兩組）。
        /// 固定顯示這兩行，Gemini 在前；重置時間標在第一行和快用完的那行上。和 macOS 版 antigravityQuota 一樣。
        /// </summary>
        /// <summary>額度彙總或使用者狀態，哪種都認（彙總優先）</summary>
        public static Dictionary<string, object> Parse(object obj) => ParseSummary(obj) ?? ParseStatus(obj);

        /// <summary>額度彙總（RetrieveUserQuotaSummary）：Antigravity 自己分好組（「Gemini Models」「Claude and GPT models」），
        /// 每組一個或幾個視窗（5h / 本週 / 今日……）。text 給關閉「合併顯示」時用（每組一行）；
        /// bars 是結構化的額度條（和別家的 bars 同一個格式）：每個視窗一項 {label, left, reset?, group}，同一個池的視窗挨著排，短視窗在前；
        /// reset 是 Unix 秒，沒有就不寫；group 是池名。合併顯示怎麼排由網頁層按 bars 決定。和 macOS 版 antigravitySummary 一致</summary>
        public static Dictionary<string, object> ParseSummary(object obj)
        {
            if (!(obj is Dictionary<string, object> root) || !(root.TryGetValue("response", out var r0) && r0 is Dictionary<string, object> resp)) return null;
            if (!(resp.TryGetValue("groups", out var gs) && gs is object[] groups)) return null;
            string Title(Dictionary<string, object> g) => g.TryGetValue("displayName", out var t) ? t as string ?? "" : "";
            var rows = new List<(string name, double left, DateTime? reset)>();
            var bars = new List<object>();
            string Piece((string label, double left, DateTime? reset) r)
            {
                var piece = $"{r.label} 剩餘 {(int)Math.Round(r.left * 100)}%";
                if (r.reset is DateTime t)   // 每行都標重置時間（和 macOS 版一致）
                    piece += $" · {((t - DateTime.Now).TotalHours < 24 ? t.ToString("HH:mm") : $"{t.Month}/{t.Day}")} 重置";
                return piece;
            }
            foreach (var g in groups.OfType<Dictionary<string, object>>().Select((g, i) => (g, i)).OrderBy(x => Title(x.g).Contains("Gemini") ? 0 : 1).ThenBy(x => x.i).Select(x => x.g))
            {
                var title = Title(g);
                var isGemini = title.ToLowerInvariant() == "gemini models";
                var isGpt = !isGemini && (title.Contains("Claude") || title.Contains("GPT"));
                var name = isGemini ? "Gemini" : isGpt ? "Claude/GPT" : title;
                var buckets = (g.TryGetValue("buckets", out var bs) && bs is object[] arr ? arr : new object[0]).OfType<Dictionary<string, object>>().ToList();
                // 每組在 text 裡只留一行（和 macOS 版一致）：有 5h 視窗、且 5h 沒滿 → 顯示 5h；5h 滿了 → 顯示別的視窗裡最緊的（一般是週額度）；
                // 例外：最緊的視窗 ≤10% 時不管 5h 怎樣都顯示它。一樣緊的取重置得更晚的，不看介面給的順序
                var cands = new List<(string win, string window, double left, DateTime? reset)>();
                foreach (var b in buckets)
                {
                    // 比例為 0 時會被省略（protobuf 轉 JSON 不寫預設值）：視窗在、比例不在 = 用完了
                    var left = b.TryGetValue("remainingFraction", out var f) ? Convert.ToDouble(f) : 0;
                    var win = b.TryGetValue("window", out var w) ? w as string ?? "" : "";
                    var window = win == "weekly" ? "本週" : win == "daily" ? "今日" : win == "5h" ? "5小時" : (b.TryGetValue("displayName", out var dn) && !string.IsNullOrEmpty(dn as string) ? (string)dn : win);
                    var resetText = b.TryGetValue("resetTime", out var rt) ? rt as string ?? "" : "";
                    DateTime? reset = DateTime.TryParse(resetText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : (DateTime?)null;
                    cands.Add((win, window, left, reset));
                }
                if (cands.Count == 0) continue;
                var tightest = cands[0];
                foreach (var c in cands.Skip(1))
                    if (c.left < tightest.left || (c.left == tightest.left && (c.reset ?? DateTime.MinValue) > (tightest.reset ?? DateTime.MinValue))) tightest = c;
                var five = cands.FirstOrDefault(c => c.win == "5h");
                var pick = tightest.left > 0.1 && five.win == "5h" && five.left < 1 ? five : tightest;
                rows.Add((cands.Count > 1 ? $"{name} {pick.window}" : name, pick.left, pick.reset));
                // bars：每個視窗一項（不只是挑出來的那個），短視窗在前
                var pool = isGemini ? "Gemini" : isGpt ? "Claude/GPT" : System.Text.RegularExpressions.Regex.Replace(title, @"^Gemini\s+(\d+\s+)?", "");
                int Order(string win) => win == "5h" ? 0 : win == "daily" ? 1 : win == "weekly" ? 2 : 3;
                foreach (var c in cands.OrderBy(c => Order(c.win)))
                {
                    var bar = new Dictionary<string, object> { ["label"] = $"{pool} {c.window}", ["left"] = c.left, ["group"] = pool };
                    if (c.reset is DateTime rd) bar["reset"] = new DateTimeOffset(rd).ToUnixTimeSeconds();
                    bars.Add(bar);
                }
            }
            if (rows.Count == 0) return null;
            return new Dictionary<string, object>
            {
                ["text"] = string.Join(" ｜ ", rows.Select(r => Piece((r.name, r.left, r.reset)))),
                ["low"] = rows.Min(r => r.left) <= 0.1,
                ["bars"] = bars,
            };
        }

        /// <summary>使用者狀態（GetUserStatus，舊版 Antigravity 的退路；裡面的額度是模型列表順帶的快取，會慢幾分鐘）</summary>
        public static Dictionary<string, object> ParseStatus(object obj)
        {
            if (!(obj is Dictionary<string, object> root) || !(root.TryGetValue("userStatus", out var st) && st is Dictionary<string, object> status)) return null;
            if (!(status.TryGetValue("cascadeModelConfigData", out var cfg) && cfg is Dictionary<string, object> config)) return null;
            if (!(config.TryGetValue("clientModelConfigs", out var ms) && ms is object[] models)) return null;
            // 固定兩行，和 Antigravity 自己「View Usage」的分組一樣：Gemini 模型一行，Claude 和 GPT 模型一行。
            // 數字相同也不合並（以前按「剩餘 + 重置時間」合併，行數會變來變去，和 Antigravity 對不上）。和 macOS 版 antigravityQuota 一致
            var groups = new List<(string name, double left, DateTime? reset)>();
            foreach (var m in models.OfType<Dictionary<string, object>>())
            {
                if (!(m.TryGetValue("quotaInfo", out var qi) && qi is Dictionary<string, object> info)) continue;
                // 不可用的模型不算：比如只給 Ultra 套餐的「Gemini 4 Argon」，別的賬號裡標著 disabled、額度資訊是空的，
                // 以前被當成「剩 0%」排在第一行（2026-10-14 兩位使用者反饋）
                if ((m.TryGetValue("disabled", out var dis) && dis is bool off && off) || (!info.ContainsKey("remainingFraction") && !info.ContainsKey("resetTime"))) continue;
                var left = info.TryGetValue("remainingFraction", out var f) ? Convert.ToDouble(f) : 0;   // 用完時這個欄位會被省略（但還有重置時間）
                var resetText = info.TryGetValue("resetTime", out var r) ? r as string ?? "" : "";
                DateTime? reset = DateTime.TryParse(resetText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : (DateTime?)null;
                var name = ((m.TryGetValue("label", out var l) ? l as string : null) ?? "").StartsWith("Gemini") ? "Gemini" : "Claude/GPT";
                var i = groups.FindIndex(g => g.name == name);
                if (i < 0) groups.Add((name, left, reset));
                else if (left < groups[i].left) groups[i] = (name, left, reset ?? groups[i].reset);
            }
            if (groups.Count == 0) return null;
            groups = groups.OrderBy(g => g.name == "Gemini" ? 0 : 1).ToList();
            var parts = groups.Select((g, i) =>
            {
                var piece = $"{g.name} 剩餘 {(int)Math.Round(g.left * 100)}%";
                // 重置時間標在第一行（Gemini）上；別的那行快用完了也標
                if ((i == 0 || g.left <= 0.1) && g.reset is DateTime t)
                    piece += $" · {((t - DateTime.Now).TotalHours < 24 ? t.ToString("HH:mm") : $"{t.Month}/{t.Day}")} 重置";
                return piece;
            });
            return new Dictionary<string, object> { ["text"] = string.Join(" ｜ ", parts), ["low"] = groups.Min(g => g.left) <= 0.1 };
        }
    }
}
