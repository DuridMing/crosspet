import Carbon
import Cocoa
import ServiceManagement
import WebKit

extension App {
    // MARK: 點選穿透
    // 視窗比桌寵大（給動作、氣泡留的空間）：滑鼠不在桌寵本體、氣泡、名牌、額度上時讓視窗不接滑鼠，點選落到後面的視窗

    func updateClickThrough() {
        if userHidden || hiddenForFullscreen {   // 收著：透明的視窗不能接滑鼠（下面會按命中區域反覆切換，這裡先攔住）
            if !panel.ignoresMouseEvents { panel.ignoresMouseEvents = true }
            return
        }
        let p = NSEvent.mouseLocation, f = panel.frame
        guard f.contains(p) else { updateDockHover(false); return }
        if NSEvent.pressedMouseButtons != 0 { return }   // 正在拖、正在點：別中途換
        // 貼著邊時量得勤一點：探出來 / 縮回去要馬上跟著滑鼠
        if Date().timeIntervalSince(hitRectsAt) > (dock != nil ? 0.08 : 0.25) {
            hitRectsAt = Date()
            web.evaluateJavaScript("hitRects()") { [weak self] r, _ in
                guard let text = r as? String, let data = text.data(using: .utf8),
                      let list = try? JSONSerialization.jsonObject(with: data) as? [[Double]] else { return }
                self?.hitRects = list.filter { $0.count == 4 }.map { CGRect(x: $0[0], y: $0[1], width: $0[2], height: $0[3]) }
            }
        }
        let zoom = f.width / pageSize.width   // 網頁畫素 → 螢幕點（畫布可能因為文字大小比 260 寬）
        let point = CGPoint(x: (p.x - f.minX) / zoom, y: (f.maxY - p.y) / zoom)
        let over = hitRects.isEmpty || hitRects.contains { $0.contains(point) }
        if panel.ignoresMouseEvents == over { panel.ignoresMouseEvents = !over }
        updateDockHover(over && !hitRects.isEmpty)   // 縮著時滑鼠在露出來的那截上：探出來
    }

    /// 降到普通層級後還排在所有普通視窗最前面、壓在你正在用的視窗上：挪到前臺程式最前面那個視窗的後面
    func orderBelowFrontWindow() {
        guard let pid = NSWorkspace.shared.frontmostApplication?.processIdentifier, pid != ProcessInfo.processInfo.processIdentifier,
              let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]],
              let front = list.first(where: { ($0[kCGWindowOwnerPID as String] as? Int32) == pid && ($0[kCGWindowLayer as String] as? Int) == 0 }),
              let number = front[kCGWindowNumber as String] as? Int else { return }
        panel.order(.below, relativeTo: number)
    }

    /// 桌寵中間那塊有沒有被別的普通視窗擋住（只看視窗位置和層級，不需要錄屏許可權）
    func isCovered() -> Bool {
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenAboveWindow, .excludeDesktopElements],
                                                    CGWindowID(panel.windowNumber)) as? [[String: Any]] else { return false }
        let screenTop = NSScreen.screens.first?.frame.maxY ?? 0
        let f = panel.frame
        let core = CGRect(x: f.minX, y: screenTop - f.maxY, width: f.width, height: f.height).insetBy(dx: f.width * 0.25, dy: f.height * 0.2)
        return list.contains { w in
            guard (w[kCGWindowLayer as String] as? Int) == 0, (w[kCGWindowAlpha as String] as? Double ?? 1) > 0.05,
                  let b = w[kCGWindowBounds as String] as? NSDictionary,
                  let r = CGRect(dictionaryRepresentation: b) else { return false }
            return r.intersects(core)
        }
    }

    @objc func setLayer(_ sender: NSMenuItem) {
        guard let mode = sender.representedObject as? String else { return }
        defaults.set(mode, forKey: "layer")
        appLog("顯示層級改成 \(mode)")
        applyLayer()
    }

    /// 收起來 / 放回來（全屏時收著、快捷鍵藏起來）：只改透明度和滑鼠，視窗始終在所有桌面裡。
    /// 不 orderOut：退出全屏程式的那一刻視窗如果是摘下來的，「所有桌面都顯示」就失效了，
    /// 之後再放回來也只在退出全屏的那個桌面有她（isOnActiveSpace 為 false，別的桌面上右鍵也沒反應）
    func applyHiddenState() {
        let hidden = userHidden || hiddenForFullscreen
        panel.alphaValue = hidden ? 0 : 1
        if hidden { panel.ignoresMouseEvents = true }
        updatePageHidden()
    }

    /// 一直允許出現在全屏程式的空間裡：AI 程式（Codex、Claude……）全屏時要照常顯示，別的程式全屏時由 checkFullscreen 藏起來
    func applyFullscreenBehavior() {
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        checkFullscreen()
    }

    // 全屏時隱藏：光靠上面的 collectionBehavior 不夠（「所有空間都顯示」的浮動面板照樣會蓋在全屏程式上），
    // 所以自己看：桌寵所在的那塊螢幕上，前臺程式有沒有一個蓋滿整塊螢幕的視窗（全屏程式、全屏影片、演示）。
    // 有就收起來，退出全屏再放回來。只看視窗位置和大小，不需要錄屏許可權；每秒看一次，切換空間 / 前臺程式時馬上看
    func checkFullscreen() {
        fullscreenCheckedAt = Date()
        if userHidden { return }   // 你用快捷鍵藏起來的：全屏規則不管她
        // 全屏的是 AI 程式（專心和 AI 幹活）：照常顯示；看影片、演示、玩遊戲才藏
        let full = hideInFullscreen && !fullscreenOverride && !frontIsAI && frontIsFullscreen()
        guard full != hiddenForFullscreen else { return }
        hiddenForFullscreen = full
        if full {
            // 不 orderOut：視窗一直留著，只是透明、不接滑鼠、動畫停掉（hiddenForFullscreen 的 didSet 裡做，
            // 原因見 applyHiddenState；Windows 也是這麼藏的）
        } else {
            panel.orderFrontRegardless()
            if layerMode != "always" { orderBelowFrontWindow() }
        }
    }
    func frontIsFullscreen() -> Bool {
        guard let app = NSWorkspace.shared.frontmostApplication, app.processIdentifier != ProcessInfo.processInfo.processIdentifier,
              let screen = panel.screen ?? NSScreen.main, let primary = NSScreen.screens.first,
              let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly], kCGNullWindowID) as? [[String: Any]]
        else { return false }
        // NSScreen 是左下角原點，視窗列表是主屏左上角原點
        let f = screen.frame
        let t = CGRect(x: f.minX, y: primary.frame.maxY - f.maxY, width: f.width, height: f.height)
        let near = { (a: CGFloat, b: CGFloat) in abs(a - b) < 2 }
        func rect(_ w: [String: Any]) -> CGRect? {
            guard let b = w[kCGWindowBounds as String] as? [String: CGFloat], let x = b["X"], let y = b["Y"], let wd = b["Width"], let h = b["Height"] else { return nil }
            return CGRect(x: x, y: y, width: wd, height: h)
        }
        let pid = { (w: [String: Any]) in w[kCGWindowOwnerPID as String] as? Int32 }
        let layer = { (w: [String: Any]) in w[kCGWindowLayer as String] as? Int ?? -1 }
        // 前臺程式的普通視窗：左右鋪滿、底邊到螢幕底。有劉海的螢幕全屏時頂上會讓出劉海那一條（約 37 點）
        let topSlack = max(screen.safeAreaInsets.top, 24) + 16   // 實測：劉海安全區 32，全屏視窗讓出 37；後面還有一道確認，這裡放寬
        var exact = false, fills = false
        for w in list where pid(w) == app.processIdentifier && layer(w) == 0 {
            guard let r = rect(w), near(r.minX, t.minX), near(r.width, t.width), near(r.maxY, t.maxY) else { continue }
            if near(r.minY, t.minY) { exact = true } else if r.minY - t.minY <= topSlack { fills = true }
        }
        if exact { return true }
        guard fills else { return false }
        // 頂上讓出了劉海：再確認真的是全屏（不是 Dock 自動隱藏時的最大化視窗）——
        // 全屏時前臺程式在螢幕頂上有一條同寬的高層視窗（隱藏起來的標題欄），而平時鋪滿螢幕的程式塢視窗不在了
        let strip = list.contains { w in
            guard pid(w) == app.processIdentifier, layer(w) > 0, let r = rect(w) else { return false }
            return near(r.minX, t.minX) && near(r.minY, t.minY) && near(r.width, t.width) && r.height <= 80
        }
        let dockPid = NSRunningApplication.runningApplications(withBundleIdentifier: "com.apple.dock").first?.processIdentifier
        let dockOverlay = list.contains { w in
            guard dockPid != nil, pid(w) == dockPid, let r = rect(w) else { return false }
            return near(r.minX, t.minX) && near(r.minY, t.minY) && near(r.width, t.width) && near(r.height, t.height)
        }
        return strip || !dockOverlay
    }

}
