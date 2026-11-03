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
        // ---------------- 快捷鍵隱藏 ----------------
        // 全域性快捷鍵（預設 Ctrl+Alt+P）：按一下藏起來，再按一下出來；雙擊托盤圖示也能叫出來。藏著時 AI 幹活也不出來，重開恢復顯示
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const int HotKeyId = 0x4350;
        static readonly (string id, string label, uint vk)[] HotkeyChoices = { ("p", "Ctrl+Alt+P", 0x50), ("h", "Ctrl+Alt+H", 0x48) };
        static string HideHotkey => Store.Settings.TryGetValue("hideHotkey", out var v) && v is string s ? s : "p";
        static string HotkeyLabel => HotkeyChoices.FirstOrDefault(h => h.id == HideHotkey).label;
        bool userHidden, hotkeyFailed;
        void RegisterHideHotKey()
        {
            if (hwnd == IntPtr.Zero) return;
            UnregisterHotKey(hwnd, HotKeyId);
            hotkeyFailed = false;
            var choice = HotkeyChoices.FirstOrDefault(h => h.id == HideHotkey);
            if (choice.id == null) return;
            hotkeyFailed = !RegisterHotKey(hwnd, HotKeyId, 0x0001 | 0x0002 | 0x4000, choice.vk);   // Alt | Ctrl | 不連發
            if (hotkeyFailed) Store.Log($"快捷鍵 {choice.label} 註冊失敗（被別的程式佔用了）");
        }
        IntPtr HotKeyHook(IntPtr h, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == 0x0312 && wParam.ToInt32() == HotKeyId) { SetUserHidden(!userHidden); handled = true; }   // WM_HOTKEY
            return IntPtr.Zero;
        }
        void SetUserHidden(bool hidden)
        {
            userHidden = hidden;
            if (!hidden) { fullscreenOverride = true; hiddenForFullscreen = false; }
            SetConcealed(hidden);
            if (!hidden) RefreshCodexQuota(CodexWhy.Tick);   // 藏著時沒查：叫回來補一次（自帶間隔，不會多問）
        }

        public void Recenter()
        {
            Undock();
            Store.Settings.Remove("position"); Store.SaveSettings();
            RestorePosition();
            if (userHidden) SetUserHidden(false);   // 快捷鍵藏起來的：一起恢復（包括動畫）
            fullscreenOverride = true; hiddenForFullscreen = false;   // 你主動叫她：全屏時也出來，前臺視窗換了再按規則藏
            SetConcealed(false);
        }
        bool fullscreenOverride;
    }
}
