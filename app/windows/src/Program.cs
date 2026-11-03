using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace CrossPet
{
    static class Program
    {
        const string WebView2Download = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        /// WPF 渲染執行緒掛了（0x88980406 UCEERR_RENDERTHREADFAILURE）：這之後視窗不會再重畫，當作沒事繼續跑就是畫面凍住
        static bool IsRenderThreadFailure(Exception e)
        {
            for (var x = e; x != null; x = x.InnerException)
                if ((uint)x.HResult == 0x88980406 || (x.Message ?? "").Contains("UCEERR_RENDERTHREADFAILURE")) return true;
            return false;
        }

        // 只從開發者選單觸發；通過 Dispatcher 丟擲同一 HRESULT，走真實異常處理與重啟交接。
        internal static void InjectRenderFailure() => Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            throw new System.Runtime.InteropServices.COMException("開發測試：UCEERR_RENDERTHREADFAILURE", unchecked((int)0x88980406))));

        /// <summary>安裝目錄缺東西（#24：上次一鍵更新刪到一半）時不往下走——不然每次啟動都崩、使用者只看到「打不開」。
        /// 找得到下載好的完整安裝包就問要不要用它修復，找不到就指去釋出頁重新下載</summary>
        static bool InstallComplete()
        {
            var missing = InstallCheck.Missing(Store.Install);
            if (missing.Count == 0) return true;
            Store.Log("安裝不完整，缺少：" + string.Join("、", missing));
            var pkg = InstallCheck.RepairPackage(Path.Combine(Path.GetTempPath(), "CrossPet-update"));
            var what = $"CrossPet 的安裝不完整（缺少 {string.Join("、", missing)}），多半是上次更新沒裝完。";
            if (pkg != null)
            {
                var ver = Path.GetFileName(Path.GetDirectoryName(pkg));
                if (MessageBox.Show($"{what}\n\n找到了已經下載好的安裝包（{ver}），要現在用它修復嗎？設定、角色和 AI 接入都會保留。",
                        "CrossPet", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    Store.Log("用下載好的安裝包修復：" + pkg);
                    Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(pkg, "install.ps1")}\"") { UseShellExecute = false });
                }
                return false;
            }
            if (MessageBox.Show($"{what}\n\n請重新下載安裝（設定、角色和 AI 接入都會保留）。要現在開啟下載頁嗎？",
                    "CrossPet", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("https://github.com/DuridMing/crosspet/releases/latest") { UseShellExecute = true });
            return false;
        }

        /// <summary>「雙擊 exe 就安裝」先關著：它每次啟動都要判斷，判斷錯了已裝好的也會被當成安裝包（每次問要不要安裝，等於打不開）。
        /// 1.3.8 正式版不帶，下一個測試版開啟它單獨測</summary>
        const bool SelfInstall = false;

        /// <summary>不是從安裝目錄開啟的（#28）：解壓出來的安裝包就問要不要現在安裝（和一鍵更新一樣在後臺跑 install.ps1，裝好自己開啟），
        /// 還在壓縮包裡就說先解壓。返回 false = 這個程式不往下走</summary>
        static bool FromInstallDir()
        {
            // 和 install.ps1 一樣用 %LOCALAPPDATA%（兩邊拿法不同，個別電腦上可能對不上）
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            if (string.IsNullOrEmpty(local)) local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var destination = Path.Combine(local, "Programs", "CrossPet");
            switch (InstallCheck.Locate(Store.Install, destination))
            {
                case InstallCheck.Place.InsideZip:
                    MessageBox.Show("CrossPet 還在壓縮包裡，沒法直接執行。\n\n請先把壓縮包整個解壓（右鍵 →「全部解壓縮」），再雙擊解壓出來的 CrossPet.exe。",
                        "CrossPet", MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                case InstallCheck.Place.Package:
                    if (MessageBox.Show($"要把 CrossPet 安裝到這臺電腦嗎？\n\n會裝到 {destination}，在開始選單建快捷方式，裝好後自動開啟，不需要管理員許可權。" +
                            "已經裝過的話就是更新，設定、角色和 AI 接入都保留。\n\n安裝要十幾秒，裝好後桌寵會自己出現。",
                            "安裝 CrossPet", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
                    {
                        Store.Log("從安裝包安裝：" + Store.Install);
                        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{Path.Combine(Store.Install, "install.ps1")}\" -Update")
                            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Store.Install });
                    }
                    return false;
                default:
                    return true;
            }
        }

        // [STAThread] 必須緊挨著 Main：WPF 和 WebView2 都要求主執行緒是 STA（中間插了別的函式，Main 就成了 MTA，啟動報 RPC_E_CHANGED_MODE）
        [STAThread]
        static void Main(string[] args)
        {
            if (!RenderRecovery.WaitForParent(args)) return;
            if (SelfInstall && args.Length == 0 && !FromInstallDir()) return;   // 放在「只開一個」前面：已裝的舊版開著時，雙擊新版的安裝包也要能裝
            // 只開一個：再次開啟時讓已經在跑的那個回到右下角
            using (var mutex = new Mutex(true, @"Local\CrossPet-" + Environment.UserName, out var first))
            using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CrossPet-Show-" + Environment.UserName))
            {
                if (!first) { show.Set(); return; }

                Directory.CreateDirectory(Store.Data);
                RenderRecovery.Started();
                try { File.Delete(Pet.UserQuitFlag); } catch { }   // 自己開啟了：恢復跟著 AI 出現
                try { Directory.CreateDirectory(Store.State); File.WriteAllText(Path.Combine(Store.State, "pet.pid"), Process.GetCurrentProcess().Id.ToString()); } catch { }   // DSH 外掛看它判斷桌寵在不在跑
                AppDomain.CurrentDomain.UnhandledException += (_, e) => Store.Log("崩潰: " + e.ExceptionObject);
                Store.Log($"啟動 {Store.Version}，{Environment.OSVersion}，64 位程式 = {Environment.Is64BitProcess}");
                if (!InstallComplete()) return;
                System.Windows.Forms.Application.EnableVisualStyles();

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (_, e) =>
                {
                    Store.Log("介面異常: " + e.Exception);
                    e.Handled = true;
                    if (IsRenderThreadFailure(e.Exception)) RenderRecovery.Restart();
                };

                // WebView2 執行時：Windows 11 自帶；老的 Windows 10 可能沒有，指引去微軟官網裝
                string version;
                try { version = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
                catch { version = null; }
                if (string.IsNullOrEmpty(version))
                {
                    if (MessageBox.Show("CrossPet 需要系統元件「Microsoft Edge WebView2 執行時」（Windows 11 自帶）。\n\n要現在開啟微軟官網下載嗎？",
                        "CrossPet", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                        Process.Start(new ProcessStartInfo(WebView2Download) { UseShellExecute = true });
                    return;
                }
                Store.Log("WebView2 執行時 " + version);
                Store.LoadSettings();
                // 不能做「軟體渲染」相容模式（#28 提過）：網頁畫面是 WebView2CompositionControl 用 D3DImage 貼進透明視窗的，
                // RenderOptions.ProcessRenderMode = SoftwareOnly 時 D3DImage 不出畫面，桌寵整個空白（1.3.8 test.10 實測）

                app.Startup += async (_, __) =>
                {
                    try
                    {
                        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Store.Data, "webview2"));
                        var pet = new Pet(env);
                        pet.Start();
                        new Thread(() =>
                        {
                            while (show.WaitOne()) app.Dispatcher.BeginInvoke(new Action(pet.Recenter));
                        }) { IsBackground = true }.Start();
                    }
                    catch (Exception e)
                    {
                        Store.Log("啟動失敗: " + e);
                        MessageBox.Show("CrossPet 啟動失敗：\n" + e.Message, "CrossPet");
                        app.Shutdown();
                    }
                };
                app.Run();
                try { File.Delete(Path.Combine(Store.State, "pet.pid")); } catch { }
            }
        }
    }
}
