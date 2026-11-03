import Carbon
import Cocoa
import ServiceManagement
import WebKit

extension App {
    // MARK: 開發者控制台
    // 一個獨立視窗（web/devconsole.html），直接呼叫桌寵頁面裡真實的動作程式碼：逐個檢查姿態、跑場景、記反饋。
    var feedbackURL: URL { root.appendingPathComponent("dev-feedback.md") }

    func devJS(_ code: String) { devWeb?.evaluateJavaScript(code, completionHandler: nil) }

    @objc func openDevConsole() {
        if let w = devWindow {
            NSApp.activate(ignoringOtherApps: true)
            w.makeKeyAndOrderFront(nil)
            return
        }
        let config = WKWebViewConfiguration()
        config.userContentController.add(DevBridge(app: self, kind: .dev), name: "dev")
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1060, height: 760),
                         styleMask: [.titled, .closable, .resizable, .miniaturizable], backing: .buffered, defer: false)
        w.title = "CrossPet 開發者控制台"
        w.isReleasedWhenClosed = false
        w.center()
        let v = WKWebView(frame: w.contentView!.bounds, configuration: config)
        v.autoresizingMask = [.width, .height]
        v.navigationDelegate = self
        v.loadFileURL(root.appendingPathComponent("web/devconsole.html"), allowingReadAccessTo: root)
        w.contentView!.addSubview(v)
        devWindow = w
        devWeb = v
        NotificationCenter.default.addObserver(forName: NSWindow.willCloseNotification, object: w, queue: .main) { [weak self] _ in
            guard let self else { return }
            self.pauseRealEvents = false
            self.js("devRelease()")
            self.devWeb = nil
            self.devWindow = nil
        }
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
    }

    func handleDev(_ body: Any) {
        guard let msg = body as? [String: Any], let cmd = msg["cmd"] as? String else { return }
        switch cmd {
        case "pet":       // 在桌寵頁面裡執行一段程式碼（控制台自己的頁面發來的，只在本機）
            if let code = msg["code"] as? String { js(code) }
        case "status":    // 讀桌寵當前狀態，回給控制台
            web.evaluateJavaScript("JSON.stringify(devStatus())") { [weak self] r, _ in
                if let text = r as? String { self?.devJS("onPetStatus(\(text))") }
            }
        case "pause":
            pauseRealEvents = msg["on"] as? Bool ?? false
        case "save":
            let text = msg["text"] as? String ?? ""
            try? text.write(to: feedbackURL, atomically: true, encoding: .utf8)
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(text, forType: .string)
            devJS("onSaved(\(quote(feedbackURL.path)))")
        case "reveal":
            NSWorkspace.shared.activateFileViewerSelecting([feedbackURL])
        case "reload":
            web.reload()
        default:
            break
        }
    }
    func quote(_ s: String) -> String {
        let data = try? JSONSerialization.data(withJSONObject: [s])
        return String(data: data ?? Data(), encoding: .utf8).map { String($0.dropFirst().dropLast()) } ?? "\"\""
    }
}
