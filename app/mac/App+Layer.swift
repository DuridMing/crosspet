import Carbon
import Cocoa
import ServiceManagement
import WebKit

extension App {
    // MARK: 顯示層級
    // always：始終在最上層；ai：前臺是 AI 應用、AI 等你回答、剛乾完活時在最上層，其他時候是普通視窗（會被擋住）；
    // normal：普通視窗。全屏時隱藏見 checkFullscreen（AI 程式全屏時照常顯示）
    var layerMode: String { defaults.string(forKey: "layer") ?? "always" }
    var hideInFullscreen: Bool { defaults.object(forKey: "hideFullscreen") as? Bool ?? true }

    /// 前臺是不是某個角色對應的 AI 程式（角色 character.json 的 apps，或 WorkBuddy / ZCode 這類多模型宿主）
    var frontIsAI: Bool {
        guard let front = NSWorkspace.shared.frontmostApplication?.bundleIdentifier else { return false }
        return appToCharacter[front] != nil || hostApp(front) != nil
    }

    var aiActive: Bool {
        if frontIsAI { return true }
        let now = Date()
        return lastPose.values.contains { p in
            let age = now.timeIntervalSince(p.at)
            if p.pose == "asking" { return age < 600 }                        // 等你回答：一直等到你回答
            return age < 8 && ["happy", "proud"].contains(p.pose)              // 剛乾完活：浮上來一會兒
            // AI 在後臺幹活時不浮上來：選這個模式的人就是嫌擋路，幹活時往往正是你切去忙別的
        }
    }

    func applyLayer() {
        // 每次輪詢（0.3 秒）都看：程式一進全屏，視窗列表馬上就是全屏後的大小，但「切換空間」的通知要等動畫放完才來，
        // 只靠它和每秒一次的檢查，她會在全屏動畫上露一秒多（閃一下）
        if userHidden { return }   // 你用快捷鍵藏起來了：什麼都別把她翻出來
        if Date().timeIntervalSince(fullscreenCheckedAt) >= 0.25 { checkFullscreen() }
        if hiddenForFullscreen { return }   // 全屏時收著：別被換層級的邏輯翻出來
        let top = layerMode == "always" || (layerMode == "ai" && aiActive)
        let level: NSWindow.Level = top ? .floating : .normal
        guard panel.level != level else { return }
        // 原本被別的視窗擋著、要浮上來：先藏起來，換完層級再淡入（沒被擋住就直接換，免得閃一下）
        guard top, isCovered() else {
            panel.level = level
            if top { panel.orderFrontRegardless() } else { orderBelowFrontWindow() }
            return
        }
        web.evaluateJavaScript("sink()") { [weak self] _, _ in
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
                guard let self, !self.userHidden, self.layerMode == "always" || (self.layerMode == "ai" && self.aiActive) else { self?.js("surface()"); return }
                self.panel.level = .floating
                self.panel.orderFrontRegardless()
                self.js("surface()")
            }
        }
    }

}
