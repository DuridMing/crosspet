using System;
using System.Collections.Generic;
using System.IO;

namespace CrossPet
{
    /// <summary>maid fork：打包只帶 Claude，其他 AI 寫的狀態檔（gpt-state.json……）沒有對應角色，原本不會被讀，
    /// 寫了 reactTo 的角色也接不到。這裡列出 state 目錄裡沒有對應角色的 AI，讓 Poll 也交給網頁，由網頁依 reactTo 決定要不要演。</summary>
    static class StateFiles
    {
        public static List<string> OtherHosts(string stateDir, IEnumerable<string> known)
        {
            const string suffix = "-state.json";
            var seen = new HashSet<string>(known);
            var hosts = new List<string>();
            try
            {
                foreach (var file in Directory.GetFiles(stateDir, "*" + suffix))
                {
                    var name = Path.GetFileName(file);
                    if (name.StartsWith(".")) continue;   // 寫到一半的臨時檔
                    var id = name.Substring(0, name.Length - suffix.Length);
                    if (!seen.Contains(id)) hosts.Add(id);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            hosts.Sort(StringComparer.Ordinal);
            return hosts;
        }
    }
}
