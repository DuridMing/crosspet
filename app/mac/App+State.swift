import Carbon
import Cocoa
import ServiceManagement
import WebKit

extension App {
    // MARK: 狀態與額度

    /// 讀 /tmp/crosspet/<id>-state.json 和 <id>-quota.json，有變化就交給畫面。
    func pollStates() {
        guard ready else { return }
        var latestActive: (id: String?, ts: Double) = (nil, 0)
        // current：多模型宿主（WorkBuddy）裡沒有對應角色的模型，由當前角色來演；它只有狀態，沒有額度
        for id in characterIds + ["current"] {
            // sessions：同一個 AI 好幾個對話同時幹活時，每個對話各自的狀態（演「手忙腳亂」用）
            for kind in ["state", "quota", "sessions"] where !(id == "current" && kind == "quota") && !(id == "deepseek" && kind == "quota" && !dsQuotaEnabled) {
                let url = stateDir.appendingPathComponent("\(id)-\(kind).json")
                let key = "\(id)-\(kind)"
                guard let attrs = try? FileManager.default.attributesOfItem(atPath: url.path),
                      let stamp = attrs[.modificationDate] as? Date, stamp != stamps[key] else { continue }
                stamps[key] = stamp
                // 狀態只認 10 分鐘內的，免得開機讀到舊狀態
                if kind == "state", Date().timeIntervalSince(stamp) > 600 { notePet(id, nil, "skipped", "stale"); continue }
                guard let data = try? Data(contentsOf: url), let text = String(data: data, encoding: .utf8) else { continue }
                if kind == "state", let state = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                   let pose = state["pose"] as? String {
                    lastPose[id] = (pose, stamp)
                    // 終端裡的 AI 沒有獨立的前臺 App：跟隨最近一條正在工作的事件。
                    if id != "current", pose != "idle", pose != "sleeping",
                       let ts = state["ts"] as? Double, Date().timeIntervalSince1970 - ts < 30, ts > latestActive.ts {
                        latestActive = (id, ts)
                    }
                }
                if kind == "sessions" { if !pauseRealEvents { js("setSessions(\(quote(id)), \(text))") }; continue }
                devJS("logEvent(\(quote(id)), \(quote(kind)), \(text), \(pauseRealEvents))")
                let parsed = kind == "state" ? (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] : nil
                if pauseRealEvents { if kind == "state" { notePet(id, parsed, "skipped", "paused") }; continue }
                if kind == "state" { notePet(id, parsed, "played") }   // 交給網頁了；網頁自己的判斷（不是當前角色、正在演彩蛋……）在 diagnoseDisplay 裡
                js(kind == "state" ? "applyCharState(\(quote(id)), \(text))" : "setQuota(\(quote(id)), \(text))")
                // Codex 回完一輪：額度記錄剛寫進會話，馬上讀一次（不然要等每分鐘一次的定時重新整理）
                if kind == "state", id == "gpt" {
                    if text.contains("\"event\": \"Stop\"") || text.contains("\"event\":\"Stop\"") {
                        codexBusyAt = nil
                        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5) { [weak self] in self?.refreshCodexQuota(.turnEnd) }
                    } else { codexBusyAt = stamp }   // 在幹活：定時查額度改成 1 分鐘一次（codexTickDue）
                }
            }
        }
        // 同角色的新事件也必須撤銷更早的非同步答覆，否則舊請求可能把她切回另一個角色。
        if let id = latestActive.id { followEvent(id) }
        applyLayer()
        updateDock()
        // 多模型宿主（DeepSeek Harness、WorkBuddy、ZCode，以及接了別家介面的 Claude Code、Codex）正在前臺、
        // 裡面換了模型：馬上換成那個模型的角色
        for host in ["deepseek", "workbuddy", "zcode", "claude", "gpt", "qwen", "cursor"] {
            let url = stateDir.appendingPathComponent("\(host)-host.json")
            guard let stamp = (try? FileManager.default.attributesOfItem(atPath: url.path))?[.modificationDate] as? Date,
                  stamp != stamps["\(host)-host"] else { continue }
            stamps["\(host)-host"] = stamp
            guard followApps, !pauseRealEvents, let front = NSWorkspace.shared.frontmostApplication?.bundleIdentifier,
                  (appToCharacter[front] ?? hostApp(front)) == host else { continue }
            let id = hostCharacter(host)
            if id != currentId, characterIds.contains(id) { switchTo(id, why: "host", via: host) }
        }
    }

    /// 兩端使用網頁層同一份優先順序；這裡只採集前臺程式及設定並執行結果。
    func followEvent(_ id: String) {
        followEventGeneration &+= 1
        let generation = followEventGeneration
        guard ready, !pauseRealEvents, followEvents, id != currentId else { return }
        let front = NSWorkspace.shared.frontmostApplication?.bundleIdentifier
        let host = front.flatMap { appToCharacter[$0] ?? hostApp($0) }
        let foregroundId = host.map { hostCharacter($0) }
        let currentAt = currentId, apps = followApps, events = followEvents
        let input: [String: Any] = ["eventId": id, "foregroundId": foregroundId as Any? ?? NSNull(),
                                    "followApps": apps, "followEvents": events, "characters": characterIds]
        guard let data = try? JSONSerialization.data(withJSONObject: input), let json = String(data: data, encoding: .utf8) else { return }
        web.evaluateJavaScript("FollowPolicy.eventTarget(\(json))") { [weak self] result, _ in
            // 和 Windows 一致：更新的事件、頁面或選擇已經到來時，舊答覆不能再覆蓋它。
            guard let self else { return }
            guard self.followEventGeneration == generation, self.ready, !self.web.isLoading,
                  !self.pauseRealEvents, self.currentId == currentAt,
                  NSWorkspace.shared.frontmostApplication?.bundleIdentifier == front,
                  self.followApps == apps, self.followEvents == events else { self.notePet(id, nil, "skipped", "answer-stale"); return }
            let currentHost = front.flatMap { self.appToCharacter[$0] ?? self.hostApp($0) }
            guard currentHost == host, currentHost.map({ self.hostCharacter($0) }) == foregroundId,
                  self.characterIds.contains(id) else { self.notePet(id, nil, "skipped", "answer-stale"); return }
            guard result as? String == id else { self.notePet(id, nil, "skipped", "front-ai"); return }
            guard id != self.currentId else { return }
            self.switchTo(id, why: "event")
        }
    }

}
