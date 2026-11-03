namespace CrossPet
{
    static class ZOrderRule
    {
        /// <summary>桌面、工作列（全屏判斷也排除它們）</summary>
        public static bool IsShell(string cls) =>
            cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd";

        /// <summary>取消置頂時要不要插到前臺視窗正下方（這樣不會蓋住你正在用的程式）。
        /// 只對普通程式視窗這麼做：前臺是桌面時插到它下面，桌寵就被桌布整個蓋住、哪裡都找不到（1.3.7 使用者反饋：
        /// 「跟著 AI」層級下右鍵幾次、換個角色她就不見了，托盤和程式都還在）；前臺是 CrossPet 自己的右鍵選單、設定視窗時也不插</summary>
        public static bool TuckBelow(string frontClass, bool frontIsOurs) =>
            !frontIsOurs && !string.IsNullOrEmpty(frontClass) && !IsShell(frontClass);

        /// <summary>該不該置頂。mode：always / ai / normal；aiActive：AI 程式在前臺，或剛乾完活、在等你回答。
        /// 「跟著 AI」時前臺是桌面、工作列也置頂：你沒在用任何程式，她擋不著誰；不然按 Win+D 回到桌面時，
        /// 桌面會把不置頂的她和別的視窗一起蓋住，一直不出來（loki 實測：開 Chrome 再 Win+D 她就沒了）</summary>
        public static bool Top(string mode, bool aiActive, bool frontIsShell) =>
            mode == "always" || (mode == "ai" && (aiActive || frontIsShell));
    }
}
