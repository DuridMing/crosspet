namespace CrossPet
{
    /// <summary>視窗擴充套件樣式裡和「藏起來」有關的那一位（不依賴介面，tests/windows/LogicTests 測）</summary>
    static class ConcealStyle
    {
        public const int WS_EX_TRANSPARENT = 0x20;

        /// <summary>藏起來時讓滑鼠穿過整個視窗。只把不透明度設成 0 不夠：Windows 判斷點沒點到看的是每個畫素自己的透明度，
        /// 不看整體不透明度，鋪在她身上那層幾乎透明的「能點的區域」照樣接住滑鼠，原來那塊看不見卻點不透（1.3.4 測出來的）</summary>
        public static int ClickThrough(int exStyle, bool on) => on ? exStyle | WS_EX_TRANSPARENT : exStyle & ~WS_EX_TRANSPARENT;
    }
}
