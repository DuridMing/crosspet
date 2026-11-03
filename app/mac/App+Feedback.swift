import Carbon
import Cocoa
import ServiceManagement
import WebKit

extension App {
    // MARK: 反饋問題
    // 右鍵「反饋問題…」：web/feedback.html 裡點選「哪方面」+ 必填一句「具體是哪裡」，App 附上版本、系統、接入情況、最近日誌，
    // 由頁面拼成卡片，發到飛書群機器人。地址可用 `defaults write io.github.crosspet feedbackURL <地址>` 改（測試用）
    static let feedbackEndpoint = "https://open.feishu.cn/open-apis/bot/v2/hook/854f8a9a-d3f4-4705-b6f8-15cc007f714b"

    @objc func openFeedback() {
        if let w = feedbackWindow {
            NSApp.activate(ignoringOtherApps: true)
            w.makeKeyAndOrderFront(nil)
            return
        }
        let config = WKWebViewConfiguration()
        config.userContentController.add(DevBridge(app: self, kind: .feedback), name: "feedback")
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: Self.fitHeight(700)),
                         styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        w.title = "CrossPet 反饋問題"
        w.isReleasedWhenClosed = false
        w.center()
        let v = WKWebView(frame: w.contentView!.bounds, configuration: config)
        v.autoresizingMask = [.width, .height]
        v.navigationDelegate = self
        v.loadFileURL(root.appendingPathComponent("web/feedback.html"), allowingReadAccessTo: root)
        w.contentView!.addSubview(v)
        feedbackWindow = w
        feedbackWeb = v
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: w, queue: .main) { [weak self] _ in
            self?.feedbackWeb = nil
            self?.feedbackWindow = nil
        }
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
    }

    /// 每臺機器一個隨機 id，只用來讓服務端限流、合併同一個人的多條反饋
    var installId: String {
        if let id = defaults.string(forKey: "installId") { return id }
        let id = UUID().uuidString
        defaults.set(id, forKey: "installId")
        return id
    }

    /// 去掉日誌裡的隱私：家目錄換成 ~，路徑裡的使用者名稱、像 API Key 的字串打碼，順便去掉終端顏色控制符
    static func redact(_ text: String) -> String {
        var s = text.replacingOccurrences(of: NSHomeDirectory(), with: "~")
        let rules: [(String, String)] = [
            (#"\x{1B}\[[0-9;]*[A-Za-z]"#, ""),                       // 終端顏色控制符
            (#"(/Users/|\\Users\\)[^/\\\s]+"#, "$1<user>"),        // 別的使用者目錄裡的使用者名稱
            (#"sk-[A-Za-z0-9_\-]{8,}"#, "sk-***"),
            (#"(?i)(bearer|token|key|secret|password)([\s\"':=]+)[A-Za-z0-9_\-\.]{8,}"#, "$1$2***"),
        ]
        for (pattern, tmpl) in rules {
            if let re = try? NSRegularExpression(pattern: pattern) {
                s = re.stringByReplacingMatches(in: s, range: NSRange(s.startIndex..., in: s), withTemplate: tmpl)
            }
        }
        return s
    }

    /// 反饋視窗第一步就要的：開頭、第二步的選項（已接入的 AI、角色、開啟了的額度）；sections 是這一類要附的段落（見 web/feedback-plan.js）
    func feedbackFacts(_ sections: [String: String], key: String? = nil) -> [String: Any] {
        var arch = "arm64"
        #if arch(x86_64)
        arch = "x86_64"
        #endif
        let skins = (defaults.dictionary(forKey: "skins") as? [String: String]) ?? [:]
        var quotas: [String] = []
        if defaults.bool(forKey: "gptQuota") { quotas.append("gpt") }
        if defaults.bool(forKey: "agyQuota") { quotas.append("gemini") }
        if dsQuotaEnabled { quotas.append("deepseek") }
        let header: [String: Any] = ["version": currentVersion, "platform": "mac", "os": "macOS \(ProcessInfo.processInfo.operatingSystemVersionString) \(arch)",
                                     "install": installId, "character": currentId ?? "", "skin": skins[currentId ?? ""] ?? "",
                                     "uptimeMin": Int(Date().timeIntervalSince(launchedAt) / 60)]
        let characters: [[String: String]] = characterIds.map { ["id": $0, "name": displayNames[$0] ?? $0] }
        var facts: [String: Any] = ["header": header, "integrations": integrated ?? [], "characters": characters, "quotas": quotas,
                                    "sections": sections.mapValues { Self.redact($0) }]
        if let key { facts["key"] = key }   // 收集的結果帶回 collect 時的 key，頁面據此丟掉上一類遲到的結果
        return facts
    }

    static func clock(from d: Date = Date()) -> String { let f = DateFormatter(); f.dateFormat = "MM-dd HH:mm"; return f.string(from: d) }
    static func hm(_ d: Date) -> String { let f = DateFormatter(); f.dateFormat = "HH:mm"; return f.string(from: d) }

    /// 收集反饋要的段落（want 來自 web/feedback-plan.js 的 sectionsFor）。網頁自報和接入診斷是非同步的：網頁 2 秒不回就寫「網頁沒有回應」
    func collectFeedback(_ want: [String], done: @escaping ([String: String]) -> Void) {
        var out: [String: String] = [:]
        let group = DispatchGroup()
        let fm = FileManager.default
        func file(_ url: URL) -> String { (try? String(contentsOf: url, encoding: .utf8)) ?? "" }
        for id in want {
            switch id {
            case "window":
                let f = panel.frame
                let onScreen = NSScreen.screens.contains { $0.visibleFrame.intersects(f.insetBy(dx: 40, dy: 40)) }
                out[id] = "位置 \(Int(f.minX)),\(Int(f.minY)) \(Int(f.width))×\(Int(f.height))\(onScreen ? "" : "（不在任何螢幕裡）")；顯示中 \(panel.isVisible ? "是" : "否")，透明度 \(panel.alphaValue)；"
                    + "層級 \(layerMode)（現在\(panel.level == .floating ? "置頂" : "普通")）；貼邊 \(dock ?? "無")\(dock != nil ? (dockOut ? "·跑出來了" : "·縮著") : "")；大小 \(Int(petScale * 100))%"
            case "displays": out[id] = displaysSummary()
            case "dpi": out[id] = displaysSummary() + "\n人物大小 \(Int(petScale * 100))%，文字大小 \(Int(textScale * 100))%"
            case "front":
                let front = NSWorkspace.shared.frontmostApplication
                out[id] = "前臺 \(front?.localizedName ?? "-")（\(front?.bundleIdentifier ?? "-")）\(front?.bundleIdentifier == "com.apple.finder" ? "，可能是桌面" : "")；"
                    + "沒被別的視窗擋住 \(panel.occlusionState.contains(.visible) ? "是" : "否")"
            case "hidden":
                out[id] = "因全屏收著 \(hiddenForFullscreen ? "是" : "否")，快捷鍵藏著 \(userHidden ? "是" : "否")，從選單退出的標記 \(fm.fileExists(atPath: userQuitFlag.path) ? "有" : "無")，全屏時被叫出來 \(fullscreenOverride ? "是" : "否")"
            case "crashes":
                let dir = fm.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/DiagnosticReports")
                let items = ((try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []).filter { $0.lastPathComponent.hasPrefix("CrossPet") }
                let newest = items.compactMap { (try? $0.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate }.max()
                out[id] = "崩潰報告 \(items.count) 份\(newest.map { "，最近一份 \(Self.clock(from: $0))" } ?? "")"
            case "install":
                let res = Bundle.main.resourceURL!
                let missing = ["web", "characters", "integrations"].filter { !fm.fileExists(atPath: res.appendingPathComponent($0).path) }
                out[id] = "裝在 \(Bundle.main.bundlePath.replacingOccurrences(of: NSHomeDirectory(), with: "~"))\(canSelfUpdate ? "" : "（不在應用程式資料夾，不能一鍵更新）")；缺少 \(missing.isEmpty ? "無" : missing.joined(separator: "、"))；開發者工具/python3 \(pythonReady ? "有" : "沒有")；檢查更新 \(lastUpdateCheck)"
            case "log": out[id] = Ledger.tail(Self.logURL, 400)
            case "updateLog": out[id] = Ledger.tail(fm.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/CrossPet-update.log"), 100)
            case "seen":
                let seen = lastSeen
                let m = Dictionary(uniqueKeysWithValues: Self.seenKeys.map { ($0.key, seen[$0.value] ?? 0) })
                out[id] = (try? JSONSerialization.data(withJSONObject: m)).flatMap { String(data: $0, encoding: .utf8) } ?? "{}"
            case "petEvents": out[id] = Ledger.tail(petEventsURL, 200)
            case "switches": out[id] = Ledger.tail(switchesURL, 200)
            case "settings":
                let skins = (defaults.dictionary(forKey: "skins") as? [String: String]) ?? [:]
                out[id] = "跟隨前臺 \(followApps ? "開" : "關")，跟隨事件 \(followEvents ? "開" : "關")，AI 幹活自動出現 \(aiAutostart ? "開" : "關")，衣服 \(skins.isEmpty ? "無" : "\(skins)")，"
                    + "峰谷換裝 \(peakSkins.isEmpty ? "無" : "\(peakSkins)")，省電 \(powerSave ? "開" : "關")，彩蛋 \(eggsEnabled ? "開" : "關")，額度合併 \(quotaAggregate ? "開" : "關")，額度樣式 \(quotaStyle)，重置提醒 \(codexResets ? "開" : "關")"
            case "characters": out[id] = characterFolders()
            case "quota:gpt": out[id] = "最近一次 \(lastGptQuota)\(codexNote.isEmpty ? "" : "；" + codexNote)\n額度檔案 \(file(stateDir.appendingPathComponent("gpt-quota.json")))"
            case "quota:gemini": out[id] = "找服務 \(lastAgyNote)\n額度檔案 \(file(stateDir.appendingPathComponent("gemini-quota.json")))"
            case "quota:deepseek": out[id] = "外掛自檢 \(file(stateDir.appendingPathComponent("deepseek-plugin.json")))\n餘額檔案 \(file(stateDir.appendingPathComponent("deepseek-quota.json")))"
            case "perf":
                let ps = Process(); ps.executableURL = URL(fileURLWithPath: "/bin/ps"); ps.arguments = ["-o", "%cpu=,rss=", "-p", "\(ProcessInfo.processInfo.processIdentifier)"]
                let pipe = Pipe(); ps.standardOutput = pipe; try? ps.run(); ps.waitUntilExit()
                let usage = String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? "?"
                out[id] = "CrossPet 程式 CPU% / 記憶體KB：\(usage)（網頁程式由系統共用，不單獨統計）；省電 \(powerSave ? "開" : "關")；螢幕重新整理率 \(NSScreen.screens.map { "\($0.maximumFramesPerSecond)Hz" }.joined(separator: "、"))；"
                    + "GPT 額度 \(defaults.bool(forKey: "gptQuota") ? "開" : "關")，Gemini 額度 \(defaults.bool(forKey: "agyQuota") ? "開" : "關")，DeepSeek 餘額 \(dsQuotaEnabled ? "開" : "關")"
            case "page":
                group.enter()
                var answered = false
                let finish = { (text: String) in if !answered { answered = true; out["page"] = text; group.leave() } }
                web.evaluateJavaScript("JSON.stringify(diagnoseDisplay())") { r, _ in finish(r as? String ?? "網頁沒有回應") }
                DispatchQueue.main.asyncAfter(deadline: .now() + 2) { finish("網頁沒有回應") }
            case "diag":
                if diagText.isEmpty, pythonReady, integrateScript != nil {
                    // 診斷要跑 python，卡住的話整份反饋都等不來：最多等 3 秒，到點先寫「診斷超時」
                    group.enter()
                    var answered = false
                    let finish = { (text: String) in if !answered { answered = true; out["diag"] = text; group.leave() } }
                    runIntegrate(["diagnose"]) { [weak self] text, _ in
                        DispatchQueue.main.async {
                            self?.diagText = text.trimmingCharacters(in: .whitespacesAndNewlines)
                            finish(self?.diagText ?? "")
                        }
                    }
                    DispatchQueue.main.asyncAfter(deadline: .now() + 3) { finish("診斷超時（3 秒內沒跑完）") }
                } else { out[id] = diagText }
            default:
                if id.hasPrefix("events:") { out[id] = Ledger.tail(eventsDir.appendingPathComponent("\(id.dropFirst(7)).jsonl"), 200) }
                else if id.hasPrefix("state:") {
                    // state:<角色>|<宿主>：狀態、多會話按角色記；輪次、按模型換角色按宿主名記（見 web/feedback-plan.js）
                    let parts = id.dropFirst(6).split(separator: "|").map(String.init)
                    let c = parts.first ?? "", host = parts.count > 1 ? parts[1] : c
                    out[id] = ["state", "sessions", "turns", "host"].compactMap { kind -> String? in
                        let t = file(stateDir.appendingPathComponent("\(kind == "turns" || kind == "host" ? host : c)-\(kind).json")).trimmingCharacters(in: .whitespacesAndNewlines)
                        return t.isEmpty ? nil : "\(kind)：\(String(t.prefix(800)))"
                    }.joined(separator: "\n")
                }
            }
        }
        group.notify(queue: .main) { done(out) }
    }

    /// 角色資料夾對照表：包裡帶的和資料目錄裡的，每個資料夾一行（json 能不能讀、有沒有待機圖、幾張立繪、載入了沒有）
    func characterFolders() -> String {
        let fm = FileManager.default
        let skinIds = Set(skinChoices.values.flatMap { $0.map(\.id) })
        let rows = [("包裡", Bundle.main.resourceURL!.appendingPathComponent("characters")), ("資料目錄", charactersDir)].flatMap { (place, dir) -> [String] in
            let dirs = ((try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? []).filter { $0.hasDirectoryPath }.sorted { $0.lastPathComponent < $1.lastPathComponent }
            return dirs.map { d in
                let name = d.lastPathComponent
                let jsonOk = (try? Data(contentsOf: d.appendingPathComponent("character.json"))).flatMap { try? JSONSerialization.jsonObject(with: $0) } != nil
                let files = (try? fm.contentsOfDirectory(atPath: d.path)) ?? []
                let sprites = files.filter { $0.hasSuffix(".webp") || $0.hasSuffix(".png") }
                let idle = sprites.contains { $0.hasPrefix("idle.") || $0.hasPrefix("default.") }
                let state = characterIds.contains(name) ? "已載入" : skinIds.contains(name) ? "已載入（衣服）" : name.hasPrefix("egg-") ? "彩蛋" : !jsonOk ? "沒載入：character.json 讀不出" : !idle ? "沒載入：缺待機圖" : "沒載入"
                return "\(place) \(name)：json \(jsonOk ? "能讀" : "讀不出")，待機圖 \(idle ? "有" : "無")，立繪 \(sprites.count) 張，\(state)"
            }
        }
        return rows.joined(separator: "\n")
    }

    /// 反饋附帶的顯示資訊：幾塊顯示器、各自大小和相對主屏的位置、桌寵在哪塊、顯示器是否各有空間、顯示層級 / 全屏隱藏 / 貼邊
    func displaysSummary() -> String {
        let screens = NSScreen.screens
        guard let main = screens.first else { return "" }
        let parts = screens.enumerated().map { i, s -> String in
            let f = s.frame
            var where_ = "主屏"
            if i > 0 {
                where_ = f.minX >= main.frame.maxX - 1 ? "右側" : f.maxX <= main.frame.minX + 1 ? "左側" : f.minY >= main.frame.maxY - 1 ? "上方" : f.maxY <= main.frame.minY + 1 ? "下方" : "重疊"
            }
            let here = panel.screen == s ? "（桌寵在這）" : ""
            return "\(Int(f.width))×\(Int(f.height))@\(Int(s.backingScaleFactor))x \(where_)\(here)"
        }
        var flags = ["層級 \(layerMode)", hideInFullscreen ? "全屏隱藏" : "全屏不隱藏"]
        if screens.count > 1 { flags.append(NSScreen.screensHaveSeparateSpaces ? "各屏單獨空間" : "各屏共用空間") }
        if let d = dock { flags.append("貼\(d == "left" ? "左" : "右")邊") }
        return "\(screens.count) 塊：" + parts.joined(separator: "、") + "；" + flags.joined(separator: "，")
    }

    func pushFacts(_ sections: [String: String], key: String? = nil) {
        guard let v = feedbackWeb, let data = try? JSONSerialization.data(withJSONObject: feedbackFacts(sections, key: key)),
              let text = String(data: data, encoding: .utf8) else { return }
        v.evaluateJavaScript("setFacts(\(text))", completionHandler: nil)
    }

    func handleFeedback(_ body: Any) {
        guard let msg = body as? [String: Any], let cmd = msg["cmd"] as? String else { return }
        let done = { [weak self] (ok: Bool, text: String) in
            DispatchQueue.main.async {
                self?.feedbackWeb?.evaluateJavaScript("onSent(\(ok), \(self?.quote(text) ?? "''"))", completionHandler: nil)
                if ok { self?.js("show('happy', true); say('收到反饋啦，謝謝你！')") }
            }
        }
        switch cmd {
        case "send":
            let endpoint = defaults.string(forKey: "feedbackURL") ?? Self.feedbackEndpoint
            guard let url = URL(string: endpoint), url.scheme != nil else { done(false, "反饋暫時關閉了"); return }
            guard let body = msg["body"] as? String, body.utf8.count <= 20_000 else { done(false, "內容太長了，刪短一點再發"); return }
            // 本機限流：一小時最多 5 條（飛書群機器人的地址是公開的，不能讓一臺機器刷屏）
            let now = Date().timeIntervalSince1970
            let recent = (defaults.array(forKey: "feedbackTimes") ?? [])
                .compactMap { ($0 as? NSNumber)?.doubleValue ?? Double("\($0)") }.filter { now - $0 < 3600 }
            guard recent.count < 5 else { done(false, "發得有點多啦，過一會兒再發"); return }
            var req = URLRequest(url: url, timeoutInterval: 15)
            req.httpMethod = "POST"
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.httpBody = Data(body.utf8)
            URLSession.shared.dataTask(with: req) { data, _, err in
                // 飛書不論成敗都回 HTTP 200，看返回裡的 code：0 才是發到了群裡
                let reply = data.flatMap { try? JSONSerialization.jsonObject(with: $0) } as? [String: Any]
                let code = (reply?["code"] as? NSNumber)?.intValue ?? -1
                if code == 0 {
                    DispatchQueue.main.async { defaults.set(recent + [now], forKey: "feedbackTimes") }
                    done(true, "")
                } else if err != nil { done(false, "連不上網路，稍後再試試") }
                else if code == 11232 { done(false, "反饋的人有點多，過一會兒再發") }
                else { done(false, "沒發出去（\(code)），稍後再試試") }
            }.resume()
        case "collect":   // 選好了型別（和補選）：按 web/feedback-plan.js 給的段落清單收集
            let want = msg["want"] as? [String] ?? [], key = msg["key"] as? String ?? ""
            collectFeedback(want) { [weak self] sections in self?.pushFacts(sections, key: key) }
        default:   // get：反饋視窗剛開啟，先給開頭和第二步的選項
            pushFacts([:])
            diagText = ""   // 每次開啟反饋都重新診斷一次（要用時再跑）
            if integrated == nil, integrateScript != nil {
                runIntegrate(["installed"]) { [weak self] out, _ in
                    let last = out.split(separator: "\n").last.map(String.init) ?? "[]"
                    self?.integrated = (try? JSONSerialization.jsonObject(with: Data(last.utf8))) as? [String] ?? []
                    self?.pushFacts([:])
                }
            }
        }
    }

}
