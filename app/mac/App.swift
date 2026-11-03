import Carbon
import Cocoa
import ServiceManagement
import WebKit

final class App: NSObject, NSApplicationDelegate, WKNavigationDelegate {
    var panel: NSPanel!
    var web: WKWebView!
    var timer: Timer?
    var quotaTimer: Timer?
    var followEventGeneration: UInt64 = 0
    var ready = false {
        didSet { if ready != oldValue { followEventGeneration &+= 1 } }
    }
    var currentId: String? {
        didSet { followEventGeneration &+= 1 }   // 同 id 的手動選擇也要讓舊事件答覆失效。
    }

    let root = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("CrossPet", isDirectory: true)
    var charactersDir: URL { root.appendingPathComponent("characters") }

    var characterIds: [String] = []           // 可切換的角色（不含彩蛋）
    var skinChoices: [String: [(id: String, name: String)]] = [:]   // 角色 → 可換的衣服（皮膚）
    var baseOutfit: [String: String] = [:]   // 原版衣服叫什麼（character.json 的 outfit，比如「女僕裝」）
    var displayNames: [String: String] = [:]  // id → 顯示名（含使用者改的名）
    var appToCharacter: [String: String] = [:]
    var stamps: [String: Date] = [:]
    var pendingSwitch: DispatchWorkItem?
    var latestRelease: (tag: String, url: URL)?
    var manifestJSON = "{}"                    // 最近一次發給桌寵的角色清單，開發者控制台也要用
    var devWindow: NSWindow?
    var devWeb: WKWebView?
    var settingsWindow: NSWindow?
    var settingsWeb: WKWebView?
    var charactersWindow: NSWindow?   // 換角色視窗：和設定頁同一套狀態（pushSettings 兩個都推）
    var charactersWeb: WKWebView?
    var pendingSettingsTab: String?   // 設定視窗載入完後切到哪個標籤頁（從「新功能」卡片開啟時是 news）
    var feedbackWindow: NSWindow?
    var feedbackWeb: WKWebView?
    var defaultNames: [String: String] = [:]   // id → character.json 裡的原名（設定頁顯示佔位用）
    var accents: [String: String] = [:]   // id → character.json 的 accent（設定頁角色卡底色）
    var pauseRealEvents = false {              // 控制台裡測試時，先不讓真實 AI 的狀態打擾桌寵
        didSet { if pauseRealEvents != oldValue { followEventGeneration &+= 1 } }
    }


    // 更新 用的
    let repoSlug = "DuridMing/crosspet"   // maid fork：更新來源指向自己的 fork，不會被上游覆蓋
    var lastUpdateCheck = "還沒查過"
    var updating = false

    // GPT 額度 用的
    var codexLiveAt = Date.distantPast       // 上次問服務的時間（不管成沒成）
    var codexBusyAt: Date?   // Codex 最近一次幹活的事件（不是 Stop）；Stop 後清掉。幹活時額度查得勤一點
    var codexInFlight = false
    var codexLast: (rateLimits: [String: Any], at: Date)?   // 最後一次問到的額度：問不到時接著顯示它，並標上時間
    var codexNote = ""                       // 從沒問到過時的原因，設定裡那個開關下面顯示
    var lastGptQuota = "還沒讀過"
    var lastAgyNote = "還沒查過"   // 最近一次找 Antigravity 服務的結果（反饋「額度不對」用）

    // Gemini 額度 用的
    var agySession: URLSession?
    var agyPort: (pid: Int32, port: Int)?

    // 快捷鍵隱藏 用的
    var userHidden = false {
        didSet { if userHidden != oldValue { applyHiddenState() } }   // 快捷鍵藏起來：和全屏時收著一樣，只是透明
    }
    var hotKeyRef: EventHotKeyRef?
    var hotKeyHandlerInstalled = false
    var hotkeyFailed = false

    // 選單欄圖示 用的
    var statusItem: NSStatusItem?

    // 顯示層級 用的
    var lastPose: [String: (pose: String, at: Date)] = [:]   // 各角色最近一次狀態（判斷 AI 在不在幹活）

    // 貼邊 用的
    var dock: String?            // "left" / "right"
    var dockOut = false          // 跑出來了
    var dockHover = false
    var dockLeaveAt: Date?
    var dockedAt = Date.distantPast   // 剛拖到邊上：之前就在進行的工作不算，先縮排去給個反應，AI 有新動作再跑出來
    var screensAsleep = false
    var wasAway = false
    var dockTucked = false   // 現在是縮著的樣子（之後換探出來不用先挪視窗）
    var dockCut: [String: Double] = [:]   // 切口位置（網頁畫素），按 邊 + 角色 記
    var dockEnterAt: Date?

    // 點選穿透 用的
    var hitRects: [CGRect] = []
    var hitRectsAt = Date.distantPast
    var hiddenForFullscreen = false {
        didSet {   // 透明度 / 滑鼠 / 動畫都跟著這個標記走
            guard hiddenForFullscreen != oldValue else { return }
            appLog(hiddenForFullscreen ? "全屏中，先藏起來（前臺 \(NSWorkspace.shared.frontmostApplication?.bundleIdentifier ?? "-")）" : "退出全屏，出來了")
            applyHiddenState()
        }
    }
    var fullscreenCheckedAt = Date.distantPast
    var fullscreenOverride = false   // 全屏時你點選單欄圖示叫她出來：切換空間 / 前臺程式之前不再收起來

    // 設定 用的
    var scaleTimer: Timer?
    var shownScale: CGFloat = 0
    // 文字大小：網頁按「人物大小、文字大小」算出的畫布（網頁 CSS 尺寸），視窗 = 畫布 × 人物大小（app/web/text-size.js）
    var pageSize = NSSize(width: 260, height: 362)
    var pageFoot: CGFloat = 62    // 畫布底欄高（名牌 + 額度），改文字大小時按它的變化挪視窗，人物不動
    var shownFootPt: CGFloat = 0  // 視窗現在的底欄在螢幕上多高（點）；0 = 還沒按畫布設過

    // 接入 AI 用的
    var integrated: [String]?
    var pluginStatus = "[]"      // 外掛版本（接入工具的只讀檢查 integrate.py plugins，JSON 陣列），桌寵和設定頁據此提醒更新

    // 接入被別的工具沖掉時接回去 用的
    var claudeConfigStamp: Date?
    var healTimes: [Date] = []
    var healPending: DispatchWorkItem?
    lazy var pythonReady: Bool = {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/xcode-select")
        p.arguments = ["-p"]
        p.standardOutput = FileHandle.nullDevice; p.standardError = FileHandle.nullDevice
        do { try p.run(); p.waitUntilExit(); return p.terminationStatus == 0 } catch { return false }
    }()

    // 反饋問題 用的
    let launchedAt = Date()
    var diagText = ""   // 接入工具的診斷（integrate.py diagnose），反饋視窗開啟時跑一次

    // MARK: 啟動

    func applicationDidFinishLaunching(_ note: Notification) {
        syncBundledResources()
        markLaunched()
        try? FileManager.default.createDirectory(at: eventsDir, withIntermediateDirectories: true)   // 鉤子 / 外掛的事件記錄寫在這裡（見 App+Log）
        appLog("啟動 \(currentVersion)，macOS \(ProcessInfo.processInfo.operatingSystemVersionString)")

        let size = NSSize(width: Self.baseSize.width * petScale, height: Self.baseSize.height * petScale)
        var origin = NSPoint.zero
        if let saved = defaults.array(forKey: "origin") as? [Double], saved.count == 2 {
            origin = NSPoint(x: saved[0], y: saved[1])
        } else if let screen = NSScreen.main?.visibleFrame {
            origin = NSPoint(x: screen.maxX - size.width - 40, y: screen.minY + 100)
        }
        panel = NSPanel(contentRect: NSRect(origin: origin, size: size),
                        styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = layerMode == "normal" ? .normal : .floating
        panel.hidesOnDeactivate = false
        applyFullscreenBehavior()

        let container = NSView(frame: NSRect(origin: .zero, size: size))
        let petConfig = WKWebViewConfiguration()
        petConfig.userContentController.add(DevBridge(app: self, kind: .pet), name: "pet")   // 桌寵頁面 → App（「新功能」卡片的「去看看」）
        web = WKWebView(frame: container.bounds, configuration: petConfig)
        web.setValue(false, forKey: "drawsBackground")
        web.autoresizingMask = [.width, .height]
        web.navigationDelegate = self
        web.pageZoom = petScale
        web.loadFileURL(root.appendingPathComponent("web/index.html"), allowingReadAccessTo: root)
        container.addSubview(web)

        let drag = DragView(frame: container.bounds)
        drag.autoresizingMask = [.width, .height]
        // 點的位置換成網頁座標（CSS 畫素：除掉人物大小的縮放，y 軸翻過來）
        drag.onClick = { [weak self, weak drag] p in
            guard let self, let drag else { return }
            let z = max(self.web.pageZoom, 0.01)
            self.js(String(format: "handleClick('pat', %.1f, %.1f)", p.x / z, (drag.bounds.height - p.y) / z))
        }
        drag.onMenu = { [weak self] e in self?.showMenu(e, in: drag) }
        drag.onDragStart = { [weak self] in self?.undock() }
        drag.onDragEnd = { [weak self] in self?.checkDock() }
        drag.onShake = { [weak self] in self?.js("shaken()") }
        container.addSubview(drag)

        panel.contentView = container
        panel.orderFrontRegardless()
        setupStatusItem()
        registerHotKey()

        NSWorkspace.shared.notificationCenter.addObserver(
            self, selector: #selector(appActivated(_:)),
            name: NSWorkspace.didActivateApplicationNotification, object: nil)
        NSWorkspace.shared.notificationCenter.addObserver(
            self, selector: #selector(spaceChanged(_:)),
            name: NSWorkspace.activeSpaceDidChangeNotification, object: nil)
        // 看不見時停動畫：視窗被完全擋住、螢幕休眠、鎖屏（鎖屏時會話失活）
        NotificationCenter.default.addObserver(forName: NSWindow.didChangeOcclusionStateNotification, object: panel, queue: .main) { [weak self] _ in self?.updatePageHidden() }
        let ws = NSWorkspace.shared.notificationCenter
        for (name, asleep) in [(NSWorkspace.screensDidSleepNotification, true), (NSWorkspace.screensDidWakeNotification, false),
                               (NSWorkspace.sessionDidResignActiveNotification, true), (NSWorkspace.sessionDidBecomeActiveNotification, false)] {
            ws.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in self?.screensAsleep = asleep; self?.updatePageHidden() }
        }
        timer = Timer.scheduledTimer(withTimeInterval: 0.3, repeats: true) { [weak self] _ in self?.pollStates() }
        Timer.scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] _ in self?.updateClickThrough() }
        quotaTimer = Timer.scheduledTimer(withTimeInterval: 60, repeats: true) { [weak self] _ in self?.refreshCodexQuota(.tick); self?.refreshAntigravityQuota() }
        Timer.scheduledTimer(withTimeInterval: 3, repeats: true) { [weak self] _ in self?.watchClaudeConfig() }
        // 檢查更新：啟動後 20 秒、之後每 3 小時，電腦從睡眠中醒來時也查一次（很多人的電腦一直不關機，只是合蓋）
        Timer.scheduledTimer(withTimeInterval: 3 * 3600, repeats: true) { [weak self] _ in self?.checkForUpdate(manual: false) }
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            DispatchQueue.main.asyncAfter(deadline: .now() + 30) { self?.checkForUpdate(manual: false) }   // 等網路連上
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 20) { [weak self] in self?.checkForUpdate(manual: false) }
    }

    // MARK: 工具

    func js(_ code: String) { web.evaluateJavaScript(code, completionHandler: nil) }

}
