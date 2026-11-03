using System;
using System.IO;

namespace CrossPet
{
    /// <summary>立繪請求 https://crosspet.data/characters/&lt;角色&gt;/&lt;圖&gt; → 角色目錄裡的檔案。
    /// 只認這一種形狀、只給 .webp / .png，別的（設定、character.json、目錄跳轉）一律不給。
    /// 不依賴 WebView2，tests/windows/LogicTests 在 macOS 上也能測</summary>
    static class SpritePath
    {
        public static bool TryResolve(string charactersRoot, string absolutePath, out string file, out string mime)
        {
            file = mime = null;
            var parts = (absolutePath ?? "").Split('/');
            if (parts.Length != 4 || parts[0] != "" || parts[1] != "characters") return false;
            var character = Uri.UnescapeDataString(parts[2]);
            var name = Uri.UnescapeDataString(parts[3]);
            if (!SafeSegment(character) || !SafeSegment(name)) return false;
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext != ".webp" && ext != ".png") return false;
            var root = Path.GetFullPath(charactersRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, character, name));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            file = full;
            mime = ext == ".webp" ? "image/webp" : "image/png";
            return true;
        }

        // 一段目錄名 / 檔名：解碼後不能是空、. 、..，不能帶路徑分隔符、磁碟機代號冒號、Windows 不認的字元，結尾不能是點或空格
        // （Windows 會悄悄去掉結尾的點和空格，換成另一個檔案）。這些按 Windows 的規矩寫死，在 macOS 上測也一樣
        const string Bad = "/\\:*?\"<>|";
        static bool SafeSegment(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s == "." || s == ".." || s.EndsWith(".") || s.EndsWith(" ")) return false;
            foreach (var c in s) if (c < 32 || Bad.IndexOf(c) >= 0) return false;
            return s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }
    }
}
