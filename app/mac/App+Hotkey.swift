import Carbon
import Cocoa
import ServiceManagement
import WebKit

extension App {
    // MARK: 快捷鍵隱藏
    // 全域性快捷鍵（預設 ⌃⌥P）：按一下藏起來，再按一下出來；點選單欄爪印也能叫出來。藏著時 AI 幹活也不出來，重開 App 恢復顯示。
    // 用 Carbon 的 RegisterEventHotKey：系統級快捷鍵，不需要「輔助功能」許可權
    static let hotkeyChoices: [(id: String, label: String, key: UInt32)] = [("p", "⌃⌥P", UInt32(kVK_ANSI_P)), ("h", "⌃⌥H", UInt32(kVK_ANSI_H))]
    var hideHotkey: String { defaults.string(forKey: "hideHotkey") ?? "p" }
    var hotkeyLabel: String? { Self.hotkeyChoices.first { $0.id == hideHotkey }?.label }

    func registerHotKey() {
        if let r = hotKeyRef { UnregisterEventHotKey(r); hotKeyRef = nil }
        hotkeyFailed = false
        guard let choice = Self.hotkeyChoices.first(where: { $0.id == hideHotkey }) else { return }
        if !hotKeyHandlerInstalled {
            hotKeyHandlerInstalled = true
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            InstallEventHandler(GetApplicationEventTarget(), { _, _, data in
                guard let data else { return noErr }
                let app = Unmanaged<App>.fromOpaque(data).takeUnretainedValue()
                DispatchQueue.main.async { app.toggleHidden() }
                return noErr
            }, 1, &spec, Unmanaged.passUnretained(self).toOpaque(), nil)
        }
        let id = EventHotKeyID(signature: OSType(0x43505431), id: 1)   // 'CPT1'
        let status = RegisterEventHotKey(choice.key, UInt32(controlKey | optionKey), id, GetApplicationEventTarget(), 0, &hotKeyRef)
        hotkeyFailed = status != noErr   // 被別的程式佔了
    }

    @objc func toggleHidden() { setUserHidden(!userHidden) }
    func setUserHidden(_ hidden: Bool) {
        userHidden = hidden   // 透明度 / 滑鼠 / 動畫在 didSet 裡處理，不 orderOut（見 applyHiddenState）
        if !hidden {
            fullscreenOverride = true; hiddenForFullscreen = false   // 你主動叫她：全屏時也先出來
            panel.orderFrontRegardless()
            if layerMode == "normal" { orderBelowFrontWindow() }
        }
        updatePageHidden()
    }

}
