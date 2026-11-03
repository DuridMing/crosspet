using System;
using System.Globalization;

namespace CrossPet
{
    // 只管異常恢復的持久記賬；啟動成功並穩定兩分鐘後由 UI 執行緒清除。
    // 不依賴 WPF / WebView2，LogicTests 可以重放連續失敗和時間邊界。
    sealed class RenderRestartState
    {
        public const int MaxAttempts = 3;
        public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(10);
        public int Attempts { get; }
        public DateTime LastAttemptUtc { get; }
        public RenderRestartState(int attempts, DateTime at) { Attempts = attempts; LastAttemptUtc = at; }

        public bool Recent(DateTime now) => now - LastAttemptUtc < FailureWindow;
        public RenderRestartState Next(DateTime now)
        {
            var count = Recent(now) ? Attempts : 0;
            return count >= MaxAttempts ? null : new RenderRestartState(count + 1, now);
        }

        public string Serialize() => "v2|" + Attempts + "|" + LastAttemptUtc.ToString("O", CultureInfo.InvariantCulture);
        public static RenderRestartState Parse(string text, DateTime fileUtc)
        {
            var parts = (text ?? "").Trim().Split('|');
            if (parts.Length == 3 && parts[0] == "v2" && int.TryParse(parts[1], out var count) && count >= 1 && count <= MaxAttempts
                && DateTime.TryParse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) && at.Kind == DateTimeKind.Utc)
                return new RenderRestartState(count, at);
            // 舊版檔案是本地時間的字串，舊邏輯也以 mtime 計時；不猜舊字串的時區。
            return new RenderRestartState(1, fileUtc);
        }
    }
}
