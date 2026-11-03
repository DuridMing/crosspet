using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace CrossPet
{
    /// <summary>目錄、設定、日誌、內建資源同步、角色清單。和 macOS 版 / docs/windows.md 的約定一致。</summary>
    static class Store
    {
        public static readonly string Install = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/');
        public static readonly string Data = Env("CROSSPET_DATA_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrossPet");
        public static readonly string State = Env("CROSSPET_STATE_DIR") ?? Path.Combine(Data, "state");
        public static readonly string Codex = Env("CODEX_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        public static string Characters => Path.Combine(Data, "characters");
        public static string Feedback => Path.Combine(Data, "dev-feedback.md");
        public static string AppMap => Path.Combine(Data, "windows-apps.json");
        public static string Python => Path.Combine(Install, "python", "python.exe");
        public static string Version => ReadText(Path.Combine(Install, "VERSION"))?.Trim() ?? "0";

        // 網頁從兩個虛擬域名讀檔案：程式自帶的網頁 https://crosspet.app/（安裝目錄\web，資料夾對映）
        // 和角色立繪 https://crosspet.data/characters/…（資料目錄，使用者可以改；程式自己讀檔案交出去，見 SpriteResources）
        public const string AppHost = "crosspet.app", DataHost = "crosspet.data";
        public static string PageUrl(string page) => $"https://{AppHost}/{page}";

        public static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        static string Env(string name) { var v = Environment.GetEnvironmentVariable(name); return string.IsNullOrWhiteSpace(v) ? null : v; }
        public static string ReadText(string path) { try { return File.ReadAllText(path); } catch { return null; } }

        public static Dictionary<string, object> ReadJson(string path)
        {
            var text = ReadText(path);
            if (text == null) return null;
            try { return Json.DeserializeObject(text.TrimStart('﻿')) as Dictionary<string, object>; } catch { return null; }
        }

        public static void WriteJson(string path, object data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(data));
            if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
        }

        public static void Log(string text)
        {
            try
            {
                Directory.CreateDirectory(Data);
                var file = Path.Combine(Data, "windows.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000) File.Delete(file);
                File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + text + "\r\n");
            }
            catch { }
        }

        // ---- 設定 ----
        public static Dictionary<string, object> Settings = new Dictionary<string, object>();
        static string SettingsFile => Path.Combine(Data, "settings.json");
        public static void LoadSettings()
        {
            Settings = ReadJson(SettingsFile) ?? new Dictionary<string, object>();
            if (!Settings.ContainsKey("followApps")) Settings["followApps"] = false;   // maid fork：預設不跟著前臺程式換角色
            if (!Settings.ContainsKey("followEvents")) Settings["followEvents"] = false;   // maid fork：預設不跟著 AI 工作事件換角色
            if (!(Settings.TryGetValue("names", out var n) && n is Dictionary<string, object>)) Settings["names"] = new Dictionary<string, object>();
        }
        public static void SaveSettings() { try { WriteJson(SettingsFile, Settings); } catch (Exception e) { Log("儲存設定失敗: " + e.Message); } }
        public static bool Flag(string key) => Settings.TryGetValue(key, out var v) && v is bool b && b;
        public static Dictionary<string, object> Names => (Dictionary<string, object>)Settings["names"];

        /// <summary>
        /// 和 macOS 版一樣：內建角色每次啟動整份覆蓋（這樣更新後能拿到新立繪），使用者自己加的角色資料夾不動；
        /// 鉤子指令碼複製到資料目錄；前臺程式 → 角色的對照表第一次才放，之後使用者改了就留著。
        /// </summary>
        public static void SyncResources()
        {
            Directory.CreateDirectory(Characters);
            Directory.CreateDirectory(State);
            Directory.CreateDirectory(Path.Combine(Data, "events"));   // 鉤子 / 外掛的事件記錄（反饋用）；Claude Code 的 mod 只能寫檔案、不能建資料夾
            var src = Path.Combine(Install, "characters");
            foreach (var dir in Directory.GetDirectories(src))
            {
                var dst = Path.Combine(Characters, Path.GetFileName(dir));
                try { if (Directory.Exists(dst)) Directory.Delete(dst, true); CopyDir(dir, dst); }
                catch (Exception e) { Log("同步角色失敗 " + dir + ": " + e.Message); }
            }
            // 下面這些複製失敗（檔案被別的程式佔著）只記一筆：舊的那份還能用，不能因為一個說明檔案把桌寵攔在啟動前
            // （更新失敗回退後重開舊版本，characters\LICENSE.md 還被佔著，整個啟動就失敗了）
            foreach (var md in Directory.GetFiles(src, "*.md"))
                try { File.Copy(md, Path.Combine(Characters, Path.GetFileName(md)), true); } catch (Exception e) { Log("同步說明檔案失敗 " + md + ": " + e.Message); }
            // 1.3.4 測試版 1 把網頁同步到了 資料目錄\web（後來不這麼做了）：刪掉這份用不上的複製
            try { var web = Path.Combine(Data, "web"); if (Directory.Exists(web)) Directory.Delete(web, true); } catch { }
            var hook = Path.Combine(Install, "integrations", "crosspet-hook.py");
            if (File.Exists(hook))
                try { File.Copy(hook, Path.Combine(Data, "crosspet-hook.py"), true); } catch (Exception e) { Log("同步鉤子指令碼失敗: " + e.Message); }
            // 只更新已經接入過的 Cursor 橋，不給沒裝 Cursor 的使用者新建這份檔案
            var bridge = Path.Combine(Install, "integrations", "cursor", "crosspet-cursor-bridge.py");
            var bridgeCopy = Path.Combine(Data, "crosspet-cursor-bridge.py");
            if (File.Exists(bridge) && File.Exists(bridgeCopy))
                try { File.Copy(bridge, bridgeCopy, true); } catch (Exception e) { Log("更新 Cursor 橋接指令碼失敗: " + e.Message); }
            if (!File.Exists(AppMap)) File.Copy(Path.Combine(Install, "apps.json"), AppMap);
            else
            {
                // 新版本加的程式補進使用者的對照表；使用者改過、刪成空值的不動
                var mine = ReadJson(AppMap);
                var bundled = ReadJson(Path.Combine(Install, "apps.json"));
                if (mine != null && bundled != null)
                {
                    var added = bundled.Keys.Where(k => !mine.Keys.Any(m => string.Equals(m, k, StringComparison.OrdinalIgnoreCase))).ToList();
                    foreach (var k in added) mine[k] = bundled[k];
                    if (added.Count > 0) try { WriteJson(AppMap, mine); } catch (Exception e) { Log("更新程式對照表失敗: " + e.Message); }
                }
            }
        }

        static void CopyDir(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(from))
                if (!string.Equals(Path.GetFileName(d), "raw", StringComparison.OrdinalIgnoreCase)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
        }

        /// <summary>掃描 characters/&lt;id&gt;/：character.json + 立繪；和 macOS 版 loadCharacters 一樣的規則</summary>
        // 皮膚（另一套衣服）：character.json 裡寫 "skinOf": "<角色>"，不單獨算角色；選了它，那個角色就用這套立繪，臺詞、動作邏輯照舊
        public static Dictionary<string, List<(string id, string name)>> SkinChoices = new Dictionary<string, List<(string, string)>>();
        public static Dictionary<string, string> BaseOutfit = new Dictionary<string, string>();   // 原版衣服叫什麼（outfit，比如「女僕裝」）
        public static Dictionary<string, object> Skins => Settings.TryGetValue("skins", out var v) && v is Dictionary<string, object> d ? d : null;

        // 立繪：<檔名去掉 -2、-3> 是姿態名，idle-blink 是眨眼幀（照著 idle 本身畫的）
        static (Dictionary<string, List<string>> poses, string blink, string blinkBase) Sprites(string dir)
        {
            var id = Path.GetFileName(dir);
            var poses = new Dictionary<string, List<string>>();
            string blink = null, blinkBase = null;
            foreach (var f in Directory.GetFiles(dir).Where(f => f.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                                                      .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal))
            {
                var stem = Path.GetFileNameWithoutExtension(f);
                if (!CardImages.IsSprite(stem)) continue;   // 展示立繪只給設定頁的卡片用
                var url = $"https://{DataHost}/characters/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(Path.GetFileName(f))}";
                if (stem == "idle-blink") { blink = url; continue; }
                if (stem == "idle") blinkBase = url;
                var pose = stem.Split('-')[0];
                if (!poses.ContainsKey(pose)) poses[pose] = new List<string>();
                poses[pose].Add(url);
            }
            return (poses, blink, blinkBase);
        }

        public static Dictionary<string, object> Manifest()
        {
            var chars = new Dictionary<string, object>();
            var dirs = Directory.GetDirectories(Characters).OrderBy(d => d, StringComparer.Ordinal).ToList();
            SkinChoices = new Dictionary<string, List<(string, string)>>();
            var skinSprites = new Dictionary<string, (Dictionary<string, List<string>>, string, string)>();
            var skinInfo = new Dictionary<string, Dictionary<string, object>>();
            foreach (var dir in dirs)
            {
                var info = ReadJson(Path.Combine(dir, "character.json"));
                if (info == null || !(info.TryGetValue("skinOf", out var so) && so is string skinOf)) continue;
                var sp = Sprites(dir);
                if (!sp.poses.ContainsKey("idle")) continue;
                var sid = Path.GetFileName(dir);
                if (!SkinChoices.ContainsKey(skinOf)) SkinChoices[skinOf] = new List<(string, string)>();
                SkinChoices[skinOf].Add((sid, info.TryGetValue("skinName", out var sn) && sn is string n ? n : sid));
                skinSprites[sid] = sp;
                skinInfo[sid] = info;
            }
            foreach (var dir in dirs)
            {
                var info = ReadJson(Path.Combine(dir, "character.json"));
                if (info == null || info.ContainsKey("skinOf")) continue;
                var id = Path.GetFileName(dir);
                var (poses, blink, blinkBase) = Sprites(dir);
                if (Skins != null && Skins.TryGetValue(id, out var chosen) && chosen is string skin && skinSprites.TryGetValue(skin, out var alt)
                    && SkinChoices.TryGetValue(id, out var list) && list.Any(x => x.id == skin))
                {
                    (poses, blink, blinkBase) = alt;
                    info["skin"] = skin;   // 網頁據此挑彩蛋（有的彩蛋只在穿某件衣服時出現）
                    // 皮膚可以有自己的臺詞 / 登場招呼：寫了的覆蓋原版，沒寫的照舊
                    if (skinInfo[skin].TryGetValue("lines", out var ol) && ol is Dictionary<string, object> own)
                    {
                        var lines = info.TryGetValue("lines", out var bl) && bl is Dictionary<string, object> b0 ? b0 : new Dictionary<string, object>();
                        foreach (var kv2 in own) lines[kv2.Key] = kv2.Value;
                        info["lines"] = lines;
                    }
                    if (skinInfo[skin].TryGetValue("greeting", out var g)) info["greeting"] = g;
                }
                var egg = info.TryGetValue("egg", out var e) && e is bool b && b;
                if (egg ? !poses.ContainsKey("pop") : !(poses.ContainsKey("idle") || poses.ContainsKey("default"))) continue;
                if (!egg) BaseOutfit[id] = info.TryGetValue("outfit", out var o) ? o as string : null;
                if (!egg && Names.TryGetValue(id, out var custom) && custom is string s && s.Trim() != "") info["name"] = s.Trim();
                info["poses"] = poses; info["blink"] = blink; info["blinkBase"] = blinkBase;
                chars[id] = info;
            }
            return new Dictionary<string, object> { ["characters"] = chars };
        }
    }
}
