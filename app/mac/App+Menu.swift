import Carbon
import Cocoa
import ServiceManagement
import WebKit

extension App {
    // MARK: 選單

    func showMenu(_ event: NSEvent, in view: NSView) {
        NSMenu.popUpContextMenu(buildMenu(), with: event, for: view)
    }

    func buildMenu() -> NSMenu {
        let menu = NSMenu()
        if let r = latestRelease {
            if canSelfUpdate {
                add(menu, updating ? "⬆️ 正在更新到 \(r.tag)…" : "⬆️ 更新到 \(r.tag)", #selector(selfUpdate))
            } else {
                add(menu, "⬆️ 有新版本 \(r.tag)，點這裡下載", #selector(openRelease))
            }
            menu.addItem(.separator())
        }
        add(menu, "摸摸頭", #selector(pat))
        add(menu, "戳一下", #selector(poke))
        add(menu, "召喚彩蛋", #selector(egg))
        if codexResets && currentId == "gpt" { add(menu, "會重置嗎？", #selector(askReset)) }   // 只有 GPT 有這個需求
        if characterIds.count > 1 {
            menu.addItem(.separator())
            // 角色和衣服多了，名單一長串要翻半天：改成開啟設定的角色頁（卡片牆），看著圖選
            add(menu, "換角色…", #selector(openCharacterPage))
        }
        menu.addItem(.separator())
        let layerItem = NSMenuItem(title: "顯示層級", action: nil, keyEquivalent: "")
        let layerMenu = NSMenu()
        for (mode, title) in [("always", "始終在最上層"), ("ai", "跟著 AI（用 AI 時才浮在最上層）"), ("normal", "普通視窗（不置頂）")] {
            let item = NSMenuItem(title: title, action: #selector(setLayer(_:)), keyEquivalent: "")
            item.target = self
            item.representedObject = mode
            item.state = layerMode == mode ? .on : .off
            layerMenu.addItem(item)
        }
        layerItem.submenu = layerMenu
        menu.addItem(layerItem)
        add(menu, "設定…", #selector(openSettings))
        if NSEvent.modifierFlags.contains(.option) {  // 按住 ⌥ 右鍵才出現：給自己換立繪、加角色的人檢查效果用
            add(menu, "開發者控制台…", #selector(openDevConsole))
        }
        add(menu, "回到右下角", #selector(resetPosition))
        add(menu, hotkeyLabel.map { "暫時隱藏（\($0)）" } ?? "暫時隱藏", #selector(toggleHidden))
        menu.addItem(.separator())
        add(menu, "退出 CrossPet", #selector(quit))
        return menu
    }

}
