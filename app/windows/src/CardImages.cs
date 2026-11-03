using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CrossPet
{
    /// <summary>設定「角色」頁卡片牆用的圖：角色資料夾裡專門畫的展示立繪 portrait（衣服卡）、探頭頭像 avatar（角色卡），和待機圖 idle。
    /// portrait-card 是 portrait 按卡片大小縮好的小圖（Windows 上大圖縮太狠會糊）。portrait、portrait-card、avatar 不是姿態，掃立繪時要排除（不然桌寵會多出一個叫 portrait 的動作）。
    /// 只給存在的檔案，頁面按「展示圖 → 待機圖 → 色塊」退。和 macOS 版 cardImages 一致；不依賴 Windows，LogicTests 在 macOS 上測</summary>
    static class CardImages
    {
        public static bool IsSprite(string stem) => stem != "portrait" && stem != "portrait-card" && stem != "avatar";

        public static Dictionary<string, object> Of(string charactersRoot, string id, string host)
        {
            var d = new Dictionary<string, object>();
            var dir = Path.Combine(charactersRoot, id);
            if (!Directory.Exists(dir)) return d;
            foreach (var stem in new[] { "portrait", "portrait-card", "avatar", "idle" })
            {
                var file = new[] { ".webp", ".png" }.Select(ext => stem + ext).FirstOrDefault(f => File.Exists(Path.Combine(dir, f)));
                if (file != null) d[stem] = $"https://{host}/characters/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(file)}";
            }
            return d;
        }
    }
}
