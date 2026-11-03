import Carbon
import Cocoa
import ServiceManagement
import WebKit

extension App {
    // MARK: 接入 AI（設定視窗裡）
    // App 裡自帶接入工具（Resources/tools/integrate.py），用系統的 python3 跑；和終端裡的安裝命令做的是同一件事
    static let integrationTargets: [(id: String, label: String, note: String)] = [
        ("claude-mod", "Claude Code 增強版 mod", "能顯示額度；終端裡要 2.1.287 以上，桌面 App 2.1.286 以上"),
        ("claude-hooks", "Claude Code 標準鉤子", "所有版本可用，不顯示額度；和增強版二選一"),
        ("codex", "Codex", "接入後要在 Codex 裡用 /hooks 信任一次"),
        ("deepseek", "DeepSeek Harness", "工作狀態 + 餘額；完全退出再開啟 Harness 生效"),
        ("antigravity", "Antigravity", "桌面版和命令列都有效"),
        ("gemini", "Gemini CLI", "Gemini 桌面 App 沒有介面"),
        ("workbuddy", "WorkBuddy", "按當前模型換角色"),
        ("zcode", "ZCode（智譜）", "按會話的模型換角色"),
        ("hermes", "Hermes Agent", "按當前模型換角色；新開的會話生效"),
        ("cursor", "Cursor", "按當前模型換角色；認不出的模型由當前角色來演"),
        ("qwen", "Qwen Code（阿里）", "按當前模型換角色；新開的會話生效"),
        ("grok", "Grok Build（xAI）", "新開的會話生效；新資料夾第一次用要在 Grok 裡 /hooks-trust 信任一次"),
    ]
    var integrateScript: URL? {
        let url = Bundle.main.resourceURL!.appendingPathComponent("tools/integrate.py")
        return FileManager.default.fileExists(atPath: url.path) ? url : nil
    }

    /// 後臺跑一次接入工具，回撥裡拿到輸出和退出碼
    func runIntegrate(_ args: [String], done: @escaping (String, Int32) -> Void) {
        guard let script = integrateScript else { done("App 裡沒有接入工具", -1); return }
        DispatchQueue.global(qos: .userInitiated).async {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/usr/bin/python3")
            p.arguments = [script.path] + args
            var env = ProcessInfo.processInfo.environment
            env["PYTHONDONTWRITEBYTECODE"] = "1"   // 別往 App 包裡寫快取（會破壞簽名）
            env["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin:/opt/homebrew/bin:/usr/local/bin:" + (env["PATH"] ?? "")
            p.environment = env
            let pipe = Pipe()
            p.standardOutput = pipe; p.standardError = pipe
            var output = ""
            do {
                try p.run()
                output = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
                p.waitUntilExit()
            } catch { output = error.localizedDescription }
            let code = p.isRunning ? -1 : p.terminationStatus
            DispatchQueue.main.async { done(output, code) }
        }
    }

    // MARK: 接入被別的工具沖掉時接回去
    // CC Switch 3.x 切換服務商會整個重寫 ~/.claude/settings.json，CrossPet 的 mod / 鉤子跟著沒了，她就不跟著 Claude 動了。
    // 每 3 秒看一眼這個檔案，有改動就等 2 秒（等對方寫完）跑一次 integrate.py heal：只補 CrossPet 自己的那一項，
    // 在 CrossPet 裡撤銷過的不管。啟動時也看一次（桌寵關著時被沖掉的）。一小時最多接 6 次，免得和別的工具來回打架
    /// 系統自帶的 python3 能直接用（裝了開發者工具）：沒裝時一呼叫就彈「安裝命令列工具」，後臺的自動檢查不能彈窗
    var claudeSettingsURL: URL {
        let dir = ProcessInfo.processInfo.environment["CLAUDE_CONFIG_DIR"].map { URL(fileURLWithPath: $0) }
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".claude")
        return dir.appendingPathComponent("settings.json")
    }
    // MARK: 心跳：各個 AI 最近一次發來訊息的時間
    // 鉤子 / 外掛每收到一條事件就在狀態目錄寫 seen-<名字>（時間戳）。狀態目錄 /tmp 重啟會清空，所以桌寵把讀到的最新時間存進設定。
    // 設定裡據此標出「接上了但從沒收到過訊息」，反饋裡也帶著
    static let seenKeys = ["claude-hooks": "claude", "claude-mod": "claude-mod", "codex": "gpt", "deepseek": "deepseek", "gemini": "gemini",
                           "antigravity": "antigravity", "workbuddy": "workbuddy", "zcode": "zcode", "hermes": "hermes", "qwen": "qwen", "cursor": "cursor", "grok": "grok"]
    var lastSeen: [String: Double] {
        get { (defaults.dictionary(forKey: "seen") as? [String: Double]) ?? [:] }
        set { defaults.set(newValue, forKey: "seen") }
    }
    func refreshSeen() {
        var seen = lastSeen, changed = false
        for key in Set(Self.seenKeys.values) {
            guard let text = try? String(contentsOf: stateDir.appendingPathComponent("seen-\(key)"), encoding: .utf8),
                  let ts = Double(text.trimmingCharacters(in: .whitespacesAndNewlines)), ts > (seen[key] ?? 0) else { continue }
            seen[key] = ts; changed = true
        }
        if changed { lastSeen = seen; if settingsWeb != nil { pushSettings() } }
    }
    static func agoText(_ ts: Double?) -> String {
        guard let ts else { return "從沒收到" }
        let d = Date().timeIntervalSince1970 - ts
        return d >= 86400 ? "\(Int(d / 86400)) 天前" : d >= 3600 ? "\(Int(d / 3600)) 小時前" : d >= 60 ? "\(Int(d / 60)) 分鐘前" : "剛剛"
    }

    func watchClaudeConfig() {
        refreshSeen()
        guard ready, let stamp = (try? FileManager.default.attributesOfItem(atPath: claudeSettingsURL.path))?[.modificationDate] as? Date,
              stamp != claudeConfigStamp else { return }
        claudeConfigStamp = stamp
        healPending?.cancel()
        let work = DispatchWorkItem { [weak self] in self?.healIntegrations() }
        healPending = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 2, execute: work)
    }
    func healIntegrations() {
        healTimes = healTimes.filter { Date().timeIntervalSince($0) < 3600 }
        guard healTimes.count < 6, pythonReady, integrateScript != nil else { return }
        runIntegrate(["heal"]) { [weak self] out, code in
            guard let self, code == 0 else { return }
            let last = out.split(separator: "\n").last.map(String.init) ?? "[]"
            let healed = (try? JSONSerialization.jsonObject(with: Data(last.utf8))) as? [String] ?? []
            guard !healed.isEmpty else { return }
            self.healTimes.append(Date())
            appLog("接回被沖掉的接入 \(healed)")
            self.js("configHealed()")
            self.refreshIntegrated()
        }
    }

    func refreshIntegrated() {
        runIntegrate(["installed"]) { [weak self] out, code in
            guard let self else { return }
            let last = out.split(separator: "\n").last.map(String.init) ?? "[]"
            self.integrated = (try? JSONSerialization.jsonObject(with: Data(last.utf8))) as? [String] ?? []
            self.pushSettings()
        }
        refreshPlugins()
    }

    /// 外掛版本（只讀檢查，不改任何配置）：有新版本 / 更新了還沒重開時，設定頁那一行掛提醒；tellPet：桌寵剛啟動，讓她閒下來時說一句。
    /// 只提醒、不自動更新——改別家配置一律讓使用者自己點「更新」。和 Windows 版 RefreshPlugins 一致
    func refreshPlugins(tellPet: Bool = false) {
        runIntegrate(["plugins"]) { [weak self] out, code in
            guard let self, code == 0, let last = out.split(separator: "\n").last.map(String.init),
                  (try? JSONSerialization.jsonObject(with: Data(last.utf8))) is [Any] else { return }
            DispatchQueue.main.async {
                self.pluginStatus = last
                if tellPet { self.js("setPluginUpdates(\(last))") }
                self.pushSettings()
            }
        }
    }

    func integrate(_ action: String, _ target: String) {
        guard let t = Self.integrationTargets.first(where: { $0.id == target }), ["install", "uninstall"].contains(action) else { return }
        runIntegrate([action, target]) { [weak self] out, code in
            // 沒找到 AI 的配置目錄時指令碼正常退出但什麼都沒改，不能顯示成「已接入」
            let missing = out.contains("沒找到")
            let alert = NSAlert()
            alert.messageText = "\(t.label)：" + (code != 0 ? "沒有完成" : missing ? "沒有接入：沒找到這個 AI 的配置" : action == "install" ? "已接入" : "已撤銷")
            alert.informativeText = out.trimmingCharacters(in: .whitespacesAndNewlines)
                .split(separator: "\n").filter { !$0.contains("已備份") }.joined(separator: "\n")
            alert.alertStyle = code != 0 || missing ? .warning : .informational
            NSApp.activate(ignoringOtherApps: true)
            alert.runModal()
            self?.refreshIntegrated()
        }
    }

    func pushSettings() {
        guard settingsWeb != nil || charactersWeb != nil else { return }
        // 當前角色以桌寵頁面里正在演的為準（啟動時外殼會先從 UserDefaults 恢復上次的角色，見 didFinish）
        web.evaluateJavaScript("charId") { [weak self] r, _ in
            guard let self else { return }
            // 只在還不知道當前角色時（剛啟動、沒存過上次的角色）問頁面；換角色有 0.33 秒過渡，過渡中頁面裡還是舊角色
            if self.currentId == nil, let id = r as? String, self.characterIds.contains(id) { self.currentId = id }
            guard let data = try? JSONSerialization.data(withJSONObject: self.settingsState()),
                  let text = String(data: data, encoding: .utf8) else { return }
            for v in [self.settingsWeb, self.charactersWeb].compactMap({ $0 }) { v.evaluateJavaScript("setState(\(text))", completionHandler: nil) }
        }
    }

    func handleSettings(_ body: Any) {
        guard let msg = body as? [String: Any], let cmd = msg["cmd"] as? String else { return }
        switch cmd {
        case "set":
            guard let key = msg["key"] as? String else { return }
            let value = msg["value"]
            switch key {
            case "size":
                if let n = value as? Double { let v = min(1.6, max(0.6, (n * 100).rounded() / 100)); defaults.set(v, forKey: "size"); applySizes(animateTo: CGFloat(v)) }
            case "textSize":   // 單獨調文字：同時記為解鎖
                if let n = value as? Double {
                    defaults.set(min(1.6, max(0.6, (n * 100).rounded() / 100)), forKey: "textSize")
                    defaults.set(false, forKey: "textLinked")
                    applySizes()
                }
            case "textLinked":
                defaults.set(value as? Bool ?? true, forKey: "textLinked"); applySizes()
            case "showName":
                let on = value as? Bool ?? true; defaults.set(on, forKey: "showName"); js("setShowName(\(on))")
            case "aura":
                if let mode = value as? String, ["auto", "on", "off"].contains(mode) { defaults.set(mode, forKey: "auraMode"); js("setAuraMode(\(quote(mode)))") }
            case "followApps":
                defaults.set(value as? Bool ?? true, forKey: "followApps")
            case "followEvents":
                defaults.set(value as? Bool ?? false, forKey: "followEvents")
            case "hideFullscreen":
                defaults.set(value as? Bool ?? true, forKey: "hideFullscreen"); applyFullscreenBehavior()
            case "eggs":
                let on = value as? Bool ?? true; defaults.set(on, forKey: "eggs"); js("setEggsEnabled(\(on))")
            case "gptQuota":
                if (value as? Bool ?? false) != defaults.bool(forKey: "gptQuota") { toggleGPTQuota() }
            case "agyQuota":
                if (value as? Bool ?? false) != defaults.bool(forKey: "agyQuota") { toggleAntigravityQuota() }
            case "hideHotkey":
                if let v = value as? String { defaults.set(v, forKey: "hideHotkey"); registerHotKey() }
            case "powerSave":
                let on = value as? Bool ?? false
                defaults.set(on, forKey: "powerSave"); js("setPowerSave(\(on))")
            case "quotaAggregate":
                let on = value as? Bool ?? false
                defaults.set(on, forKey: "quotaAggregate"); js("setQuotaAggregate(\(on))")
            case "whatsNewSeen":   // 開啟了「新功能」頁：記為看過，小紅點消失
                if let v = value as? String { defaults.set(v, forKey: "whatsNewSeen") }
            case "codexResets":
                let on = value as? Bool ?? false
                defaults.set(on, forKey: "codexResets"); js("setCodexResets(\(on))")
            case "peakSkins":
                defaults.set(Self.cleanPeakSkins(value), forKey: "peakSkins"); js("setPeakSkins(\(peakSkinsJSON))")
            case "quotaStyle":
                if let s = value as? String, ["text", "bars"].contains(s) { defaults.set(s, forKey: "quotaStyle"); js("setQuotaStyle(\(quote(s)))") }
            case "peakStyle":
                if let s = value as? String, ["bar", "text", "off"].contains(s) { defaults.set(s, forKey: "peakStyle"); js("setPeakStyle(\(quote(s)))") }
            case "dsQuota":
                let on = value as? Bool ?? true
                defaults.set(on, forKey: "dsQuota")
                stamps["deepseek-quota"] = nil   // 重新開啟時馬上讀一次
                if !on { js("setQuota('deepseek', null)") }
            case "edgeDock":
                let on = value as? Bool ?? true
                defaults.set(on, forKey: "edgeDock")
                if !on { undock() }
            case "aiAutostart":
                setAIAutostart(value as? Bool ?? true)
            case "login":
                if (value as? Bool ?? false) != (SMAppService.mainApp.status == .enabled) { toggleLogin() }
            case "character":
                if let id = value as? String, characterIds.contains(id) { switchTo(id, why: "manual", via: "settings") }
            default: break
            }
            pushSettings()
        case "integrate":
            if let target = msg["target"] as? String, let action = msg["action"] as? String { integrate(action, target) }
        case "skin":   // 換衣服：記下來，重新載入立繪；正在顯示的就是她的話馬上換上
            guard let id = msg["id"] as? String, characterIds.contains(id), let skin = msg["skin"] as? String else { return }
            wear(id, skin)
            switchTo(id, why: "skin", via: skin)
            pushSettings()
        case "rename":
            guard let id = msg["id"] as? String, characterIds.contains(id) else { return }
            var names = defaults.dictionary(forKey: "names") as? [String: String] ?? [:]
            let value = (msg["name"] as? String ?? "").trimmingCharacters(in: .whitespaces)
            if value.isEmpty { names.removeValue(forKey: id) } else { names[id] = value }
            defaults.set(names, forKey: "names")
            loadCharacters()
            js("setName(\(quote(id)), \(quote(displayNames[id] ?? id)))")
            pushSettings()
        case "action":
            switch msg["name"] as? String {
            case "update": selfUpdate()
            case "checkUpdate": checkForUpdate(manual: true)
            case "openCharacters": openCharacters()
            case "openCodexResets": NSWorkspace.shared.open(URL(string: "https://codex-resets.com")!)
            case "openData": NSWorkspace.shared.open(root)
            case "openReleases": NSWorkspace.shared.open(URL(string: "https://github.com/DuridMing/crosspet/releases")!)
            case "devConsole": openDevConsole()
            case "feedback": openFeedback()
            case "characterWindow": openCharacterPage()
            default: break
            }
            pushSettings()
        default:   // get：設定視窗剛開啟
            if integrated == nil { refreshIntegrated() }
            pushSettings()
        }
    }

}
