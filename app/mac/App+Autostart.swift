import Carbon
import Cocoa
import ServiceManagement
import WebKit

extension App {
    // MARK: 跟著 AI 出現
    // 接入的 AI 開始工作時，鉤子指令碼 / DSH 外掛發現桌寵沒在跑就把它開啟（看 /tmp/crosspet/pet.pid）。
    // 從選單手動退出 = 這會兒不想看到它：記下 user-quit（看檔案修改時間），在那之前就開著的 AI 不再把它拉起來；
    // AI 程式關掉重開、重啟電腦後就作廢，自己開啟桌寵也會清掉。
    // 設定裡關掉「AI 開始工作時自動出現」= 寫 ai-autostart-off。兩個記號都放在 Application Support，重啟電腦也不丟。
    var userQuitFlag: URL { root.appendingPathComponent("user-quit") }
    var aiAutostartOffFlag: URL { root.appendingPathComponent("ai-autostart-off") }
    var aiAutostart: Bool { !FileManager.default.fileExists(atPath: aiAutostartOffFlag.path) }
    func setAIAutostart(_ on: Bool) {
        if on { try? FileManager.default.removeItem(at: aiAutostartOffFlag) }
        else { try? Data().write(to: aiAutostartOffFlag) }
    }
    func markLaunched() {
        try? FileManager.default.removeItem(at: userQuitFlag)
        try? FileManager.default.createDirectory(at: stateDir, withIntermediateDirectories: true)
        try? String(ProcessInfo.processInfo.processIdentifier).write(to: stateDir.appendingPathComponent("pet.pid"), atomically: true, encoding: .utf8)
    }
    func applicationWillTerminate(_ note: Notification) {
        try? FileManager.default.removeItem(at: stateDir.appendingPathComponent("pet.pid"))
    }

    /// 選單裡的「退出」：使用者主動退出。第一次退出時說一句，讓人知道什麼時候會再出來
    @objc func quit() {
        try? Data().write(to: userQuitFlag)
        // 揮手道別再走；第一次退出時說明什麼時候會再出來，多停一會兒讓人看清
        let hint = aiAutostart && !defaults.bool(forKey: "quitHinted")
        if hint { defaults.set(true, forKey: "quitHinted") }
        js(hint ? "farewell('我先走啦～下次重新開啟 AI 我再出來', 5000)" : "farewell(null, 2000)")
        DispatchQueue.main.asyncAfter(deadline: .now() + (hint ? 5 : 2)) { NSApp.terminate(nil) }
    }

}
