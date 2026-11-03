import Foundation

extension App {
    // MARK: 執行日誌
    // ~/Library/Logs/CrossPet.log：和 Windows 的 windows.log 同一種寫法（一行一條帶時間，超過 1 MB 刪掉重來）。
    // 反饋時按問題型別篩出相關的幾十行（規則在 web/feedback-plan.js）。只記開關、時間和出錯原因，不記對話
    static let logURL = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Logs/CrossPet.log")
    static let logQueue = DispatchQueue(label: "crosspet.log")

    func appLog(_ text: String) {
        let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd HH:mm:ss"
        let line = "\(f.string(from: Date())) \(text)\n"
        Self.logQueue.async {
            let url = Self.logURL, fm = FileManager.default
            if let size = (try? fm.attributesOfItem(atPath: url.path))?[.size] as? Int, size > 1_000_000 { try? fm.removeItem(at: url) }
            if let h = try? FileHandle(forWritingTo: url) { _ = try? h.seekToEnd(); try? h.write(contentsOf: Data(line.utf8)); try? h.close() }   // 出錯不拋 ObjC 異常（見 Ledger）
            else { try? Data(line.utf8).write(to: url) }
        }
    }

    /// 鉤子事件記錄、桌寵處理記錄所在的資料夾（資料目錄 events/）。啟動時建好：Claude Code 的 mod 只能寫檔案、不能建資料夾
    var eventsDir: URL { root.appendingPathComponent("events", isDirectory: true) }
    var petEventsURL: URL { eventsDir.appendingPathComponent("pet.jsonl") }
    var switchesURL: URL { root.appendingPathComponent("switches.jsonl") }

    func notePet(_ id: String, _ state: [String: Any]?, _ result: String, _ detail: String = "") {
        Ledger.append(petEventsURL, ["char": id, "ev": state?["event"] as? String ?? "", "pose": state?["pose"] as? String ?? "",
                                     "result": result, "detail": detail])
    }
}
