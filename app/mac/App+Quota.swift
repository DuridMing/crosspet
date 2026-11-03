import Carbon
import Cocoa
import ServiceManagement
import WebKit

extension App {
    // MARK: GPT 額度（可選，預設關）：什麼時候拿、從哪拿（來源見檔案末尾 CodexQuotaSource）
    enum CodexWhy { case now, turnEnd, tick }

    /// 開啟額度顯示 / 頁面就緒：馬上問；Codex 回完一輪：隔 20 秒以上才問；定時（每分鐘）：見 codexTickDue。
    /// 藏著、螢幕休眠時定時的不查（和 Windows 版 Pet.Quota.cs 一致）
    func refreshCodexQuota(_ why: CodexWhy) {
        guard ready, defaults.bool(forKey: "gptQuota"), !codexInFlight else { return }
        if why == .tick && (screensAsleep || userHidden) { return }
        let since = Date().timeIntervalSince(codexLiveAt)
        let askLive: Bool
        switch why {
        case .now: askLive = true
        case .turnEnd: askLive = since >= 20
        case .tick: askLive = Self.codexTickDue(since: since, busyAgo: codexBusyAt.map { Date().timeIntervalSince($0) })
        }
        if !askLive { return }
        codexLiveAt = Date()
        codexInFlight = true
        // 找程式可能要起一次登入 shell，放到後臺，別卡住桌寵。程式被更新挪走的話 findBinary 下次會發現路徑不在、重新找
        DispatchQueue.global(qos: .utility).async {
            let finish = { (rl: [String: Any]?, err: String?, ms: Int) in
                DispatchQueue.main.async {
                    self.codexInFlight = false
                    self.applyCodex(rateLimits: rl, error: err, ms: ms)
                }
            }
            guard let bin = CodexQuotaSource.findBinary() else { finish(nil, "沒找到 codex 程式", 0); return }
            CodexQuotaSource.queryLive(binary: bin, done: finish)
        }
    }

    /// 定時到點時要不要問：Codex 在幹活（10 分鐘內有不是 Stop 的事件，長命令中途可能好幾分鐘沒事件）1 分鐘一次，
    /// 閒著 2 分鐘一次（別處用了 Codex 也能較快跟上）。實測一次約 0.15 秒 CPU，每分鐘一次平均約 0.25% 一個核心。
    /// 定時器每 60 秒一跳會有一兩秒誤差，門檻各留 5 秒（tests/codex_quota_probe.sh 測）
    static func codexTickDue(since: TimeInterval, busyAgo: TimeInterval?) -> Bool {
        let busy = busyAgo.map { $0 < 600 } ?? false
        return since >= (busy ? 55 : 115)
    }

    /// 交給網頁：問到了就是 live；沒問到、但以前問到過，就接著顯示最後一次的數（stale，網頁標上是幾點的資料）；從沒問到過就不顯示
    func applyCodex(rateLimits: [String: Any]?, error: String?, ms: Int) {
        guard defaults.bool(forKey: "gptQuota") else { return }   // 查的途中關掉了額度顯示
        if let rl = rateLimits { codexLast = (rl, Date()); codexNote = "" }
        else if codexLast == nil { codexNote = "問不到 Codex：\(error ?? "原因不明")" }
        if rateLimits == nil { appLog("GPT 額度查詢失敗：\(error ?? "原因不明")") }
        let tag = rateLimits != nil ? "Codex 服務，\(String(format: "%.1f", Double(ms) / 1000)) 秒" : "沒問到：\(error ?? "原因不明")"
        lastGptQuota = "\(Self.clock()) （\(tag)）" + (codexLast.map { (rateLimits == nil ? "沿用 \(Self.hm($0.at)) 的數：" : "") + CodexQuotaSource.brief($0.rateLimits) } ?? "")
        pushSettings()
        guard let last = codexLast else { js("setCodexQuota(null)"); return }
        var p = CodexQuotaSource.payload(source: rateLimits != nil ? "live" : "stale", rateLimits: last.rateLimits, error: error, ms: ms)
        p["at"] = last.at.timeIntervalSince1970
        guard let d = try? JSONSerialization.data(withJSONObject: p), let arg = String(data: d, encoding: .utf8) else { return }
        js("setCodexQuota(\(arg))")
    }

    // MARK: Gemini 額度（可選，預設關）
    // Antigravity 在本機跑著一個後臺服務（language_server），它自己的介面就是向它要額度的。
    // 這裡做同樣的事：從它的啟動引數裡取本機介面的防偽令牌（每次啟動隨機生成、只在本機有效，不是賬號憑據），
    // 只調「使用者狀態」介面、只取額度數字；令牌只在記憶體裡用，不存檔、不外傳。Antigravity 沒開就不顯示。

    func refreshAntigravityQuota() {
        guard ready, defaults.bool(forKey: "agyQuota") else { return }
        DispatchQueue.global(qos: .utility).async { [weak self] in
            guard let self else { return }
            guard let (pid, token) = self.antigravityServer() else {
                DispatchQueue.main.async { self.js("setQuota('gemini', null)"); self.lastAgyNote = "\(Self.clock()) 沒找到 Antigravity 的服務程式" }
                return
            }
            let ports = self.agyPort?.pid == pid ? [self.agyPort!.port] : self.listeningPorts(pid)
            DispatchQueue.main.async { self.lastAgyNote = "\(Self.clock()) 找到服務程式，監聽埠 \(ports.count) 個" }
            self.askAntigravity(pid: pid, token: token, ports: ports)
        }
    }

    /// 找到 Antigravity 的 language_server 程式，返回 (pid, 令牌)
    func antigravityServer() -> (Int32, String)? {
        guard let out = run("/bin/ps", ["-axo", "pid=,command="]) else { return nil }
        for line in out.split(separator: "\n") where line.contains("Antigravity.app/Contents/Resources/bin/language_server") {
            let parts = line.split(separator: " ").map(String.init)
            guard let pid = Int32(parts.first ?? "") else { continue }
            for (i, a) in parts.enumerated() {
                if a.hasPrefix("--csrf_token=") { return (pid, String(a.dropFirst("--csrf_token=".count))) }
                if a == "--csrf_token", i + 1 < parts.count { return (pid, parts[i + 1]) }
            }
        }
        return nil
    }

    func listeningPorts(_ pid: Int32) -> [Int] {
        guard let out = run("/usr/sbin/lsof", ["-nP", "-a", "-p", "\(pid)", "-iTCP", "-sTCP:LISTEN"]) else { return [] }
        return out.split(separator: "\n").dropFirst().compactMap { line in
            line.split(separator: " ").first { $0.contains("127.0.0.1:") }.flatMap { Int($0.split(separator: ":").last ?? "") }
        }
    }

    func run(_ path: String, _ args: [String]) -> String? {
        let p = Process(), pipe = Pipe()
        p.executableURL = URL(fileURLWithPath: path); p.arguments = args
        p.standardOutput = pipe; p.standardError = FileHandle.nullDevice
        guard (try? p.run()) != nil else { return nil }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return String(data: data, encoding: .utf8)
    }

    /// 每個埠先問「額度彙總」（RetrieveUserQuotaSummary：和 Antigravity 的「View Usage」同一份資料，剛對話完就更新），
    /// 舊版 Antigravity 沒有這個介面時退回「使用者狀態」（GetUserStatus：裡面的額度是模型列表順帶的一份快取，會慢幾分鐘）。
    /// 2026-10-14 實測：View Usage 已經 98%，使用者狀態裡還是 100%。和 Windows 版 AntigravityQuota.Read 一致
    static let agyMethods = ["RetrieveUserQuotaSummary", "GetUserStatus"]
    func askAntigravity(pid: Int32, token: String, ports: [Int], method: Int = 0) {
        guard let port = ports.first else { return }
        var req = URLRequest(url: URL(string: "https://127.0.0.1:\(port)/exa.language_server_pb.LanguageServerService/\(Self.agyMethods[method])")!)
        req.httpMethod = "POST"
        req.timeoutInterval = 5
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        req.setValue(token, forHTTPHeaderField: "X-Codeium-Csrf-Token")
        req.httpBody = #"{"metadata":{"ideName":"antigravity","extensionName":"antigravity","locale":"en"}}"#.data(using: .utf8)
        if agySession == nil { agySession = URLSession(configuration: .ephemeral, delegate: LocalhostTrust(), delegateQueue: nil) }
        agySession!.dataTask(with: req) { [weak self] data, resp, _ in
            guard let self else { return }
            guard (resp as? HTTPURLResponse)?.statusCode == 200, let data,
                  let obj = try? JSONSerialization.jsonObject(with: data), let q = Self.antigravityParse(obj) else {
                if method + 1 < Self.agyMethods.count {   // 這個埠換舊介面再問一次
                    self.askAntigravity(pid: pid, token: token, ports: ports, method: method + 1); return
                }
                self.agyPort = nil
                self.askAntigravity(pid: pid, token: token, ports: Array(ports.dropFirst()))  // 換下一個埠試
                return
            }
            self.agyPort = (pid, port)
            guard let json = try? JSONSerialization.data(withJSONObject: q), let arg = String(data: json, encoding: .utf8) else { return }
            DispatchQueue.main.async { self.js("setQuota('gemini', \(arg))") }
        }.resume()
    }

    /// 額度彙總或使用者狀態，哪種都認（彙總優先）
    static func antigravityParse(_ obj: Any) -> [String: Any]? { antigravitySummary(obj) ?? antigravityQuota(obj) }

    /// 額度彙總（RetrieveUserQuotaSummary）：Antigravity 自己分好組（「Gemini Models」「Claude and GPT models」「Gemini 4 Argon」……），
    /// 每組一個或幾個視窗（5h / 本週 / 今日……）。交出兩樣東西：
    /// - text：關閉「合併顯示各家額度」時用的，每組一行，一個組只留一個視窗（見下面的挑法），用「｜」隔開
    /// - bars：結構化的額度條（和別家的 bars 同一個格式）：每個視窗一項 {label, left, reset?, group}，同一個池的視窗挨著排（短視窗在前）。
    ///   label = 「池名 視窗名」（Gemini 5小時 / Argon 本週 / Claude/GPT 本週）；left 是剩餘比例 0–1；reset 是重置時刻（Unix 秒，沒有就不寫）；
    ///   group 是池名（額外加的，網頁層不用去拆 label）。合併顯示怎麼排（Gemini + Arg 一行、Agy C/G 另起一行）由網頁層按 bars 決定
    static func antigravitySummary(_ obj: Any) -> [String: Any]? {
        guard let groups = ((obj as? [String: Any])?["response"] as? [String: Any])?["groups"] as? [[String: Any]] else { return nil }
        let iso = ISO8601DateFormatter()
        let fmt = DateFormatter()
        typealias Cell = (label: String, left: Double, reset: Date?)
        func piece(_ c: Cell) -> String {
            var s = "\(c.label) 剩餘 \(Int((c.left * 100).rounded()))%"
            if let d = c.reset {   // 每行都標重置時間（5h 視窗很快就重置，知道多久能用上比知道還剩多少更有用）
                fmt.dateFormat = d.timeIntervalSinceNow < 86400 ? "HH:mm" : "M/d"
                s += " · \(fmt.string(from: d)) 重置"
            }
            return s
        }
        var rows: [Cell] = []
        var bars: [[String: Any]] = []
        let sorted = groups.enumerated().sorted { a, b in
            let ga = (a.element["displayName"] as? String ?? "").contains("Gemini") ? 0 : 1, gb = (b.element["displayName"] as? String ?? "").contains("Gemini") ? 0 : 1
            return ga != gb ? ga < gb : a.offset < b.offset
        }.map { $0.element }
        for g in sorted {
            let title = g["displayName"] as? String ?? ""
            // 只合並普通的兩個池；「Gemini 4 Argon」這類獨立額度池保留 Antigravity 給的名字，不然會和普通 Gemini 分不出來
            let isGemini = title.lowercased() == "gemini models", isGPT = !isGemini && (title.contains("Claude") || title.contains("GPT"))
            let name = isGemini ? "Gemini" : isGPT ? "Claude/GPT" : title
            let buckets = g["buckets"] as? [[String: Any]] ?? []
            var cands: [(win: String, window: String, left: Double, reset: Date?)] = []
            for b in buckets {
                // 比例為 0 時會被省略（protobuf 轉 JSON 不寫預設值）：視窗在、比例不在 = 用完了
                let left = (b["remainingFraction"] as? NSNumber)?.doubleValue ?? 0
                // 認得的視窗寫中文（和 Claude / GPT 的「5小時」「本週」一致）；不認得的先用介面給的名字，再不行用視窗 id，不留空
                let win = b["window"] as? String ?? ""
                let window = ["weekly": "本週", "daily": "今日", "5h": "5小時"][win] ?? ((b["displayName"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? win)
                cands.append((win, window, left, (b["resetTime"] as? String).flatMap { iso.date(from: $0) }))
            }
            // 每組在 text 裡只留一行：
            // 有 5h 視窗、且 5h 沒滿 → 顯示 5h（和它的重置時間）；5h 滿了 → 顯示別的視窗裡最緊的（一般是週額度）；
            // 例外：最緊的視窗 ≤10% 時不管 5h 怎樣都顯示它，週額度快用完了不能被 5h 蓋住。
            // 一樣緊的視窗取重置得更晚的（真正限制你什麼時候能繼續用的是它），不看介面給的順序
            let tightest = cands.reduce(nil as (win: String, window: String, left: Double, reset: Date?)?) { best, c in
                guard let b = best else { return c }
                return c.left < b.left || (c.left == b.left && (c.reset ?? .distantPast) > (b.reset ?? .distantPast)) ? c : b
            }
            guard let t = tightest else { continue }
            let five = cands.first { $0.win == "5h" }
            let pick = t.left > 0.1 && five != nil && five!.left < 1 ? five! : t
            rows.append((cands.count > 1 ? "\(name) \(pick.window)" : name, pick.left, pick.reset))
            // bars：每個視窗一項（不只是挑出來的那個），短視窗在前
            let pool = isGemini ? "Gemini" : isGPT ? "Claude/GPT" : title.replacingOccurrences(of: "^Gemini\\s+(\\d+\\s+)?", with: "", options: .regularExpression)
            let order = ["5h": 0, "daily": 1, "weekly": 2]
            for c in cands.sorted(by: { (order[$0.win] ?? 3) < (order[$1.win] ?? 3) }) {
                var bar: [String: Any] = ["label": "\(pool) \(c.window)", "left": c.left, "group": pool]
                if let d = c.reset { bar["reset"] = Int(d.timeIntervalSince1970) }
                bars.append(bar)
            }
        }
        guard !rows.isEmpty else { return nil }
        return ["text": rows.map(piece).joined(separator: " ｜ "), "low": (rows.map { $0.left }.min() ?? 1) <= 0.1, "bars": bars]
    }

    /// 使用者狀態（GetUserStatus，舊版 Antigravity 的退路）：額度按「額度池」共享：Gemini 模型一個池，Claude 和 GPT 模型一個池（Antigravity 的「View Usage」也是這兩組）。
    /// 固定顯示這兩行，Gemini 在前。
    /// 不寫死「5 小時 / 每週」：24 小時內重置顯示時刻，否則顯示日期，免費版和會員都適用。
    static func antigravityQuota(_ obj: Any) -> [String: Any]? {
        guard let st = (obj as? [String: Any])?["userStatus"] as? [String: Any],
              let cfg = st["cascadeModelConfigData"] as? [String: Any],
              let models = cfg["clientModelConfigs"] as? [[String: Any]] else { return nil }
        // 固定兩行，和 Antigravity 自己「View Usage」的分組一樣：Gemini 模型一行，Claude 和 GPT 模型一行。
        // 數字相同也不合並（以前按「剩餘 + 重置時間」合併，行數會變來變去，和 Antigravity 對不上）。和 Windows 版 AntigravityQuota.Parse 一致
        var groups: [(name: String, left: Double, reset: Date?)] = []
        let iso = ISO8601DateFormatter()
        for m in models {
            guard let qi = m["quotaInfo"] as? [String: Any] else { continue }
            // 不可用的模型不算：比如只給 Ultra 套餐的「Gemini 4 Argon」，別的賬號裡標著 disabled、額度資訊是空的，
            // 以前被當成「剩 0%」排在第一行（2026-10-14 兩位使用者反饋）
            if m["disabled"] as? Bool == true || (qi["remainingFraction"] == nil && qi["resetTime"] == nil) { continue }
            let left = (qi["remainingFraction"] as? NSNumber)?.doubleValue ?? 0  // 用完時這個欄位會被省略（但還有重置時間）
            let reset = (qi["resetTime"] as? String).flatMap { iso.date(from: $0) }
            let name = (m["label"] as? String ?? "").hasPrefix("Gemini") ? "Gemini" : "Claude/GPT"
            if let i = groups.firstIndex(where: { $0.name == name }) {
                if left < groups[i].left { groups[i].left = left; groups[i].reset = reset ?? groups[i].reset }
            } else {
                groups.append((name, left, reset))
            }
        }
        guard !groups.isEmpty else { return nil }
        groups.sort { ($0.name == "Gemini" ? 0 : 1) < ($1.name == "Gemini" ? 0 : 1) }
        let minLeft = groups.map { $0.left }.min() ?? 1
        let fmt = DateFormatter()
        let parts = groups.enumerated().map { i, g -> String in
            var piece = "\(g.name) 剩餘 \(Int((g.left * 100).rounded()))%"
            // 重置時間標在第一行（Gemini）上；別的那行快用完了也標
            if i == 0 || g.left <= 0.1, let d = g.reset {
                fmt.dateFormat = d.timeIntervalSinceNow < 86400 ? "HH:mm" : "M/d"
                piece += " · \(fmt.string(from: d)) 重置"
            }
            return piece
        }
        return ["text": parts.joined(separator: " ｜ "), "low": minLeft <= 0.1]
    }

}
