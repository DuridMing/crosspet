using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace CrossPet
{
    // WPF 合成執行緒已損壞後，只做檔案/程式交接，不能 Close/Dispose WebView2。
    // net48 的 Environment.Exit 也會執行終結器（#28：HwndHost.Finalize 內的 COM 強轉崩潰）。
    static class RenderRecovery
    {
        static string Mark => Path.Combine(Store.Data, "render-restart.txt");
        static int restarting;
        static DispatcherTimer healthyTimer;
        static bool notice;

        static RenderRestartState ReadState() => File.Exists(Mark)
            ? RenderRestartState.Parse(File.ReadAllText(Mark), File.GetLastWriteTimeUtc(Mark)) : new RenderRestartState(0, DateTime.UtcNow);

        public static void Started()
        {
            try
            {
                var state = ReadState();
                notice = state.Attempts > 0 && state.Recent(DateTime.UtcNow);
                if (notice) Store.Log($"檢測到上次渲染失敗記錄：已嘗試重啟 {state.Attempts} 次，等待網頁穩定");
            }
            catch (Exception e) { Store.Log("讀取渲染恢復記錄失敗: " + e.Message); }
        }

        // 返回是否需要提示：每程式只提示一次。必須網頁就緒，且 UI 執行緒繼續工作兩分鐘才算恢復成功。
        public static bool Ready()
        {
            healthyTimer?.Stop();
            healthyTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
            healthyTimer.Tick += (_, __) =>
            {
                healthyTimer.Stop();
                if (Volatile.Read(ref restarting) != 0 || !File.Exists(Mark)) return;   // 沒出過故障就不寫這行（不然每次啟動都記一條，排查時誤導人）
                try { File.Delete(Mark); Store.Log("渲染恢復確認：網頁已穩定執行 2 分鐘，重試計數清零"); }
                catch (Exception e) { Store.Log("清除渲染恢復記錄失敗: " + e.Message); }
            };
            healthyTimer.Start();
            var show = notice; notice = false;
            return show;
        }

        public static void Restart()
        {
            if (Interlocked.Exchange(ref restarting, 1) != 0) return;
            healthyTimer?.Stop();
            try
            {
                var next = ReadState().Next(DateTime.UtcNow);
                if (next != null)
                {
                    // 先落盤再交接；若記錄不可寫就停止，不能在沒有預算保護的情況下無限重啟。
                    var temp = Mark + ".tmp";
                    File.WriteAllText(temp, next.Serialize());
                    if (File.Exists(Mark)) File.Replace(temp, Mark, null); else File.Move(temp, Mark);
                    Store.Log($"渲染執行緒掛了，自動重啟 CrossPet（第 {next.Attempts}/{RenderRestartState.MaxAttempts} 次）");
                }
                else Store.Log("渲染執行緒連續失敗，重試預算已耗盡，停止自動重啟並提示手動開啟");
                var parent = Process.GetCurrentProcess();
                // 不經過 cmd/start（安裝路徑可能含空格、& 等）；子程式先等父程式真正退出，再搶單例項鎖。
                var args = $"--render-restart {parent.Id} {parent.StartTime.ToUniversalTime().Ticks} {(next == null ? "stop" : "retry")}";
                if (Process.Start(new ProcessStartInfo(Path.Combine(Store.Install, "CrossPet.exe"), args)
                    { UseShellExecute = false, WorkingDirectory = Store.Install }) == null)
                    throw new InvalidOperationException("未能建立接班程式");
            }
            catch (Exception e)
            {
                Store.Log("自動重啟失敗: " + e.Message);
                MessageBoxW(IntPtr.Zero, "CrossPet 的畫面出了問題，自動重啟沒有成功。請手動重新開啟；若反覆發生，請提交反饋。", "CrossPet", 0x10);
            }
            // 只終止自身。讓核心回收已損壞的程式，跳過 CLR 終結器和 WebView2 COM 清理。
            // 設定在正常操作時已儲存；此處不嘗試在壞掉的介面上再次儲存/釋放控制元件。
            TerminateProcess(GetCurrentProcess(), 0);
            Environment.FailFast("終止損壞的渲染程式失敗"); // 極少數核心呼叫失敗時也不能回到託管清理路徑。
        }

        // 在 Main 進入單例項鎖、建立 Application/WebView2 之前執行。
        public static bool WaitForParent(string[] args)
        {
            if (args.Length == 0 || args[0] != "--render-restart") return true;
            if (args.Length != 4 || !int.TryParse(args[1], out var pid) || pid <= 0
                || !long.TryParse(args[2], out var ticks) || (args[3] != "retry" && args[3] != "stop")) return false;
            try
            {
                Process parent;
                try { parent = Process.GetProcessById(pid); } catch (ArgumentException) { parent = null; }
                using (parent)
                {
                    // PID 可能已被系統複用；只能等待原來那一個程式。
                    if (parent != null && parent.StartTime.ToUniversalTime().Ticks == ticks && !parent.WaitForExit(15000))
                    { Store.Log("渲染恢復接班超時：舊程式未退出，不啟動重複例項"); return false; }
                }
            }
            catch (InvalidOperationException) { /* 在讀取時間或等待前剛好退出，單例項鎖會在後面繼續防重。 */ }
            catch (Exception e) { Store.Log("渲染恢復接班失敗: " + e.Message); return false; }
            if (args[3] == "stop")
            {
                MessageBoxW(IntPtr.Zero, "CrossPet 的圖形元件連續出錯，已停止自動重啟以免迴圈。請手動重新開啟，並通過托盤提交反饋。設定和角色檔案已保留。", "CrossPet", 0x30);
                return false;
            }
            Store.Log("渲染恢復接班：舊程式已退出，開始重新開啟");
            return true;
        }

        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
    }
}
