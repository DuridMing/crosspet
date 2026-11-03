import Carbon
import Cocoa
import ServiceManagement
import WebKit

final class LocalhostTrust: NSObject, URLSessionDelegate {
    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge) async
        -> (URLSession.AuthChallengeDisposition, URLCredential?) {
        if challenge.protectionSpace.host == "127.0.0.1", let trust = challenge.protectionSpace.serverTrust {
            return (.useCredential, URLCredential(trust: trust))
        }
        return (.performDefaultHandling, nil)
    }
}

/// 控制台頁面 → App 的訊息通道（單獨一個物件，避免 WKUserContentController 強引用 App 造成迴圈）
final class DevBridge: NSObject, WKScriptMessageHandler {
    enum Kind { case dev, settings, feedback, pet }
    weak var app: App?
    let kind: Kind
    init(app: App, kind: Kind) { self.app = app; self.kind = kind }
    func userContentController(_ c: WKUserContentController, didReceive message: WKScriptMessage) {
        switch kind {
        case .dev: app?.handleDev(message.body)
        case .settings: app?.handleSettings(message.body)
        case .feedback: app?.handleFeedback(message.body)
        case .pet: app?.handlePet(message.body)
        }
    }
}
