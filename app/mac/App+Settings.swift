import Carbon
import Cocoa
import ServiceManagement
import WebKit

extension App {
    // MARK: 設定
    // 一個獨立視窗（web/settings.html，和 Windows 版共用），右鍵選單隻留常用的幾項，其餘都在這裡
    static let baseSize = NSSize(width: 260, height: 362)
    var petScale: CGFloat {
        let v = defaults.double(forKey: "size")
        return v >= 0.6 && v <= 1.6 ? CGFloat(v) : 1
    }
    /// 文字大小（只在解鎖時用）和鎖：沒存過 = 鎖著，文字跟著人物，和以前一樣
    var textScale: Double {
        let v = defaults.double(forKey: "textSize")
        return v >= 0.6 && v <= 1.6 ? v : 1
    }
    var textLinked: Bool { defaults.object(forKey: "textLinked") as? Bool ?? true }
    var showName: Bool { defaults.object(forKey: "showName") as? Bool ?? true }
    var followApps: Bool { defaults.object(forKey: "followApps") as? Bool ?? false }   // maid fork：預設不跟著前臺程式換角色
    // 跟隨 AI 工作事件（#23 給 macOS 補上的）：預設關——macOS 以前沒有這個行為，升級後不該突然按事件換角色；Windows 一直預設開
    var followEvents: Bool { defaults.object(forKey: "followEvents") as? Bool ?? false }
    var eggsEnabled: Bool { defaults.object(forKey: "eggs") as? Bool ?? true }

    /// 換大小時視窗錨在哪：
    /// - corner：啟動 / 網頁過載。存下的位置本來就是這個畫布的視窗，左下角不動只換大小（不然解鎖的人每次啟動都往旁邊漂）
    /// - bottom：改人物大小。底邊中點不動（腳下的位置不變），和以前一樣；螢幕上的底欄高 = 62 × 文字大小，沒變
    /// - feet：改文字大小。底欄在螢幕上變高 / 變矮多少，視窗底邊就往下 / 往上挪多少，人物不動
    enum Anchor: String { case corner, bottom, feet }

    /// 純計算（tests/window_place_probe.sh 測），螢幕點、y 向上。oldFoot / newFoot：底欄在螢幕上的高
    static func placeWindow(old: NSRect, size: NSSize, oldFoot: CGFloat, newFoot: CGFloat, anchor: Anchor, visibleMinY: CGFloat?) -> NSRect {
        switch anchor {
        case .corner:
            return NSRect(origin: old.origin, size: size)
        case .bottom:
            return NSRect(x: old.midX - size.width / 2, y: old.minY, width: size.width, height: size.height)
        case .feet:
            var y = old.minY - (newFoot - oldFoot)
            // 往下擴出了可見區：挪回來，但不挪到比原來還高（使用者本來就放在邊上的不動）
            if let v = visibleMinY, y < v, y < old.minY { y = min(old.minY, v) }
            return NSRect(x: old.midX - size.width / 2, y: y, width: size.width, height: size.height)
        }
    }

    /// 改大小：視窗 = 畫布 × 人物大小，網頁按人物大小縮放；放在哪見 Anchor
    func applyScale(_ scale: CGFloat, anchor: Anchor = .bottom) {
        let size = NSSize(width: pageSize.width * scale, height: pageSize.height * scale)
        let f = Self.placeWindow(old: panel.frame, size: size, oldFoot: shownFootPt, newFoot: pageFoot * scale, anchor: anchor,
                                 visibleMinY: (panel.screen ?? NSScreen.main)?.visibleFrame.minY)
        panel.setFrame(f, display: true)
        web.pageZoom = scale
        shownFootPt = pageFoot * scale
        defaults.set([f.minX, f.minY], forKey: "origin")
    }

    /// 人物大小 / 文字大小變了：交給網頁算畫布（規則在 app/web/text-size.js），按回來的尺寸設視窗。
    /// target：人物大小要動畫到哪（底邊不動）；nil = 文字變了（anchor .feet）或網頁剛載入完（.corner），按現在的人物大小重設一次
    func applySizes(animateTo target: CGFloat? = nil, anchor: Anchor = .feet) {
        if shownFootPt == 0 { shownFootPt = pageFoot * panel.frame.width / pageSize.width }   // 還沒按畫布設過：按現在的視窗算
        let finish = { [weak self] in
            guard let self else { return }
            if let t = target { self.animateScale(to: t); return }
            self.applyScale(self.shownScale > 0 ? self.shownScale : self.petScale, anchor: anchor)
            if anchor == .feet, self.dock != nil { self.applyDock() }   // 貼著邊時改了文字：視窗變寬了，重新對齊螢幕邊
        }
        guard ready else { finish(); return }   // 網頁還沒載入完：先按舊畫布，載入完（didFinish）會再調一次
        web.evaluateJavaScript("setSizes(\(Double(target ?? petScale)), \(textScale), \(textLinked))") { [weak self] r, _ in
            guard let self else { return }
            if let text = r as? String, let data = text.data(using: .utf8),
               let o = try? JSONSerialization.jsonObject(with: data) as? [String: Double],
               let w = o["w"], let h = o["h"], let foot = o["foot"] {
                let page = NSSize(width: w, height: h)
                if page != self.pageSize || CGFloat(foot) != self.pageFoot { self.dockCut = [:] }   // 畫布變了：貼邊切口要重新量
                self.pageSize = page
                self.pageFoot = CGFloat(foot)
            }
            finish()
        }
    }

    /// 拖滑塊時每次只變一點，直接跟手；一下子變很多（點「還原」）時用 0.2 秒過渡過去
    func animateScale(to target: CGFloat) {
        scaleTimer?.invalidate()
        let from = shownScale > 0 ? shownScale : petScale
        guard abs(target - from) > 0.06 else { shownScale = target; applyScale(target); return }
        let start = Date()
        scaleTimer = Timer.scheduledTimer(withTimeInterval: 1.0 / 60, repeats: true) { [weak self] t in
            guard let self else { t.invalidate(); return }
            let k = min(1, Date().timeIntervalSince(start) / 0.2)
            let eased = 1 - pow(1 - k, 3)
            self.shownScale = from + (target - from) * CGFloat(eased)
            self.applyScale(self.shownScale)
            if k >= 1 { t.invalidate(); if self.dock != nil { self.applyDock() } }   // 貼著邊時調完大小重新對齊邊緣
        }
    }

    /// 設定、反饋視窗預設開到內容基本放得下的高度（反饋的四步一頁排開、角色頁的卡片牆），螢幕矮就縮到放得下
    static func fitHeight(_ h: CGFloat) -> CGFloat { min(h, (NSScreen.main?.visibleFrame.height ?? h) - 60) }

    @objc func openSettings() { openSettingsAt(tab: nil) }
    /// 換角色視窗（右鍵「換角色…」、設定裡的按鈕）：大立繪 + 衣服卡 + 角色卡。和設定頁同一條通道、同一份狀態，
    /// 單獨開一個大視窗好讓立繪看得清楚；可以拖大小，系統記住上次的大小和位置
    @objc func openCharacterPage() {
        if let w = charactersWindow {
            NSApp.activate(ignoringOtherApps: true)
            w.makeKeyAndOrderFront(nil)
            return
        }
        let config = WKWebViewConfiguration()
        config.userContentController.add(DevBridge(app: self, kind: .settings), name: "settings")
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 760, height: Self.fitHeight(740)),
                         styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        w.title = "CrossPet 換角色"
        w.isReleasedWhenClosed = false
        w.minSize = NSSize(width: 420, height: 400)
        w.center()
        w.setFrameAutosaveName("CrossPetCharacters")
        let v = WKWebView(frame: w.contentView!.bounds, configuration: config)
        v.autoresizingMask = [.width, .height]
        v.navigationDelegate = self
        v.loadFileURL(root.appendingPathComponent("web/characters.html"), allowingReadAccessTo: root)
        w.contentView!.addSubview(v)
        charactersWindow = w
        charactersWeb = v
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: w, queue: .main) { [weak self] _ in
            self?.charactersWeb = nil
            self?.charactersWindow = nil
        }
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
    }

    /// 桌寵頁面發來的訊息：「新功能」卡片點了「去看看」→ 開啟設定停在「新功能」頁
    func handlePet(_ body: Any) {
        if let d = body as? [String: Any], let tab = d["openSettings"] as? String { if tab == "character" { openCharacterPage() } else { openSettingsAt(tab: tab) } }
        // 按時段換衣服（網頁層判斷好了時段）：只換這個角色已有的衣服，不切換當前角色
        if let d = body as? [String: Any], let w = d["wear"] as? [String: Any], let id = w["id"] as? String, let skin = w["skin"] as? String,
           characterIds.contains(id), skin.isEmpty || skinChoices[id]?.contains(where: { $0.id == skin }) == true {
            wear(id, skin)
        }
    }

    /// tab：開啟後停在哪個標籤頁（nil = 上次看的那頁）；視窗已經開著就切過去
    func openSettingsAt(tab: String?) {
        if let w = settingsWindow {
            if let tab { settingsWeb?.evaluateJavaScript("showTab(\(quote(tab)))", completionHandler: nil) }
            NSApp.activate(ignoringOtherApps: true)
            w.makeKeyAndOrderFront(nil)
            return
        }
        pendingSettingsTab = tab
        let config = WKWebViewConfiguration()
        config.userContentController.add(DevBridge(app: self, kind: .settings), name: "settings")
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: Self.fitHeight(780)),
                         styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        w.title = "CrossPet 設定"
        w.isReleasedWhenClosed = false
        w.center()
        let v = WKWebView(frame: w.contentView!.bounds, configuration: config)
        v.autoresizingMask = [.width, .height]
        v.navigationDelegate = self
        v.loadFileURL(root.appendingPathComponent("web/settings.html"), allowingReadAccessTo: root)
        w.contentView!.addSubview(v)
        settingsWindow = w
        settingsWeb = v
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: w, queue: .main) { [weak self] _ in
            self?.settingsWeb = nil
            self?.settingsWindow = nil
        }
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
    }

    func settingsState() -> [String: Any] {
        let names = defaults.dictionary(forKey: "names") as? [String: String] ?? [:]
        var state: [String: Any] = [
            "version": currentVersion, "appVersion": currentVersion, "whatsNewSeen": defaults.string(forKey: "whatsNewSeen") ?? currentVersion, "platform": "mac",
            "size": Double(petScale), "textSize": textScale, "textLinked": textLinked, "showName": showName, "eggs": eggsEnabled, "hideFullscreen": hideInFullscreen, "aura": auraMode, "followApps": followApps, "followEvents": followEvents,
            "gptQuota": defaults.bool(forKey: "gptQuota"), "agyQuota": defaults.bool(forKey: "agyQuota"), "gptQuotaNote": codexNote,
            "login": SMAppService.mainApp.status == .enabled,
            "edgeDock": edgeDockEnabled, "dsQuota": dsQuotaEnabled, "quotaAggregate": quotaAggregate, "codexResets": codexResets, "peakStyle": peakStyle, "quotaStyle": quotaStyle, "peakSkins": peakSkins, "powerSave": powerSave,
            "hideHotkey": hideHotkey, "hotkeyFailed": hotkeyFailed, "hotkeys": [["id": "off", "label": "不用快捷鍵"]] + Self.hotkeyChoices.map { ["id": $0.id, "label": $0.label] },
            "aiAutostart": aiAutostart,
            "character": currentId ?? characterIds.first ?? "",
            "characters": characterIds.map { id -> [String: Any] in
                var c: [String: Any] = ["id": id, "name": displayNames[id] ?? id, "defaultName": defaultNames[id] ?? id, "custom": names[id] ?? "",
                                        "images": cardImages(id)]   // 角色頁卡片圖（原版）
                if let accent = accents[id] { c["accent"] = accent }
                if let skins = skinChoices[id], !skins.isEmpty {   // 有別的衣服：原版排第一
                    c["skins"] = [["id": "", "name": baseOutfit[id] ?? "原版", "images": cardImages(id)] as [String: Any]]
                        + skins.map { ["id": $0.id, "name": $0.name, "images": cardImages($0.id)] as [String: Any] }
                    c["skin"] = (defaults.dictionary(forKey: "skins") as? [String: String])?[id] ?? ""
                }
                return c
            },
            "updating": updating,
            "plugins": (try? JSONSerialization.jsonObject(with: Data(pluginStatus.utf8))) ?? [],
        ]
        if let r = latestRelease, canSelfUpdate { state["update"] = r.tag }
        if let list = integrated, integrateScript != nil {
            state["integrations"] = Self.integrationTargets.map {
                ["id": $0.id, "label": $0.label, "note": $0.note, "installed": list.contains($0.id),
                 "lastSeen": lastSeen[Self.seenKeys[$0.id] ?? ""].map { Date().timeIntervalSince1970 - $0 as Any } ?? NSNull()] as [String: Any]
            }
        }
        return state
    }

}
