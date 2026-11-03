import Carbon
import Cocoa
import ServiceManagement
import WebKit

// MARK: GPT（Codex）額度來源
// 起 `codex app-server`，問 account/rateLimits/read——Codex 用它自己的登入狀態向 OpenAI 查即時額度
// （等同於在 Codex 裡看使用情況，不對話、不耗額度；CrossPet 不碰憑據）。
// 不再讀本機會話記錄：Codex 回覆了也不一定往裡寫額度，讀出來的常常是舊數還看著像新的（1.3.3 測試裡實測）。
// 排版在網頁 codex-quota.js 裡做，這裡只交原始資料。規則見 docs/superpowers/specs/2026-10-12-codex-quota-design.md
enum CodexQuotaSource {
    static var cachedBinary: String?

    /// 找 codex 程式：測試用的覆蓋 → ChatGPT 桌面版自帶的 → 登入 shell 裡的 → Homebrew 常見位置
    static func findBinary() -> String? {
        let fm = FileManager.default
        if let o = ProcessInfo.processInfo.environment["CROSSPET_CODEX_BIN"] { return fm.isExecutableFile(atPath: o) ? o : nil }
        if let c = cachedBinary, fm.isExecutableFile(atPath: c) { return c }
        cachedBinary = nil
        // 按順序找，找到就停：大多數人有 ChatGPT 桌面版，就不用起登入 shell（.zprofile 裡有 nvm、conda 的會慢）
        let bundled = "/Applications/ChatGPT.app/Contents/Resources/codex-cli/bin/codex"
        if fm.isExecutableFile(atPath: bundled) { cachedBinary = bundled; return bundled }
        if let p = loginShellWhich("codex"), fm.isExecutableFile(atPath: p) { cachedBinary = p; return p }
        cachedBinary = ["/opt/homebrew/bin/codex", "/usr/local/bin/codex"].first { fm.isExecutableFile(atPath: $0) }
        return cachedBinary
    }

    /// GUI 程式的 PATH 不全（npm / nvm 裝的找不到），問一次登入 shell
    static func loginShellWhich(_ name: String) -> String? {
        let p = Process(); p.executableURL = URL(fileURLWithPath: "/bin/zsh"); p.arguments = ["-lc", "command -v \(name)"]
        let out = Pipe(); p.standardOutput = out; p.standardError = FileHandle.nullDevice
        guard (try? p.run()) != nil else { return nil }
        let deadline = Date().addingTimeInterval(5)
        while p.isRunning && Date() < deadline { usleep(50_000) }
        if p.isRunning { p.terminate(); return nil }
        // .zprofile 可能往輸出裡打別的東西：取最後一行
        let s = String(decoding: out.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
            .split(separator: "\n").last.map { $0.trimmingCharacters(in: .whitespaces) } ?? ""
        return s.hasPrefix("/") ? s : nil
    }

    /// 問一次額度。done 在後臺執行緒呼叫且只調一次：(rateLimits, error, 毫秒)
    static func queryLive(binary: String, timeout: TimeInterval = 15, done: @escaping ([String: Any]?, String?, Int) -> Void) {
        let started = Date()
        let p = Process(); p.executableURL = URL(fileURLWithPath: binary); p.arguments = ["app-server"]
        let inPipe = Pipe(), outPipe = Pipe()
        p.standardInput = inPipe; p.standardOutput = outPipe; p.standardError = FileHandle.nullDevice
        let lock = NSLock(); var finished = false; var buffer = Data()
        func finish(_ rl: [String: Any]?, _ err: String?) {
            lock.lock(); defer { lock.unlock() }
            guard !finished else { return }
            finished = true
            outPipe.fileHandleForReading.readabilityHandler = nil
            if p.isRunning { p.terminate() }
            done(rl, err, Int(Date().timeIntervalSince(started) * 1000))
        }
        outPipe.fileHandleForReading.readabilityHandler = { h in
            let chunk = h.availableData
            if chunk.isEmpty { finish(nil, "Codex 服務提前退出"); return }
            buffer.append(chunk)
            while let nl = buffer.firstIndex(of: 0x0A) {
                let line = buffer.subdata(in: buffer.startIndex..<nl); buffer.removeSubrange(buffer.startIndex...nl)
                guard let msg = try? JSONSerialization.jsonObject(with: line) as? [String: Any], (msg["id"] as? Int) == 2 else { continue }
                if let e = msg["error"] as? [String: Any] { finish(nil, e["message"] as? String ?? "Codex 服務返回錯誤"); return }
                let r = msg["result"] as? [String: Any]
                let rl = (r?["rateLimitsByLimitId"] as? [String: Any])?["codex"] as? [String: Any] ?? r?["rateLimits"] as? [String: Any]
                finish(rl, rl == nil ? "Codex 服務沒給額度" : nil); return
            }
        }
        do { try p.run() } catch { finish(nil, "起不來 Codex：\(error.localizedDescription)"); return }
        let send = { (o: [String: Any]) in
            if let d = try? JSONSerialization.data(withJSONObject: o) { try? inPipe.fileHandleForWriting.write(contentsOf: d + Data([0x0A])) }
        }
        send(["id": 1, "method": "initialize", "params": ["clientInfo": ["name": "crosspet", "version": "1"]]])
        send(["method": "initialized"])
        send(["id": 2, "method": "account/rateLimits/read"])
        DispatchQueue.global().asyncAfter(deadline: .now() + timeout) { finish(nil, "超時（\(Int(timeout)) 秒沒回）") }
    }

    /// 執行狀態裡的簡寫：5小時已用 3%，本週已用 40%
    static func brief(_ rl: [String: Any]) -> String {
        ["primary", "secondary"].compactMap { k -> String? in
            guard let w = rl[k] as? [String: Any], let u = w["usedPercent"] as? NSNumber else { return nil }
            let mins = (w["windowDurationMins"] as? NSNumber)?.intValue ?? 0
            return "\(mins >= 10080 ? "本週" : mins >= 1440 ? "今日" : "\(max(1, mins / 60))小時")已用 \(Int(u.doubleValue.rounded()))%"
        }.joined(separator: "，")
    }

    /// 給網頁的 payload（rateLimits 原樣，不含賬號標識——accountId 在 result 裡，不在 rateLimits 裡）
    static func payload(source: String, rateLimits: [String: Any]?, error: String?, ms: Int) -> [String: Any] {
        var p: [String: Any] = ["source": source, "ms": ms, "at": Date().timeIntervalSince1970]
        if let rl = rateLimits { p["rateLimits"] = rl }
        if let e = error { p["error"] = e }
        return p
    }
}
