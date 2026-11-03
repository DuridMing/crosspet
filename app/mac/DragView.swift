import Carbon
import Cocoa
import ServiceManagement
import WebKit

final class DragView: NSView {
    var onClick: ((NSPoint) -> Void)?   // 點在哪（這個檢視的座標）：網頁要知道是不是點在「新功能」卡片的按鈕上
    var onMenu: ((NSEvent) -> Void)?
    var onDragStart: (() -> Void)?
    var onDragEnd: (() -> Void)?
    var onShake: (() -> Void)?        // 按住快速來回甩（惹她生氣）
    private var shook = false          // 這次拖動甩過：鬆手不貼邊
    private var swingX: CGFloat = 0, swingDir: CGFloat = 0, swings: [Date] = [], shakeSaid = Date.distantPast
    private var downAt: NSPoint = .zero
    private var originAt: NSPoint = .zero
    private var dragged = false

    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) {
        downAt = NSEvent.mouseLocation
        dragged = false
    }
    override func mouseDragged(with event: NSEvent) {
        let p = NSEvent.mouseLocation
        if !dragged {
            guard abs(p.x - downAt.x) + abs(p.y - downAt.y) > 3 else { return }
            dragged = true
            onDragStart?()
            originAt = window?.frame.origin ?? .zero
            downAt = p
            shook = false; swingX = p.x; swingDir = 0; swings = []
        }
        // 甩：橫向來回反轉方向。記住這個方向上拖到的最遠點，從那裡往回拖超過 18 點算一次反轉，0.9 秒內反轉兩次以上就算在甩
        if swingDir == 0 {
            if abs(p.x - swingX) > 18 { swingDir = p.x > swingX ? 1 : -1; swingX = p.x }
        } else if (p.x - swingX) * swingDir > 0 {
            swingX = p.x   // 同方向繼續拖：更新最遠點
        } else if (swingX - p.x) * swingDir > 18 {
            swingDir = -swingDir; swingX = p.x
            swings.append(Date())
            swings = swings.filter { Date().timeIntervalSince($0) < 0.9 }
            if swings.count >= 2, Date().timeIntervalSince(shakeSaid) > 0.3 { shook = true; shakeSaid = Date(); onShake?() }
        }
        // 自己跟著滑鼠挪（不用 performDrag 交給系統：macOS 15 起系統拖到螢幕邊緣會彈「視窗分屏」預覽、鬆手還會去動視窗，跟貼邊搶位置）
        window?.setFrameOrigin(NSPoint(x: originAt.x + p.x - downAt.x, y: originAt.y + p.y - downAt.y))
    }
    override func mouseUp(with event: NSEvent) {
        if dragged {
            if let o = window?.frame.origin { defaults.set([o.x, o.y], forKey: "origin") }
            if !shook { onDragEnd?() }   // 甩她的時候鬆手不貼邊
        } else {
            onClick?(convert(event.locationInWindow, from: nil))
        }
    }
    override func rightMouseDown(with event: NSEvent) { onMenu?(event) }
}
