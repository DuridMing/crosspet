import Carbon
import Cocoa
import ServiceManagement
import WebKit

extension App {
    // MARK: 角色

    /// 設定「角色」頁卡片牆用的圖：角色資料夾裡的展示立繪 portrait（衣服卡）、探頭頭像 avatar（角色卡）和待機圖 idle，只給存在的檔案
    /// （頁面按「展示圖 → 待機圖 → 色塊」退）。和 Windows 版 CardImages.Of 一致
    func cardImages(_ id: String) -> [String: String] {
        let dir = charactersDir.appendingPathComponent(id)
        var d: [String: String] = [:]
        for stem in ["portrait", "portrait-card", "avatar", "idle"] {   // portrait-card：卡片用的小圖
            if let f = ["webp", "png"].map({ dir.appendingPathComponent("\(stem).\($0)") }).first(where: { FileManager.default.fileExists(atPath: $0.path) }) {
                d[stem] = f.absoluteString
            }
        }
        return d
    }

    /// 掃描 characters/<id>/：character.json + 立繪（.webp / .png）。檔名去掉 -2、-3 字尾是姿態名，idle-blink 是眨眼幀。
    func loadCharacters() {
        followEventGeneration &+= 1
        var chars: [String: Any] = [:]
        appToCharacter = [:]
        characterIds = []
        displayNames = [:]
        let fm = FileManager.default
        let renamed = defaults.dictionary(forKey: "names") as? [String: String] ?? [:]
        let dirs = ((try? fm.contentsOfDirectory(at: charactersDir, includingPropertiesForKeys: nil)) ?? [])
            .sorted(by: { $0.lastPathComponent < $1.lastPathComponent })
        // 立繪：<檔名去掉 -2、-3> 是姿態名，idle-blink 是眨眼幀（照著 idle 本身畫的）
        func sprites(_ dir: URL) -> (poses: [String: [String]], blink: String?, blinkBase: String?) {
            let files = ((try? fm.contentsOfDirectory(atPath: dir.path)) ?? [])
                .filter { $0.hasSuffix(".webp") || $0.hasSuffix(".png") }.sorted()
            var poses: [String: [String]] = [:]
            var blink: String?, blinkBase: String?
            for f in files {
                let name = (f as NSString).deletingPathExtension
                if name == "portrait" || name == "portrait-card" || name == "avatar" { continue }   // 展示立繪、探頭頭像只給設定頁的卡片用，不是姿態
                let url = dir.appendingPathComponent(f).absoluteString
                if name == "idle-blink" { blink = url; continue }
                if name == "idle" { blinkBase = url }
                let pose = name.split(separator: "-").first.map(String.init) ?? name
                poses[pose, default: []].append(url)
            }
            return (poses, blink, blinkBase)
        }
        // 皮膚（另一套衣服）：character.json 裡寫 "skinOf": "<角色>"，不單獨算角色；選了它，那個角色就用這套立繪，臺詞、動作邏輯照舊
        skinChoices = [:]
        var skinSprites: [String: (poses: [String: [String]], blink: String?, blinkBase: String?)] = [:]
        var skinInfo: [String: [String: Any]] = [:]
        for dir in dirs {
            guard let data = try? Data(contentsOf: dir.appendingPathComponent("character.json")),
                  let info = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let base = info["skinOf"] as? String else { continue }
            let sp = sprites(dir)
            guard sp.poses["idle"] != nil else { continue }
            skinChoices[base, default: []].append((dir.lastPathComponent, info["skinName"] as? String ?? dir.lastPathComponent))
            skinSprites[dir.lastPathComponent] = sp
            skinInfo[dir.lastPathComponent] = info
        }
        let chosen = defaults.dictionary(forKey: "skins") as? [String: String] ?? [:]
        for dir in dirs {
            guard let data = try? Data(contentsOf: dir.appendingPathComponent("character.json")),
                  var info = try? JSONSerialization.jsonObject(with: data) as? [String: Any], info["skinOf"] == nil else { continue }
            let id = dir.lastPathComponent
            var sp = sprites(dir)
            if let skin = chosen[id], let s = skinSprites[skin], skinChoices[id]?.contains(where: { $0.id == skin }) == true {
                sp = s
                info["skin"] = skin   // 網頁據此挑彩蛋（有的彩蛋只在穿某件衣服時出現）
                // 皮膚可以有自己的臺詞 / 登場招呼：寫了的覆蓋原版，沒寫的照舊
                if let own = skinInfo[skin]?["lines"] as? [String: Any] {
                    var lines = info["lines"] as? [String: Any] ?? [:]
                    for (k, v) in own { lines[k] = v }
                    info["lines"] = lines
                }
                if let g = skinInfo[skin]?["greeting"] { info["greeting"] = g }
            }
            let (poses, blink, blinkBase) = sp
            let isEgg = info["egg"] as? Bool == true  // 彩蛋角色：不進選單、不綁應用
            guard isEgg ? poses["pop"] != nil : (poses["idle"] != nil || poses["default"] != nil) else { continue }
            if !isEgg { defaultNames[id] = info["name"] as? String ?? id; baseOutfit[id] = info["outfit"] as? String; accents[id] = info["accent"] as? String }
            if !isEgg, let custom = renamed[id], !custom.isEmpty { info["name"] = custom }
            info["poses"] = poses
            info["blink"] = blink ?? NSNull()
            info["blinkBase"] = blinkBase ?? NSNull()
            chars[id] = info
            if isEgg { continue }
            characterIds.append(id)
            displayNames[id] = info["name"] as? String ?? id
            for app in (info["apps"] as? [String]) ?? [] { appToCharacter[app] = id }
        }
        guard let json = try? JSONSerialization.data(withJSONObject: ["characters": chars]),
              let arg = String(data: json, encoding: .utf8) else { return }
        manifestJSON = arg
        js("init(\(arg))")
        devJS("boot(\(arg))")
        // 剛更新完（上次開啟的是別的版本）：讓她說一聲
        let last = defaults.string(forKey: "lastVersion")
        // 「新功能」頁看到哪一版了（設定裡的小紅點用）：沒記過時，老使用者按上次的版本算（有沒看過的就亮），新安裝按當前版本（不亮）。要在下面改掉 lastVersion 之前記
        if defaults.string(forKey: "whatsNewSeen") == nil { defaults.set(last ?? currentVersion, forKey: "whatsNewSeen") }
        if last != currentVersion {
            defaults.set(currentVersion, forKey: "lastVersion")
            // 帶上上次的版本：這版（跳過的幾版合在一起）有新功能就彈「新功能」卡片（見 whats-new.js）
            if last != nil, isNewer(currentVersion, than: last!) { js("justUpdated(\(quote("v" + currentVersion)), \(quote(last!)))") }
        }
    }

    /// why：為什麼換（front 跟隨前臺 / event 跟隨事件 / host 按模型 / manual 選單或設定 / startup / skin 換衣服），記進換角色記錄（反饋「角色換得不對」用）
    func switchTo(_ id: String, why: String, via: String = "") {
        let skins = defaults.dictionary(forKey: "skins") as? [String: String] ?? [:]
        Ledger.append(switchesURL, ["from": currentId ?? "", "to": id, "skin": skins[id] ?? "", "why": why, "via": via])
        if id != currentId { appLog("換角色 \(currentId ?? "-") → \(id)（\(why)\(via.isEmpty ? "" : " " + via)）") }
        currentId = id
        defaults.set(id, forKey: "character")   // 重啟後恢復（didFinish 裡）；Windows 的 SwitchTo 也是這麼記的
        if dock != nil, !dockOut { DispatchQueue.main.asyncAfter(deadline: .now() + 0.8) { [weak self] in self?.applyDock() } }   // 換了角色，切口位置跟著變
        js("setCharacter(\(quote(id)))")
        pushSettings()
    }

    /// 一個 App 裡能用好幾家模型（比如 DeepSeek Harness）：它的接入外掛把當前角色寫在 <角色>-host.json，
    /// 切到這個 App 時換成那個角色。一天內寫的才算，角色不存在就還用 character.json 裡繫結的
    /// 本身不對應角色、而是能用好幾家模型的 App：切過去時按它的接入寫的 <宿主>-host.json 換角色，
    /// 當前模型沒有對應角色時不換（hostCharacter 返回宿主名，不是角色）
    func hostApp(_ bundle: String) -> String? {
        if bundle.hasPrefix("com.workbuddy.") { return "workbuddy" }
        if bundle == "dev.zcode.app" { return "zcode" }
        if bundle == "com.todesktop.230313mzl4w4u92" { return "cursor" }
        return nil
    }

    func hostCharacter(_ id: String) -> String {
        let url = stateDir.appendingPathComponent("\(id)-host.json")
        guard let attrs = try? FileManager.default.attributesOfItem(atPath: url.path),
              let stamp = attrs[.modificationDate] as? Date, Date().timeIntervalSince(stamp) < 86400,
              let data = try? Data(contentsOf: url),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            // 還不知道它在用哪個模型（剛裝好、還沒對話過）：先按它預設的模型換，比如 ZCode 預設是 GLM
            if let d = Self.hostDefaults[id], characterIds.contains(d) { return d }
            return id
        }
        guard let c = obj["character"] as? String, characterIds.contains(c) else { return id }   // 認不出的模型（寫的 null）：不換人
        return c
    }
    static let hostDefaults = ["zcode": "glm", "qwen": "qwen"]

    // 選單和控制台都能直接 reload；統一在導航開始時撤銷舊頁面上的非同步選擇。
    // didFinish 仍按原流程恢復角色、清空 stamps 並重新跟隨前臺，不改 pendingSwitch 的排程。
    func webView(_ webView: WKWebView, didStartProvisionalNavigation navigation: WKNavigation!) {
        guard webView === web else { return }
        followEventGeneration &+= 1
        ready = false
    }

    /// 切到某個 AI 的 App 後，要在它上面停留一會兒才換角色：
    /// App 啟動時視窗會短暫地搶焦點、讓焦點，立即響應會來回閃。
    @objc func spaceChanged(_ note: Notification) {
        fullscreenOverride = false
        // 進全屏的動畫要一會兒，視窗大小穩定了再看
        for delay in [0.1, 0.8] { DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in self?.checkFullscreen() } }
    }

    @objc func appActivated(_ note: Notification) {
        followEventGeneration &+= 1
        spaceChanged(note)
        guard ready, let app = note.userInfo?[NSWorkspace.applicationUserInfoKey] as? NSRunningApplication else { return }
        pendingSwitch?.cancel()
        guard followApps, let bundle = app.bundleIdentifier, let mapped = appToCharacter[bundle] ?? hostApp(bundle) else { return }
        let id = hostCharacter(mapped)
        guard id != currentId, characterIds.contains(id) else { return }
        let work = DispatchWorkItem { [weak self] in
            guard NSWorkspace.shared.frontmostApplication?.bundleIdentifier == bundle else { return }
            self?.switchTo(id, why: "front", via: bundle)
        }
        pendingSwitch = work
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: work)
    }

}
