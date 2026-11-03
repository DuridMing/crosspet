using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CrossPet
{
    /// <summary>記錄檔案（反饋用）：一行一個 JSON，只留最近 200 行（超過 240 行時截回 200）。
    /// 桌寵處理記錄 events\pet.jsonl、換角色記錄 switches.jsonl 都用它；格式見 docs/架构.md「記錄」。
    /// 自己拼 JSON（值只有字串、數字、布林）：外殼是 .NET Framework、測試是 .NET 8，兩邊沒有共用的 JSON 庫。寫不了就算了，不影響桌寵</summary>
    static class Ledger
    {
        public static string Line(IDictionary<string, object> line)
        {
            var row = new Dictionary<string, object>(line) { ["t"] = Math.Round(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, 2) };
            return "{" + string.Join(",", row.Select(kv => Str(kv.Key) + ":" + Value(kv.Value))) + "}";
        }

        public static void Append(string path, IDictionary<string, object> line, int keep = 200)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, Line(line) + "\n", new UTF8Encoding(false));
                var rows = File.ReadAllLines(path).Where(l => l.Trim() != "").ToArray();
                if (rows.Length > 240) File.WriteAllText(path, string.Join("\n", rows.Skip(rows.Length - keep)) + "\n", new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>檔案最後 n 行（沒有檔案返回空串）</summary>
        public static string Tail(string path, int n)
        {
            try { return File.Exists(path) ? string.Join("\n", File.ReadAllLines(path).Reverse().Take(n).Reverse()) : ""; } catch { return ""; }
        }

        static string Value(object v) =>
            v == null ? "null" : v is bool b ? (b ? "true" : "false")
            : v is int || v is long || v is double || v is float || v is decimal ? Convert.ToString(v, CultureInfo.InvariantCulture) : Str(v.ToString());

        static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s)
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            return sb.Append('"').ToString();
        }
    }
}
