using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using CrossPet;

static class Program
{
    static int fails;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }

    static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "crosspet-sprite-test", "characters");
        string file, mime;

        Check("普通立繪", SpritePath.TryResolve(root, "/characters/claude/idle.webp", out file, out mime)
            && file == Path.GetFullPath(Path.Combine(root, "claude", "idle.webp")) && mime == "image/webp");
        Check("png", SpritePath.TryResolve(root, "/characters/gpt/edge_left.png", out file, out mime) && mime == "image/png");
        Check("副檔名大寫", SpritePath.TryResolve(root, "/characters/gpt/IDLE.WEBP", out file, out mime) && mime == "image/webp");
        Check("中文角色名（網址編碼）", SpritePath.TryResolve(root, "/characters/%E5%8F%AF%E8%90%9D/idle.webp", out file, out mime)
            && file == Path.GetFullPath(Path.Combine(root, "可蘿", "idle.webp")));
        Check("檔名裡有空格（%20）", SpritePath.TryResolve(root, "/characters/my%20pet/idle%201.webp", out file, out mime));

        Check("角色目錄是 ..", !SpritePath.TryResolve(root, "/characters/%2E%2E/settings.webp", out file, out mime));
        Check("檔名裡編碼的斜槓", !SpritePath.TryResolve(root, "/characters/claude/..%2F..%2Fsettings.webp", out file, out mime));
        Check("檔名裡編碼的反斜槓", !SpritePath.TryResolve(root, "/characters/claude/..%5C..%5Cx.webp", out file, out mime));
        Check("磁碟機代號冒號", !SpritePath.TryResolve(root, "/characters/claude/C%3Ax.webp", out file, out mime));
        Check("character.json 不給", !SpritePath.TryResolve(root, "/characters/gpt/character.json", out file, out mime));
        Check("資料目錄裡別的檔案不給", !SpritePath.TryResolve(root, "/settings.json", out file, out mime));
        Check("多一層目錄不給", !SpritePath.TryResolve(root, "/characters/a/b/c.webp", out file, out mime));
        Check("空的檔名", !SpritePath.TryResolve(root, "/characters/claude/", out file, out mime));
        Check("結尾是點", !SpritePath.TryResolve(root, "/characters/claude./idle.webp", out file, out mime));
        Check("控制字元", !SpritePath.TryResolve(root, "/characters/claude/a%00.webp", out file, out mime));

        // 藏起來時滑鼠穿透（WS_EX_TRANSPARENT = 0x20）：只動這一位，別的樣式原樣保留
        const int layered = 0x80000, noActivate = 0x08000000, toolWindow = 0x80, transparent = 0x20;
        int ex = layered | noActivate | toolWindow;
        Check("藏起來：加上滑鼠穿透", ConcealStyle.ClickThrough(ex, true) == (ex | transparent));
        Check("叫回來：去掉滑鼠穿透", ConcealStyle.ClickThrough(ex | transparent, false) == ex);
        Check("重複藏不會出錯", ConcealStyle.ClickThrough(ConcealStyle.ClickThrough(ex, true), true) == (ex | transparent));
        Check("沒藏時叫回來不變", ConcealStyle.ClickThrough(ex, false) == ex);

        // Gemini（Antigravity）額度排版，樣本和 macOS 的 tests/antigravity_quota_probe.sh 共用
        string Agy(string name)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "../../../../../fixtures/antigravity", name + ".json");
            var q = AntigravityQuota.Parse(Js(JsonDocument.Parse(File.ReadAllText(path)).RootElement));
            return q == null ? "null" : q["text"] + " | low=" + q["low"];
        }
        string AgyBars(string name)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "../../../../../fixtures/antigravity", name + ".json");
            var q = AntigravityQuota.Parse(Js(JsonDocument.Parse(File.ReadAllText(path)).RootElement));
            return q == null || !q.ContainsKey("bars") ? "null" : JsonSerializer.Serialize(q["bars"], new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        var a1 = Agy("ultra-only-model");
        Check("Gemini：不可用的模型不算進額度（" + a1 + "）", a1.StartsWith("Gemini 剩餘 100%") && a1.Contains(" ｜ Claude/GPT 剩餘 100% |") && a1.EndsWith("low=False"));
        var a2 = Agy("gemini-used-up");
        Check("Gemini：真用完了還是 0%（" + a2 + "）", a2.StartsWith("Gemini 剩餘 0%") && a2.Contains(" ｜ Claude/GPT 剩餘 100% |") && a2.EndsWith("low=True"));
        var a3 = Agy("two-pools-with-unavailable");
        Check("Gemini：兩個池 + 不可用模型（" + a3 + "）", a3.StartsWith("Gemini 剩餘 52%") && a3.Contains(" ｜ Claude/GPT 剩餘 99% |") && !a3.Contains(" 0%") && a3.EndsWith("low=False"));

        var s1 = Agy("summary-live");
        Check("Gemini 彙總：兩組各一行，98%（" + s1 + "）", s1.StartsWith("Gemini 剩餘 100%") && s1.Contains(" ｜ Claude/GPT 剩餘 98% · ") && s1.EndsWith("low=False"));
        var s2 = Agy("summary-gemini-used-up");
        Check("Gemini 彙總：比例被省略算 0%（" + s2 + "）", s2.StartsWith("Gemini 剩餘 0%") && s2.EndsWith("low=True"));
        var s3 = Agy("summary-two-windows");
        Check("Gemini 彙總：一組兩個視窗只留剩餘最少的（" + s3 + "）", s3.StartsWith("Gemini 今日 剩餘 40%") && !s3.Contains("本週") && s3.Contains(" ｜ Claude/GPT 剩餘 98% · "));
        var s4 = Agy("summary-multi-pool");
        Check("Gemini 彙總：獨立額度池保留名字，每組一行（" + s4 + "）", s4.StartsWith("Gemini 本週 剩餘 99% · ") && s4.Contains(" ｜ Gemini 4 Argon 剩餘 46%") && s4.Split('｜').Length == 3 && !s4.Contains("Five Hour"));
        var s5 = Agy("summary-5h-tightest");
        Check("Gemini 彙總：5h 最緊時寫成「5小時」（" + s5 + "）", s5.StartsWith("Gemini 5小時 剩餘 30%") && !s5.Contains("本週") && s5.Contains(" ｜ Claude/GPT 剩餘 100% · "));
        var s6 = Agy("summary-tie-5h-first"); var s7 = Agy("summary-tie-weekly-first");
        Check("Gemini 彙總：兩視窗一樣緊，顯示 5小時 和重置時間，不看介面順序（" + s6 + "）", s6.StartsWith("Gemini 5小時 剩餘 50% · ") && s7.StartsWith("Gemini 5小時 剩餘 50% · "));
        var s8 = Agy("summary-weekly-low");
        Check("Gemini 彙總：週額度 ≤10% 不被 5h 蓋住（" + s8 + "）", s8.StartsWith("Gemini 本週 剩餘 5% · ") && s8.EndsWith("low=True"));
        var s9 = Agy("summary-all-50");
        Check("Gemini 彙總：每一行都帶重置時間（" + s9 + "）", s9.Split(new[] { " ｜ " }, StringSplitOptions.None).Length == 3 && s9.Split(new[] { " ｜ " }, StringSplitOptions.None).All(seg => seg.Contains(" 重置")));
        var g1 = AgyBars("summary-multi-pool");
        Check("Gemini bars：每個視窗一項，池名 + 視窗名，同一個池挨著排（" + g1 + "）",
            g1.IndexOf("\"label\":\"Gemini 5小時\"") < g1.IndexOf("\"label\":\"Gemini 本週\"") && g1.IndexOf("\"label\":\"Gemini 本週\"") < g1.IndexOf("\"label\":\"Argon 本週\"")
            && g1.IndexOf("\"label\":\"Argon 本週\"") < g1.IndexOf("\"label\":\"Claude/GPT 5小時\"") && g1.Contains("\"group\":\"Argon\"") && g1.Split(new[] { "\"label\"" }, StringSplitOptions.None).Length == 6 && g1.Contains("\"reset\":17"));
        var g2 = AgyBars("summary-no-reset");
        Check("Gemini bars：沒有重置時間就不寫 reset（" + g2 + "）", g2 == "[{\"label\":\"Gemini 本週\",\"left\":0.7,\"group\":\"Gemini\"}]");
        Check("Gemini bars：舊介面（使用者狀態）沒有 bars", AgyBars("two-pools-with-unavailable") == "null");
        Check("Gemini 彙總：空的不顯示", Agy("summary-empty") == "null");

        // 網頁大小核對（只記日誌不修正）：量兩次、兩次一致且和畫布差 2% 以上才算對不上——拖滑塊時網頁還沒排完，量到的是半路的數
        Check("網頁大小：對得上不記", !ZoomCheck.Off(260, 362, 260, 362, 260, 362));
        Check("網頁大小：差 1% 以內不記（取整誤差）", !ZoomCheck.Off(260, 362, 258.5, 360, 258.5, 360));
        Check("網頁大小：兩次都偏小且一致才記", ZoomCheck.Off(260, 362, 208, 289.6, 208, 289.6));
        Check("網頁大小：兩次量的不一樣（還在變）不記", !ZoomCheck.Off(488, 477, 594, 531, 415, 408));
        Check("網頁大小：量到的數不對勁（0 或負）不記", !ZoomCheck.Off(260, 362, 0, 0, 0, 0));
        // 系統縮放被算了兩遍（反饋：125%、WebView2 155，網頁 224×298 應為 280×372）：按偏差把網頁縮放調回來；別的偏差不動
        bool Near(double? v, double want) => v.HasValue && Math.Abs(v.Value - want) < 0.005;
        Check("縮放修正：偏差正好是系統縮放 → 修正 0.8", Near(ZoomCheck.Fix(280, 372, 224, 298, 1.25, 1), 0.8));
        Check("縮放修正：150% 時偏差 1/1.5 → 修正 0.667", Near(ZoomCheck.Fix(280, 372, 186.7, 248, 1.5, 1), 0.667));
        Check("縮放修正：對得上不修", ZoomCheck.Fix(280, 372, 280, 372, 1.25, 1) == null);
        Check("縮放修正：寬高偏得不一樣（拖滑塊半路）不修", ZoomCheck.Fix(488, 477, 594, 531, 1.25, 1) == null);
        Check("縮放修正：100% 時偏小 0.8 不像算兩遍，不修", ZoomCheck.Fix(280, 372, 224, 298, 1.0, 1) == null);
        Check("縮放修正：修過以後又好了（偏大回去）→ 撤銷成 1", Near(ZoomCheck.Fix(280, 372, 350, 465, 1.25, 0.8), 1));
        Check("縮放修正：量到的數不對勁不修", ZoomCheck.Fix(280, 372, 0, 0, 1.25, 1) == null);
        // #28：換縮放後網頁區域還停在舊縮放的畫素上（虛擬顯示器 + 混合顯示卡）：偏差正好是「舊縮放 ÷ 新縮放」
        Check("舊縮放：150%→200% 網頁 195×271（應為 260×362）認得出", ZoomCheck.StuckAtOldDpi(260, 362, 195, 271, 1.5, 2.0));
        Check("舊縮放：200%→150% 網頁 346×483 認得出", ZoomCheck.StuckAtOldDpi(260, 362, 346, 483, 2.0, 1.5));
        Check("舊縮放：對齊好了不算", !ZoomCheck.StuckAtOldDpi(260, 362, 260, 362, 1.5, 2.0));
        Check("舊縮放：偏差不是這個比例不算", !ZoomCheck.StuckAtOldDpi(260, 362, 208, 289.6, 1.5, 2.0));
        Check("舊縮放：寬高偏得不一樣不算", !ZoomCheck.StuckAtOldDpi(260, 362, 195, 300, 1.5, 2.0));
        Check("舊縮放：縮放沒變不算", !ZoomCheck.StuckAtOldDpi(260, 362, 260, 362, 1.5, 1.5));
        Check("舊縮放：這兩個比例老的「算兩遍」修正認不出（1.3.8-beta.1 實測沒觸發）", ZoomCheck.Fix(260, 362, 195, 271, 2.0, 1) == null && ZoomCheck.Fix(260, 362, 346, 483, 1.5, 1) == null);

        // 主執行緒必須是 STA：WPF 和 WebView2 都要求。1.3.7-test 裡 [STAThread] 被新加的 InstallComplete 隔開、落到了它頭上，
        // Main 變成 MTA，WebView2 一初始化就報 RPC_E_CHANGED_MODE（「無法在設定執行緒模式後對其加以更改」），桌寵打不開
        string FindUp(string rel) { for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) { var f = Path.Combine(d.FullName, rel); if (File.Exists(f)) return f; } return null; }
        var programCs = FindUp(Path.Combine("app", "windows", "src", "Program.cs"));
        Check("啟動：找得到 Program.cs", programCs != null);
        if (programCs != null)
            Check("啟動：[STAThread] 緊挨著 static void Main", System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(programCs), @"\[STAThread\]\s*static\s+void\s+Main\s*\("));

        // 安裝是否完整（#24：一鍵更新刪到一半，characters、integrations 沒了，之後每次啟動都崩）
        var icRoot = Path.Combine(Path.GetTempPath(), "crosspet-installcheck-" + Guid.NewGuid().ToString("N"));
        string Make(string dir, params string[] parts) { Directory.CreateDirectory(dir); foreach (var x in parts) Directory.CreateDirectory(Path.Combine(dir, x)); return dir; }
        var full = Make(Path.Combine(icRoot, "full"), "characters", "web", "integrations", "tools", "python");
        Check("安裝檢查：齊全的不缺", InstallCheck.Missing(full).Count == 0);
        var half = Make(Path.Combine(icRoot, "half"), "web", "tools", "python");
        Check("安裝檢查：刪到一半的列出缺了哪些", string.Join(",", InstallCheck.Missing(half)) == "characters,integrations");
        Check("安裝檢查：目錄不存在時全缺", InstallCheck.Missing(Path.Combine(icRoot, "nope")).Count == 5);
        var cache = Path.Combine(icRoot, "CrossPet-update");
        Check("修復包：沒有快取時找不到", InstallCheck.RepairPackage(cache) == null);
        var oldPkg = Make(Path.Combine(cache, "v1.3.3", "CrossPet"), "characters", "web", "integrations", "tools", "python");
        File.WriteAllText(Path.Combine(oldPkg, "install.ps1"), "");
        var badPkg = Make(Path.Combine(cache, "v1.3.6", "CrossPet"), "web");   // 下載 / 解壓沒完成的
        File.WriteAllText(Path.Combine(badPkg, "install.ps1"), "");
        var newPkg = Make(Path.Combine(cache, "v1.3.4", "CrossPet"), "characters", "web", "integrations", "tools", "python");
        File.WriteAllText(Path.Combine(newPkg, "install.ps1"), "");
        Check("修復包：挑版本最新的完整安裝包（不完整的跳過）", InstallCheck.RepairPackage(cache) == newPkg);
        // 從哪兒開啟的（#28）：解壓出來的安裝包問要不要安裝；還在壓縮包裡說先解壓；開發時的編譯目錄照常啟動
        var pkgDir = Make(Path.Combine(icRoot, "Downloads", "CrossPet"), "characters", "web", "integrations", "tools", "python");
        File.WriteAllText(Path.Combine(pkgDir, "install.ps1"), "");
        Check("開啟位置：就在安裝目錄", InstallCheck.Locate(full, full + "\\") == InstallCheck.Place.Installed);
        Check("開啟位置：解壓出來的完整安裝包", InstallCheck.Locate(pkgDir, full) == InstallCheck.Place.Package);
        Check("開啟位置：壓縮包裡直接雙擊", InstallCheck.Locate(Path.Combine(icRoot, "Temp1_CrossPet-Windows.zip", "CrossPet"), full) == InstallCheck.Place.InsideZip);
        Check("開啟位置：編譯目錄（沒有 install.ps1）照常啟動", InstallCheck.Locate(half, full) == InstallCheck.Place.Other);
        // 寧可漏問也不誤判：裝好的那份路徑和算出來的安裝目錄對不上時（AppData 重定向、目錄連結……），只要叫 …\Programs\CrossPet 就算裝好了
        var redirected = Make(Path.Combine(icRoot, "OneDrive", "AppData", "Programs", "CrossPet"), "characters", "web", "integrations", "tools", "python");
        File.WriteAllText(Path.Combine(redirected, "install.ps1"), "");
        Check("開啟位置：安裝目錄對不上，但本身是 Programs\\CrossPet → 當作裝好了", InstallCheck.Locate(redirected, full) == InstallCheck.Place.Installed);
        Check("開啟位置：大小寫不同也算", InstallCheck.Locate(Path.Combine(icRoot, "x", "programs", "crosspet"), full) == InstallCheck.Place.Installed);
        try { Directory.Delete(icRoot, true); } catch { }

        // 「跟著 AI」取消置頂時插到誰下面（1.3.7 使用者反饋：右鍵幾次 / 換角色後她不見了——前臺是桌面時被插到了桌布下面）
        Check("層級：前臺是普通程式，插到它下面", ZOrderRule.TuckBelow("Chrome_WidgetWin_1", false));
        Check("層級：前臺是桌面（Progman），不插", !ZOrderRule.TuckBelow("Progman", false));
        Check("層級：前臺是桌面（WorkerW），不插", !ZOrderRule.TuckBelow("WorkerW", false));
        Check("層級：前臺是工作列，不插", !ZOrderRule.TuckBelow("Shell_TrayWnd", false) && !ZOrderRule.TuckBelow("Shell_SecondaryTrayWnd", false));
        Check("層級：前臺是 CrossPet 自己（右鍵選單、設定視窗），不插", !ZOrderRule.TuckBelow("WindowsForms10.Window.808.app.0.1", true));
        Check("層級：取不到類名，不插", !ZOrderRule.TuckBelow("", false) && !ZOrderRule.TuckBelow(null, false));
        // 記錄檔案（反饋用）：一行一個 JSON，超過 240 行截回 200 行（docs/架构.md「记录」）
        var line = Ledger.Line(new Dictionary<string, object> { ["char"] = "gpt", ["ev"] = "Pre\"Tool\nUse", ["ok"] = true, ["n"] = 3 });
        var parsed = JsonDocument.Parse(line).RootElement;
        Check("記錄：一行能被解析、欄位齊（含 t）", parsed.GetProperty("char").GetString() == "gpt" && parsed.GetProperty("ev").GetString() == "Pre\"Tool\nUse"
            && parsed.GetProperty("ok").GetBoolean() && parsed.GetProperty("n").GetInt32() == 3 && parsed.GetProperty("t").GetDouble() > 1_700_000_000);
        var ledgerFile = Path.Combine(Path.GetTempPath(), "crosspet-ledger-" + Guid.NewGuid().ToString("N"), "pet.jsonl");
        for (int i = 0; i < 245; i++) Ledger.Append(ledgerFile, new Dictionary<string, object> { ["i"] = i });
        var rows = File.ReadAllLines(ledgerFile).Where(l => l.Trim() != "").ToList();
        Check("記錄：245 次後在 200–240 行、最後一行是最後寫的", rows.Count >= 200 && rows.Count <= 240 && JsonDocument.Parse(rows.Last()).RootElement.GetProperty("i").GetInt32() == 244);
        Check("記錄：讀最後 n 行", Ledger.Tail(ledgerFile, 3).Split('\n').Length == 3 && Ledger.Tail(Path.Combine(Path.GetTempPath(), "nope.jsonl"), 3) == "");
        // 「跟著 AI」時要不要置頂（loki 實測：開 Chrome 再按 Win+D，桌面把不置頂的她一起蓋住了，一直不出來）
        Check("置頂：始終在最上層", ZOrderRule.Top("always", false, false));
        Check("置頂：普通視窗模式從不置頂", !ZOrderRule.Top("normal", true, true));
        Check("置頂：跟著 AI，AI 在前臺或剛乾完活", ZOrderRule.Top("ai", true, false));
        Check("置頂：跟著 AI，普通程式在前臺時不置頂（不擋你）", !ZOrderRule.Top("ai", false, false));
        Check("置頂：跟著 AI，前臺是桌面 / 工作列時置頂（沒在用程式，擋不著誰）", ZOrderRule.Top("ai", false, true));
        var rn = new DateTime(2026, 10, 21, 4, 0, 0, DateTimeKind.Utc);
        var r1 = new RenderRestartState(0, rn).Next(rn);
        Check("渲染恢復：首次失敗允許第 1 次重啟", r1?.Attempts == 1);
        var r2 = r1.Next(rn.AddSeconds(10));
        Check("渲染恢復：十秒後又失敗允許第 2 次，不留凍住例項", r2?.Attempts == 2);
        var r3 = r2?.Next(rn.AddSeconds(20));
        Check("渲染恢復：第三次連續失敗仍允許重啟", r3?.Attempts == 3);
        Check("渲染恢復：第 4 次失敗停止自動重啟", r3 != null && r3.Next(rn.AddSeconds(30)) == null);
        var capped = new RenderRestartState(3, rn);
        Check("渲染恢復：十分鐘內預算仍耗盡", capped.Next(rn.AddMinutes(10).AddTicks(-1)) == null);
        Check("渲染恢復：十分鐘邊界允許新一輪", capped.Next(rn.AddMinutes(10))?.Attempts == 1);
        Check("渲染恢復：時鐘倒退不繞過預算", capped.Next(rn.AddSeconds(-1)) == null);
        Check("渲染恢復：舊時間戳檔案遷移為一次失敗", RenderRestartState.Parse("2026-10-21T12:00:00", rn).Next(rn.AddSeconds(5))?.Attempts == 2);
        Check("渲染恢復：新版記錄往返保持 UTC 和次數", RenderRestartState.Parse(capped.Serialize(), rn.AddDays(1)).Serialize() == capped.Serialize());
        Check("渲染恢復：損壞記錄按一次失敗接續", RenderRestartState.Parse("bad", rn).Next(rn)?.Attempts == 2);
        Check("渲染恢復：無歷史次數時不繼承失敗預算", new RenderRestartState(0, rn).Next(rn.AddMinutes(2))?.Attempts == 1);
        // 角色頁卡片圖：portrait 不算姿態；卡片圖只給存在的檔案（設定頁據此退回待機圖 / 色塊）
        Check("卡片圖：portrait、portrait-card、avatar 不算姿態", !CardImages.IsSprite("portrait") && !CardImages.IsSprite("portrait-card") && !CardImages.IsSprite("avatar") && CardImages.IsSprite("idle") && CardImages.IsSprite("happy-2"));
        var cardRoot = Path.Combine(Path.GetTempPath(), "crosspet-cards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(cardRoot, "deepseek"));
        foreach (var f in new[] { "idle.webp", "idle-blink.webp", "portrait.webp", "avatar.png", "happy.webp" }) File.WriteAllText(Path.Combine(cardRoot, "deepseek", f), "");
        Directory.CreateDirectory(Path.Combine(cardRoot, "mine"));
        File.WriteAllText(Path.Combine(cardRoot, "mine", "default.png"), "");
        var ci = CardImages.Of(cardRoot, "deepseek", "crosspet.data");
        Check("卡片圖：有展示圖、頭像和待機圖", ci.Count == 3 && (string)ci["avatar"] == "https://crosspet.data/characters/deepseek/avatar.png" && (string)ci["portrait"] == "https://crosspet.data/characters/deepseek/portrait.webp" && (string)ci["idle"] == "https://crosspet.data/characters/deepseek/idle.webp");
        Check("卡片圖：只有 default 的自定義角色什麼都不給", CardImages.Of(cardRoot, "mine", "crosspet.data").Count == 0);
        Check("卡片圖：資料夾不存在也不出錯", CardImages.Of(cardRoot, "nobody", "crosspet.data").Count == 0);
        Directory.Delete(cardRoot, true);
        Check("縮放：#28 的 200% 下 195×271 是 0.75 倍，不冒充重複 DPI 的 0.5 倍", ZoomCheck.Off(260, 362, 195, 271, 195, 271)
            && ZoomCheck.Fix(260, 362, 195, 271, 2, 1) == null);
        Console.WriteLine(fails == 0 ? "全部通過" : $"{fails} 項失敗");
        return fails == 0 ? 0 : 1;
    }

    // 把 JSON 轉成和 Windows 上 JavaScriptSerializer 一樣的形狀：物件 → Dictionary，陣列 → object[]，整數 → int，小數 → decimal
    static object Js(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Js(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Select(Js).ToArray(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt32(out var i) ? (object)i : e.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
