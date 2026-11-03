import Carbon
import Cocoa
import ServiceManagement
import WebKit

// 往已經退出的子程式（codex app-server）寫東西時，系統預設發 SIGPIPE 把整個 App 殺掉：忽略它，寫失敗只返回錯誤
signal(SIGPIPE, SIG_IGN)

// 測試用：只問一次 GPT 額度，列印 JSON 就退出（tests/codex_quota_probe.sh）
func probeResult(_ rl: [String: Any]?, _ err: String?, _ ms: Int) -> [String: Any] {
    CodexQuotaSource.payload(source: rl != nil ? "live" : "none", rateLimits: rl, error: rl != nil ? nil : err, ms: ms)
}
if CommandLine.arguments.contains("--codex-quota-probe") {
    let sem = DispatchSemaphore(value: 0); var out: [String: Any] = [:]
    if let bin = CodexQuotaSource.findBinary() {
        // CROSSPET_CODEX_TIMEOUT：測試時把 15 秒超時縮短
        let timeout = Double(ProcessInfo.processInfo.environment["CROSSPET_CODEX_TIMEOUT"] ?? "") ?? 15
        CodexQuotaSource.queryLive(binary: bin, timeout: timeout) { rl, err, ms in
            out = probeResult(rl, err, ms); sem.signal()
        }
        sem.wait()
    } else { out = probeResult(nil, "沒找到 codex 程式", 0) }
    let d = try! JSONSerialization.data(withJSONObject: out, options: [.sortedKeys])
    print(String(decoding: d, as: UTF8.self)); exit(0)
}

// 測試用：定時到點時要不要問 GPT 額度，列印 yes / no 就退出（tests/codex_quota_probe.sh）。引數：離上次問過幾秒、離上次幹活幾秒（-1 = 沒有）
if let i = CommandLine.arguments.firstIndex(of: "--codex-tick-probe") {
    let n = CommandLine.arguments.dropFirst(i + 1).compactMap { Double($0) }
    guard n.count == 2 else { print("引數不對"); exit(1) }
    print(App.codexTickDue(since: n[0], busyAgo: n[1] < 0 ? nil : n[1]) ? "yes" : "no"); exit(0)
}

// 測試用：把一份 Antigravity 額度回覆排版，列印結果 JSON 就退出（tests/antigravity_quota_probe.sh）
if let i = CommandLine.arguments.firstIndex(of: "--agy-quota-probe") {
    guard i + 1 < CommandLine.arguments.count, let data = FileManager.default.contents(atPath: CommandLine.arguments[i + 1]),
          let obj = try? JSONSerialization.jsonObject(with: data) else { print("null"); exit(1) }
    let q = App.antigravityParse(obj) ?? [:]
    print(String(decoding: try! JSONSerialization.data(withJSONObject: q), as: UTF8.self)); exit(0)
}

// 測試用：算一次「換大小時視窗放在哪」，列印 x y 寬 高 就退出（tests/window_place_probe.sh）
if let i = CommandLine.arguments.firstIndex(of: "--window-place-probe") {
    let a = Array(CommandLine.arguments.dropFirst(i + 1))
    let n = a.dropFirst().compactMap { Double($0) }.map { CGFloat($0) }
    guard let anchor = App.Anchor(rawValue: a.first ?? ""), n.count == 9 else { print("引數不對"); exit(1) }
    let r = App.placeWindow(old: NSRect(x: n[0], y: n[1], width: n[2], height: n[3]), size: NSSize(width: n[4], height: n[5]),
                            oldFoot: n[6], newFoot: n[7], anchor: anchor, visibleMinY: n[8])
    print([r.minX, r.minY, r.width, r.height].map { String(format: "%g", Double($0)) }.joined(separator: " ")); exit(0)
}

let app = NSApplication.shared
let delegate = App()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()

/// 只信任本機 127.0.0.1 的自簽名證書（Antigravity 後臺服務用的是自簽名 HTTPS）
