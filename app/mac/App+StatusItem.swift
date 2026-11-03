import Carbon
import Cocoa
import ServiceManagement
import WebKit

extension App {
    // MARK: 選單欄圖示
    // Mac 版不在程式塢和 ⌘Tab 裡：她被別的視窗擋住（顯示層級選了「普通視窗」或「跟著 AI」）、
    // 被拖到看不見的地方時，從這裡找她。左鍵：叫到最前面；右鍵：和桌寵右鍵一樣的選單（和 Windows 的托盤圖示對應）

    func setupStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.autosaveName = "CrossPetStatusItem"   // 記住使用者按住 ⌘ 拖到的位置
        if let button = item.button {
            let image = NSImage(systemSymbolName: "pawprint.fill", accessibilityDescription: "CrossPet")
            image?.isTemplate = true
            button.image = image
            button.toolTip = "CrossPet：單擊把她叫到最前面，右鍵開啟選單"
            button.target = self
            button.action = #selector(statusItemClicked(_:))
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
        }
        statusItem = item
    }

    @objc func statusItemClicked(_ sender: NSStatusBarButton) {
        let event = NSApp.currentEvent
        if event?.type == .rightMouseUp || event?.modifierFlags.contains(.control) == true {
            // 臨時掛上選單再「點一下」：選單從圖示正下方彈出，關掉後摘下，免得左鍵也變成彈選單
            statusItem?.menu = buildMenu()
            sender.performClick(nil)
            statusItem?.menu = nil
        } else {
            bringToFront()
        }
    }

    /// 叫到最前面：在當前螢幕看不見（被拖出螢幕、換了顯示器）就先回到右下角
    @objc func bringToFront() {
        let visible = NSScreen.screens.contains { $0.visibleFrame.intersects(panel.frame.insetBy(dx: 40, dy: 40)) }
        if !visible { resetPosition() }
        userHidden = false
        fullscreenOverride = true; hiddenForFullscreen = false   // 你主動叫她：全屏時也出來（切換空間 / 前臺程式後再按規則收起來）
        panel.orderFrontRegardless()
        js("handleClick('pat')")
    }

    @discardableResult
    func add(_ menu: NSMenu, _ title: String, _ action: Selector) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: action, keyEquivalent: "")
        item.target = self
        menu.addItem(item)
        return item
    }

    @objc func pat() { js("handleClick('pat')") }
    @objc func poke() { js("handleClick('poke')") }
    @objc func egg() { js("playEgg(true)") }
    @objc func askReset() { js("askReset()") }

    /// 換衣服（皮膚）：記下來，重新載入立繪；正在顯示的就是她的話馬上換上
    func wear(_ id: String, _ skin: String) {
        var skins = defaults.dictionary(forKey: "skins") as? [String: String] ?? [:]
        guard (skins[id] ?? "") != skin else { return }
        if skin.isEmpty { skins.removeValue(forKey: id) } else { skins[id] = skin }
        defaults.set(skins, forKey: "skins")
        loadCharacters()
        js("refreshLook(\(quote(id)))")
        pushSettings()   // 設定頁開著（角色頁的衣服卡）時高亮跟著變：按時段自動換衣服也走這裡
    }

    @objc func rename() {
        guard let id = currentId ?? characterIds.first else { return }
        let alert = NSAlert()
        alert.messageText = "給 \(displayNames[id] ?? id) 改名"
        alert.informativeText = "留空則恢復預設名字。"
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 220, height: 24))
        field.stringValue = displayNames[id] ?? ""
        alert.accessoryView = field
        alert.addButton(withTitle: "好")
        alert.addButton(withTitle: "取消")
        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        var names = defaults.dictionary(forKey: "names") as? [String: String] ?? [:]
        let value = field.stringValue.trimmingCharacters(in: .whitespaces)
        if value.isEmpty { names.removeValue(forKey: id) } else { names[id] = value }
        defaults.set(names, forKey: "names")
        loadCharacters()
        js("setName(\(quote(id)), \(quote(displayNames[id] ?? id)))")
    }

    @objc func toggleAntigravityQuota() {
        let on = !defaults.bool(forKey: "agyQuota")
        defaults.set(on, forKey: "agyQuota")
        if on { refreshAntigravityQuota() } else { js("setQuota('gemini', null)") }
    }

    /// 背景光暈：auto（跟著系統深淺色，深色模式下關）/ on / off
    var auraMode: String { defaults.string(forKey: "auraMode") ?? "auto" }
    @objc func setAura(_ sender: NSMenuItem) {
        guard let mode = sender.representedObject as? String else { return }
        defaults.set(mode, forKey: "auraMode")
        js("setAuraMode(\(quote(mode)))")
    }

    @objc func toggleGPTQuota() {
        let on = !defaults.bool(forKey: "gptQuota")
        defaults.set(on, forKey: "gptQuota")
        if on { refreshCodexQuota(.now) } else { js("setCodexQuota(null)") }
    }

    @objc func toggleLogin() {
        do {
            if SMAppService.mainApp.status == .enabled { try SMAppService.mainApp.unregister() }
            else { try SMAppService.mainApp.register() }
        } catch {
            let alert = NSAlert()
            alert.messageText = "設定失敗"
            alert.informativeText = "請把 CrossPet.app 放進「應用程式」資料夾後再試。\n\(error.localizedDescription)"
            alert.runModal()
        }
    }

    @objc func openCharacters() { NSWorkspace.shared.open(charactersDir) }
    @objc func reload() { web.reload() }
    @objc func resetPosition() {
        undock()
        guard let screen = NSScreen.main?.visibleFrame else { return }
        panel.setFrameOrigin(NSPoint(x: screen.maxX - panel.frame.width - 40, y: screen.minY + 100))
        defaults.removeObject(forKey: "origin")
    }
}
