import Foundation

/// 記錄檔案（反饋用）：一行一個 JSON，只留最近 200 行（超過 240 行時截回 200）。
/// 桌寵處理記錄 events/pet.jsonl、換角色記錄 switches.jsonl 都用它；格式見 docs/架构.md「記錄」。寫不了就算了，不影響桌寵
enum Ledger {
    static func append(_ url: URL, _ line: [String: Any], keep: Int = 200) {
        var row = line
        row["t"] = (Date().timeIntervalSince1970 * 100).rounded() / 100
        guard let data = try? JSONSerialization.data(withJSONObject: row, options: [.sortedKeys]),
              let text = String(data: data, encoding: .utf8) else { return }
        let fm = FileManager.default
        try? fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        // 用會拋 Swift 錯誤的介面：老的 seekToEndOfFile / write(_:) 出錯（磁碟滿、沒許可權）時拋 ObjC 異常，整個桌寵會閃退
        if let h = try? FileHandle(forWritingTo: url) {
            _ = try? h.seekToEnd(); try? h.write(contentsOf: Data((text + "\n").utf8)); try? h.close()
        } else {
            try? Data((text + "\n").utf8).write(to: url)
        }
        guard let all = try? String(contentsOf: url, encoding: .utf8) else { return }
        let rows = all.split(separator: "\n", omittingEmptySubsequences: true)
        if rows.count > 240 { try? (rows.suffix(keep).joined(separator: "\n") + "\n").write(to: url, atomically: true, encoding: .utf8) }
    }

    /// 檔案最後 n 行（沒有檔案返回空串）
    static func tail(_ url: URL, _ n: Int) -> String {
        guard let all = try? String(contentsOf: url, encoding: .utf8) else { return "" }
        return all.split(separator: "\n", omittingEmptySubsequences: false).suffix(n).joined(separator: "\n")
    }
}
