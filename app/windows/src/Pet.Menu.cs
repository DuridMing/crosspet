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
        // ---------------- 選單 ----------------
        void FillMenu(Forms.ContextMenuStrip menu, bool developer)
        {
            menu.Items.Clear();
            Forms.ToolStripMenuItem Item(string text, Action click, bool? check = null)
            {
                var i = new Forms.ToolStripMenuItem(text);
                if (check.HasValue) i.Checked = check.Value;
                i.Click += (_, __) => { try { click(); } catch (Exception e) { Store.Log("選單出錯: " + e); } };
                return i;
            }
            void Sep() => menu.Items.Add(new Forms.ToolStripSeparator());

            if (latestRelease is var (tag, url))
            {
                if (updateZip != null) menu.Items.Add(Item(updating ? $"⬆️ 正在更新到 {tag}…" : $"⬆️ 更新到 {tag}", () => _ = SelfUpdate()));
                else menu.Items.Add(Item($"⬆️ 有新版本 {tag}，點這裡下載", () => Open(url)));
                Sep();
            }
            menu.Items.Add(Item("摸摸頭", () => Js("handleClick('pat')")));
            menu.Items.Add(Item("戳一下", () => Js("handleClick('poke')")));
            menu.Items.Add(Item("召喚彩蛋", () => Js("playEgg(true)")));
            if (CodexResets && current == "gpt") menu.Items.Add(Item("會重置嗎？", () => Js("askReset()")));   // 只有 GPT 有這個需求
            Sep();
            // 角色和衣服多了，名單一長串要翻半天：改成開啟設定的角色頁（卡片牆），看著圖選
            if (Characters().Count > 1) { menu.Items.Add(Item("換角色…", OpenCharacterWindow)); Sep(); }
            var layer = new Forms.ToolStripMenuItem("顯示層級");
            foreach (var (mode, title) in new[] { ("always", "始終在最上層"), ("ai", "跟著 AI（用 AI 時才浮在最上層）"), ("normal", "普通視窗（不置頂）") })
            {
                var m = mode;
                layer.DropDownItems.Add(Item(title, () => { Store.Settings["layer"] = m; Store.SaveSettings(); ApplyLayer(); }, LayerMode == m));
            }
            menu.Items.Add(layer);
            menu.Items.Add(Item("設定…", () => OpenSettings()));
            if (developer)
            {
                menu.Items.Add(Item("開發者控制台…", OpenDev));
                menu.Items.Add(Item("測試渲染失敗並重啟…", () =>
                {
                    if (MessageBox.Show("這會主動觸發渲染失敗的恢復流程，桌寵及設定視窗會關閉，然後嘗試自動重開。是否繼續？",
                        "CrossPet 開發測試", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                        Program.InjectRenderFailure();
                }));
            }
            menu.Items.Add(Item("回到右下角", Recenter));
            menu.Items.Add(Item(HotkeyLabel != null ? $"暫時隱藏（{HotkeyLabel}）" : "暫時隱藏", () => SetUserHidden(true)));
            Sep();
            menu.Items.Add(Item("退出 CrossPet", UserQuit));
        }
    }
}
