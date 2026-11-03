using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CrossPet
{
    /// <summary>安裝目錄是否完整（不依賴介面，tests/windows/LogicTests 測）。
    /// #24：一鍵更新時安裝指令碼先刪舊目錄再複製，鉤子正佔著 python\python.exe 刪不掉，刪到一半就停了——
    /// characters、integrations 沒了，之後每次啟動都崩，使用者只看到「打不開」。1.3.7 起安裝指令碼不再原地刪（見 install.ps1），
    /// 這裡是兜底：啟動前看一眼，缺了就提示，有下載好的安裝包就用它修</summary>
    static class InstallCheck
    {
        public static readonly string[] Required = { "characters", "web", "integrations", "tools", "python" };

        /// <summary>安裝目錄裡缺了哪些必需的資料夾（按 Required 的順序）</summary>
        public static List<string> Missing(string installDir) =>
            Required.Where(d => !Directory.Exists(Path.Combine(installDir, d))).ToList();

        /// <summary>一鍵更新下載過的安裝包（%TEMP%\CrossPet-update\&lt;版本&gt;\CrossPet）裡，版本最新、本身完整、帶 install.ps1 的那個；沒有返回 null</summary>
        public static string RepairPackage(string updateRoot)
        {
            if (!Directory.Exists(updateRoot)) return null;
            return Directory.GetDirectories(updateRoot)
                .Select(v => (ver: Path.GetFileName(v), pkg: Path.Combine(v, "CrossPet")))
                .Where(x => File.Exists(Path.Combine(x.pkg, "install.ps1")) && Missing(x.pkg).Count == 0)
                .OrderByDescending(x => VersionKey(x.ver))
                .Select(x => x.pkg)
                .FirstOrDefault();
        }

        public enum Place { Installed, Package, InsideZip, Other }

        /// <summary>CrossPet.exe 是從哪兒開啟的（#28：直接執行解壓出來的 exe，容易以為裝好了，接入 AI 時鉤子卻記著下載資料夾的路徑）。
        /// Installed：就在安裝目錄；Package：解壓出來的完整安裝包（有 install.ps1、資料夾齊全），可以一鍵安裝；
        /// InsideZip：在資源管理器裡直接雙擊壓縮包裡的 exe，Windows 只把它取到臨時資料夾（路徑裡有「xxx.zip」這一層），要先解壓；
        /// Other：別的（開發時從編譯目錄跑），照常啟動</summary>
        public static Place Locate(string exeDir, string installDir)
        {
            string Full(string d) { try { return Path.GetFullPath(d).TrimEnd('\\', '/'); } catch { return d.TrimEnd('\\', '/'); } }
            if (string.Equals(Full(exeDir), Full(installDir), StringComparison.OrdinalIgnoreCase)) return Place.Installed;
            // 寧可漏問，也不能把裝好的當成安裝包（那樣每次開啟都問要不要安裝，點取消就退出，等於打不開）：
            // AppData 被重定向、目錄連結、短檔名都可能讓上面對不上，所以只要本身就叫 …\Programs\CrossPet 也算裝好了
            var segs = Full(exeDir).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segs.Length >= 2 && string.Equals(segs[segs.Length - 1], "CrossPet", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segs[segs.Length - 2], "Programs", StringComparison.OrdinalIgnoreCase)) return Place.Installed;
            // 先認壓縮包：有的解壓方式會把整包取到臨時資料夾，看著齊全，可關掉壓縮包視窗就被清掉，不能從那兒裝
            if (exeDir.Split('\\', '/').Any(seg => seg.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))) return Place.InsideZip;
            if (File.Exists(Path.Combine(exeDir, "install.ps1")) && Missing(exeDir).Count == 0) return Place.Package;
            return Place.Other;
        }

        // v1.3.10 → 「0001.0003.0010」：按數字比，不按字母比（1.3.10 比 1.3.9 新）
        static string VersionKey(string tag) =>
            string.Join(".", tag.TrimStart('v', 'V').Split('-')[0].Split('.').Select(n => (int.TryParse(n, out var i) ? i : 0).ToString("D4")));
    }
}
