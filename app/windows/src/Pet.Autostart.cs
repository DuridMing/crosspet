using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CrossPet
{
    sealed partial class Pet
    {
        // ---------------- 跟著 AI 出現 ----------------
        // 接入的 AI 開始工作時，鉤子指令碼 / DSH 外掛發現桌寵沒在跑（查單例項互斥量）就把它開啟。
        // 從選單手動退出 = 這會兒不想看到它：記下 user-quit（看檔案修改時間），在那之前就開著的 AI 不再把它拉起來；
        // AI 程式關掉重開、重啟電腦後就作廢，自己開啟桌寵也會清掉（見 Program）。
        // 設定裡關掉「AI 開始工作時自動出現」= 寫 ai-autostart-off。一鍵更新時的退出不算手動退出。
        public static string UserQuitFlag => Path.Combine(Store.Data, "user-quit");
        static string AIAutostartOffFlag => Path.Combine(Store.Data, "ai-autostart-off");
        static bool AIAutostart => !File.Exists(AIAutostartOffFlag);
        static void SetAIAutostart(bool on)
        {
            try { if (on) File.Delete(AIAutostartOffFlag); else File.WriteAllText(AIAutostartOffFlag, ""); }
            catch (Exception e) { Store.Log("設定跟著 AI 出現失敗: " + e.Message); }
        }

        /// 選單裡的「退出」：使用者主動退出。第一次退出時說一句，讓人知道什麼時候會再出來
        void UserQuit()
        {
            try { File.WriteAllText(UserQuitFlag, ""); } catch { }
            // 揮手道別再走；第一次退出時說明什麼時候會再出來，多停一會兒讓人看清
            var hint = AIAutostart && !Store.Flag("quitHinted");
            if (hint) { Store.Settings["quitHinted"] = true; Store.SaveSettings(); }
            Js(hint ? "farewell('我先走啦～下次重新開啟 AI 我再出來', 5000)" : "farewell(null, 2000)");
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(hint ? 5 : 2) };
            t.Tick += (_, __) => { t.Stop(); Quit(); };
            t.Start();
        }

        public void Quit()
        {
            foreground?.Dispose();
            tray.Visible = false; tray.Dispose();
            Application.Current.Shutdown();
        }

        // ---------------- 開機啟動（當前使用者的 Run 登入檔項，不需要管理員） ----------------
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        static string Exe => Process.GetCurrentProcess().MainModule.FileName;
        static bool LoginEnabled
        {
            get { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return (k?.GetValue("CrossPet") as string)?.Contains(Exe) == true; }
        }
        static void ToggleLogin()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                if (LoginEnabled) k.DeleteValue("CrossPet", false); else k.SetValue("CrossPet", $"\"{Exe}\"");
        }
    }
}
