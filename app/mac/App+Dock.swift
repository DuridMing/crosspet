import Carbon
import Cocoa
import ServiceManagement
import WebKit

extension App {
    // MARK: 貼邊
    // 拖到螢幕左 / 右邊緣鬆手：她縮到螢幕外，只露半個身子扒著邊緣（額度、名牌、氣泡都藏起來）。
    // AI 幹活時（或在等你回答）自己跑出來，幹完過約 10 秒縮回去；滑鼠移到露出來的那截上她會探出來一點。
    // 拖離邊緣就不貼了。另一塊螢幕接在這邊時不算邊緣
    var edgeDockEnabled: Bool { defaults.object(forKey: "edgeDock") as? Bool ?? true }
    /// DeepSeek 餘額（預設開）：用別家訂閱（OpenCode Go……）跑 DeepSeek 模型時官方賬戶沒錢，她會一直沒精神，可以關掉
    var dsQuotaEnabled: Bool { defaults.object(forKey: "dsQuota") as? Bool ?? true }
    /// 額度合併顯示（預設關）：小窗把各家 AI 的額度排成幾行，而不是隻顯示當前角色的。怎麼排在網頁裡（quota-aggregate.js）
    var quotaAggregate: Bool { defaults.bool(forKey: "quotaAggregate") }
    var quotaStyle: String { defaults.string(forKey: "quotaStyle") == "bars" ? "bars" : "text" }   // 額度怎麼顯示：text 文字（預設）/ bars 額度條（網頁層 quota-bars.js）
    var peakStyle: String { ["text", "off"].contains(defaults.string(forKey: "peakStyle") ?? "") ? defaults.string(forKey: "peakStyle")! : "bar" }   // 額度欄怎麼顯示峰谷時段：bar 時間條（預設）/ text 一行文字 / off（規則在網頁層 peak-hours.js）
    /// 按時段換衣服：{ 角色: { peak: 皮膚, valley: 皮膚 } }（"" = 原版，沒寫 = 不換）。判斷在網頁層（peak-hours.js），這裡只存
    var peakSkins: [String: [String: String]] { Self.cleanPeakSkins(defaults.dictionary(forKey: "peakSkins")) }
    static func cleanPeakSkins(_ v: Any?) -> [String: [String: String]] {
        var out: [String: [String: String]] = [:]
        for (id, x) in v as? [String: Any] ?? [:] {
            guard let d = x as? [String: Any] else { continue }
            var e: [String: String] = [:]
            for k in ["peak", "valley"] { if let s = d[k] as? String { e[k] = s } }
            if !e.isEmpty { out[id] = e }
        }
        return out
    }
    var peakSkinsJSON: String {
        guard let data = try? JSONSerialization.data(withJSONObject: peakSkins), let s = String(data: data, encoding: .utf8) else { return "{}" }
        return s
    }
    var codexResets: Bool { defaults.bool(forKey: "codexResets") }   // Codex 重置提醒（預設關，資料來自 codex-resets.com）
    /// 省電模式（預設關）：特效每秒 15 幀、閒著 10 秒就停（平時 30 幀、30 秒）
    var powerSave: Bool { defaults.bool(forKey: "powerSave") }
    // 看不見的時候（視窗被完全擋住、鎖屏、螢幕休眠）讓網頁把動畫全停了
    func updatePageHidden() {
        guard ready else { return }
        js("setPageHidden(\(screensAsleep || userHidden || hiddenForFullscreen || !panel.occlusionState.contains(.visible)))")
        // 藏著 / 螢幕休眠時定時的不查：從那種狀態回來時補一次（自帶 5 分鐘 / 1 分鐘間隔，不會多問）
        let away = screensAsleep || userHidden
        if wasAway && !away { refreshCodexQuota(.tick) }
        wasAway = away
    }

    func undock() {
        guard dock != nil else { return }
        dock = nil; dockOut = false; dockHover = false; dockTucked = false
        defaults.removeObject(forKey: "dock")
        js("setTucked(null)")
    }
    func checkDock() {
        guard edgeDockEnabled, let screen = panel.screen ?? NSScreen.main else { return }
        let f = panel.frame, vf = screen.visibleFrame, reach = f.width * (Self.baseSize.width / pageSize.width) * 0.3   // 按人物那塊的寬算：畫布因為文字變寬時不該更容易貼邊
        func neighbor(_ left: Bool) -> Bool {
            NSScreen.screens.contains { s in s != screen && s.frame.minY < f.maxY && s.frame.maxY > f.minY &&
                (left ? abs(s.frame.maxX - screen.frame.minX) < 2 : abs(s.frame.minX - screen.frame.maxX) < 2) }
        }
        if f.midX - vf.minX < reach, !neighbor(true) { setDock("left") }
        else if vf.maxX - f.midX < reach, !neighbor(false) { setDock("right") }
    }
    func setDock(_ side: String) {
        dock = side
        dockedAt = Date()
        defaults.set(side, forKey: "dock")
        dockOut = dockBusy
        applyDock()
    }
    /// AI 在幹活 / 等你回答 / 剛乾完（10 秒內）：跑出來
    var dockBusy: Bool {
        let now = Date()
        return lastPose.values.contains { p in
            guard p.at > dockedAt else { return false }
            let age = now.timeIntervalSince(p.at)
            switch p.pose {
            case "asking": return age < 600
            case "happy", "proud", "oops", "surprised": return age < 10
            case "idle", "sleeping", "tired": return false
            default: return age < 180
            }
        }
    }
    func updateDock() {
        guard dock != nil else { return }
        let busy = dockBusy
        if busy != dockOut { dockOut = busy; dockHover = false; applyDock() }
    }
    func applyDock() {
        guard let side = dock, let screen = panel.screen ?? NSScreen.main else { return }
        hitRects = []; hitRectsAt = .distantPast   // 換了樣子，可點區域重新量（舊的範圍會讓她誤以為滑鼠在身上，來回閃）
        let vf = screen.visibleFrame
        var f = panel.frame
        f.origin.y = min(max(f.origin.y, vf.minY - f.height * 0.15), vf.maxY - f.height)
        if dockOut {
            dockTucked = false
            js("setTucked(null)")
            f.origin.x = side == "left" ? vf.minX : vf.maxX - f.width
            slide(to: f)
            return
        }
        // 視窗還沒到邊上（剛拖過來鬆手）：先整個滑到螢幕邊緣，滑完再縮排去。一次只做一個動作，不然視窗在滑、人物也在視窗裡滑，看著亂
        if !dockTucked {
            var g = f; g.origin.x = side == "left" ? vf.minX : vf.maxX - f.width
            if abs(panel.frame.origin.x - g.origin.x) > 1 || abs(panel.frame.origin.y - g.origin.y) > 1 {
                slide(to: g)
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.37) { [weak self] in self?.tuck(side, vf) }
                return
            }
        }
        tuck(side, vf)
    }
    /// 縮排去（或換成探出來那張），按切口位置對齊螢幕邊緣。有貼邊立繪時切口就是視窗邊，視窗不用再動
    func tuck(_ side: String, _ vf: NSRect) {
        guard dock == side, !dockOut else { return }
        dockTucked = true
        var f = panel.frame
        // 滑鼠移上來：換成探出來更多的那張（edge_*_peek），切口照樣對齊螢幕邊緣（不能光把視窗往外挪，切口會露在螢幕中間）
        let peek = dockHover
        js("setTucked(\(quote(side)), \(peek))")
        let key = side + (peek ? "-peek" : "") + currentIdKey
        let place = { [weak self] (cut: Double) in
            guard let self, self.dock == side, !self.dockOut, self.dockHover == peek else { return }
            let zoom = f.width / self.pageSize.width
            f.origin.x = side == "left" ? vf.minX - CGFloat(cut) * zoom : vf.maxX - CGFloat(cut) * zoom
            if abs(f.origin.x - self.panel.frame.origin.x) > 1 { self.slide(to: f) }
        }
        if let cut = dockCut[key] { place(cut); return }
        // 換成貼邊立繪後再量切口（圖要先載入），量過的記下來
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.45) { [weak self] in
            guard let self, self.dock == side, !self.dockOut, self.dockHover == peek else { return }
            self.web.evaluateJavaScript("dockCut(\(self.quote(side)))") { [weak self] r, _ in
                guard let cut = (r as? NSNumber)?.doubleValue else { return }
                self?.dockCut[key] = cut
                place(cut)
            }
        }
    }
    var currentIdKey: String { currentId ?? "" }
    func slide(to frame: NSRect) {
        NSAnimationContext.runAnimationGroup { ctx in
            ctx.duration = 0.35
            ctx.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            panel.animator().setFrame(frame, display: true)
        }
    }
    /// 縮著時滑鼠在露出來的那截上停 0.15 秒：探出來；移開 0.3 秒後縮回去
    func updateDockHover(_ inside: Bool) {
        guard dock != nil, !dockOut else { return }
        if inside {
            dockLeaveAt = nil
            guard !dockHover else { return }
            if dockEnterAt == nil { dockEnterAt = Date() }   // 停一小會兒才探出來，路過不算
            else if Date().timeIntervalSince(dockEnterAt!) > 0.15 { dockEnterAt = nil; dockHover = true; applyDock() }
            return
        }
        dockEnterAt = nil
        if dockHover {
            if dockLeaveAt == nil { dockLeaveAt = Date() }
            else if Date().timeIntervalSince(dockLeaveAt!) > 0.3 { dockHover = false; dockLeaveAt = nil; applyDock() }
        }
    }

}
