// CrossPet：跨 AI 桌寵。透明無邊框、永遠在最上層、可拖動的小窗。
//
// 目錄約定
//   ~/Library/Application Support/CrossPet/   執行目錄（每次啟動從 App 包裡同步 web/ 和內建角色）
//       web/            畫面（index.html + effects.js）
//       characters/     角色：<id>/character.json + 各姿態立繪（idle.webp、thinking-2.webp、idle-blink.webp …）
//                       自己加的角色放這裡不會被覆蓋
//       crosspet-hook.py  鉤子指令碼（Claude Code / Codex / Gemini CLI 用）
//   /tmp/crosspet/      各 AI 接入寫的狀態：<id>-state.json、<id>-quota.json
//
// 互動：單擊 = 摸摸頭；拖動 = 移動；右鍵 = 選單（戳一下 / 彩蛋 / 換角色 / 改名 / 設定 / 退出）
import Carbon
import Cocoa
import ServiceManagement
import WebKit

let defaults = UserDefaults.standard
let stateDir = URL(fileURLWithPath: "/tmp/crosspet", isDirectory: true)
