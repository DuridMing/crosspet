using System;

namespace CrossPet
{
    /// <summary>網頁大小核對（不依賴介面，tests/windows/LogicTests 測）：平時只用來記日誌；只有一種情況會改縮放，見 Fix。
    /// 1.3.4 有幾位使用者（系統縮放大於 100%）遇到模型、氣泡被裁。1.3.5 測試查到一個原因：執行中換了系統縮放（或拖到縮放不同的螢幕），
    /// 網頁控制元件沒跟著重算網頁區域（見 Pet.ResyncWebForDpi）。曾經試過量網頁大小、對不上就自動改縮放，測試裡沒碰到一次真該改的，
    /// 倒是拖滑塊時網頁還沒排完就量了、改錯了兩次——所以改成只記錄：兩次量到的一樣、又和畫布對不上，才寫日誌，留給下一個排查的人</summary>
    static class ZoomCheck
    {
        /// <summary>w1/h1、w2/h2：隔一會兒量的兩次網頁大小（innerWidth / innerHeight）。兩次差 1 以內且和應有的畫布差 2% 以上返回 true</summary>
        public static bool Off(double expectedW, double expectedH, double w1, double h1, double w2, double h2)
        {
            if (expectedW <= 0 || expectedH <= 0 || w1 <= 0 || h1 <= 0) return false;
            if (Math.Abs(w1 - w2) > 1 || Math.Abs(h1 - h2) > 1) return false;   // 還在變：拖滑塊、換縮放的半路
            return Math.Abs(w1 / expectedW - 1) > 0.02 || Math.Abs(h1 / expectedH - 1) > 0.02;
        }

        /// <summary>換了系統縮放後網頁區域還停在舊縮放的畫素上（#28：150%→200% 網頁 195×271、應為 260×362，正好 150/200；
        /// 200%→150% 是 346×483，正好 200/150）：ResyncWebForDpi 沒對齊成功。這時不該改網頁縮放（人物會跟著變大變小），
        /// 要再讓網頁控制元件按新縮放重算一次區域。寬高偏得一樣多、而且正好是「舊縮放 ÷ 新縮放」才算</summary>
        public static bool StuckAtOldDpi(double expectedW, double expectedH, double w, double h, double oldDpi, double newDpi)
        {
            if (expectedW <= 0 || expectedH <= 0 || w <= 0 || h <= 0 || oldDpi <= 0 || newDpi <= 0) return false;
            if (Math.Abs(oldDpi / newDpi - 1) < 0.02) return false;   // 縮放沒變
            double rw = w / expectedW, rh = h / expectedH;
            if (Math.Abs(rw / rh - 1) > 0.015) return false;
            return Math.Abs((rw + rh) / 2 / (oldDpi / newDpi) - 1) < 0.02;
        }

        /// <summary>系統縮放被算了兩遍時的修正（10-17 反饋：125%、Windows 預覽版、WebView2 155，網頁 224×298，應為 280×372）：
        /// 網頁按「人物大小 × 系統縮放 × 系統縮放」畫，寬高都正好小了一個系統縮放倍，氣泡被擠出視窗。
        /// 返回網頁縮放該乘的係數（給網頁的縮放 = 人物大小 × 係數），不用改返回 null。
        /// 只認這一種指紋，免得像 1.3.5 那樣拖滑塊時量早了改錯：寬高偏得一樣多，而且修完的係數是 1（修過又好了，撤銷）或 1 / 系統縮放。
        /// w、h 是已經兩次量過、穩定的網頁大小（先過 Off）；fix 是現在用著的係數</summary>
        public static double? Fix(double expectedW, double expectedH, double w, double h, double dpi, double fix)
        {
            if (expectedW <= 0 || expectedH <= 0 || w <= 0 || h <= 0 || dpi <= 0 || fix <= 0) return null;
            double rw = w / expectedW, rh = h / expectedH;
            if (Math.Abs(rw / rh - 1) > 0.015) return null;   // 寬高偏得不一樣：不是縮放的事
            var next = fix * (rw + rh) / 2;
            if (Math.Abs(next / fix - 1) < 0.02) return null;   // 差不多對得上
            if (Math.Abs(next - 1) < 0.02) return 1;
            if (dpi > 1.01 && Math.Abs(next * dpi - 1) < 0.02) return 1 / dpi;
            return null;
        }
    }
}
