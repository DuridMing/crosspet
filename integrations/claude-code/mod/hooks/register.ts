import type { EngineInterface, Register } from 'claude-code'

// CrossPet × Claude Code（增強版 mod）
// 和標準鉤子輸出同一種狀態（<狀態目錄>/claude-state.json），另外多兩樣：
// - 額度：session.measure 推來的 5 小時 / 每週視窗 → claude-quota.json
// - 被打斷 / 出錯：turn.complete 的 reason
// - 壓縮上下文：session.compact
// - 等你回答：AskUserQuestion 提問、彈出授權框（Notification）
// 狀態目錄和桌寵、其他接入一致：CROSSPET_STATE_DIR > Windows 的 %LOCALAPPDATA%\CrossPet\state > macOS 的 /tmp/crosspet。

let dir: string | undefined
async function stateDir($: EngineInterface): Promise<string> {
  if (dir) return dir
  const custom = await $.env.get('CROSSPET_STATE_DIR')
  const local = await $.env.get('LOCALAPPDATA')  // 只有 Windows 有這個變數
  dir = custom || (local ? `${local}\\CrossPet\\state` : '/tmp/crosspet')
  return dir
}
async function write($: EngineInterface, file: string, data: unknown): Promise<void> {
  await $.fs.write(`${await stateDir($)}/${file}`, JSON.stringify(data))
}
const ID = 'claude'

// 走別家的相容介面（DeepSeek / 智譜……）時換成對應的角色演，認不出的還是 Claude。
// 看的是模型名：每次請求前（turn.step）看這次要用的模型，回覆回來再用 API 報的「實際作答的模型」校正，中途 /model 換了也跟得上。
// 不看環境變數：CC Switch 之類會把 ANTHROPIC_BASE_URL / ANTHROPIC_MODEL 寫進 settings.json，
// 但 Claude 桌面 App 登入的賬號照樣走 Anthropic，只看環境變數會把它誤認成 DeepSeek。
// 介面地址只在模型名完全認不出時參考
function characterForUrl(url: string): string | undefined {
  const u = url.toLowerCase()
  if (u.includes('deepseek.com')) return 'deepseek'
  if (u.includes('bigmodel.cn') || u.includes('z.ai') || u.includes('zhipu')) return 'glm'
  return undefined
}
function characterForModel(model: string): string | undefined {
  const m = model.toLowerCase()
  if (/glm|zhipu|chatglm|z\.ai|zai-/.test(m)) return 'glm'
  if (m.includes('deepseek')) return 'deepseek'
  if (/claude|anthropic|opus|sonnet|haiku/.test(m)) return 'claude'
  if (m.includes('gemini')) return 'gemini'
  if (/^(gpt|o[1-9]|codex|chatgpt)/.test(m) || m.includes('openai')) return 'gpt'
  return undefined
}
// 會話 id → 正在演的角色（還沒看到模型時是 Claude）
const actors = new Map<string, string>()
async function charactersDir($: EngineInterface): Promise<string> {
  const local = await $.env.get('LOCALAPPDATA')
  return local ? `${local}\\CrossPet\\characters` : `${await $.env.get('HOME')}/Library/Application Support/CrossPet/characters`
}
async function noteModel($: EngineInterface, model: string): Promise<void> {
  if (!model) return
  const sid = await $.session.id()
  let who = characterForModel(model) ?? characterForUrl((await $.env.get('ANTHROPIC_BASE_URL')) ?? '') ?? ID
  if (who !== ID && !(await $.fs.exists(`${await charactersDir($)}/${who}/character.json`))) who = ID   // 這個角色沒裝
  const old = actors.get(sid)
  if (old === who) return
  actors.set(sid, who)
  // 換了角色：這個會話從原來那個角色的多會話記錄裡拿掉，免得那邊留著一張「還在忙」的卡片
  if (old) await dropSession($, old, sid)
  // 桌寵切到 Claude 的 App 時按它換角色（和 DeepSeek Harness、WorkBuddy 的 <宿主>-host.json 一樣）
  await write($, `${ID}-host.json`, { character: who, model, ts: Date.now() / 1000 })
}
async function dropSession($: EngineInterface, who: string, sid: string): Promise<void> {
  try {
    const file = `${await stateDir($)}/${who}-sessions.json`
    const known = JSON.parse(await $.fs.read(file))
    if (!(sid in known)) return
    delete known[sid]
    await $.fs.write(file, JSON.stringify(known))
  } catch {}
}

// 事件記錄（反饋「AI 幹活時她沒反應」用）：<資料目錄>/events/claude-mod.jsonl，格式和 crosspet-hook.py 一樣（見 docs/架构.md「记录」）。
// 只記事件名、工具名、會話 id 的雜湊、結果，不記內容；只留最近 200 行。events 資料夾由桌寵啟動時建好
function shortHash(s: string): string {
  let h = 0x811c9dc5
  for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0 }
  return h.toString(16).padStart(8, '0').slice(0, 6)
}
// 同一個程式裡的寫入排隊（並行的工具呼叫會同時觸發），免得兩次整讀整寫互相覆蓋、丟行。
// 兩個 Claude Code 程式同時寫還可能丟一兩行：引擎沒有檔案鎖，這裡接受（只是少幾行記錄，不會寫壞格式以外的東西）
let ledgerChain: Promise<void> = Promise.resolve()
function ledger($: EngineInterface, ev: string, tool: string, sid: string, result: string, detail: string): void {
  ledgerChain = ledgerChain.then(() => writeLedger($, ev, tool, sid, result, detail))
}
async function writeLedger($: EngineInterface, ev: string, tool: string, sid: string, result: string, detail: string): Promise<void> {
  try {
    const file = `${(await charactersDir($)).replace(/[\\/]characters$/, '')}/events/claude-mod.jsonl`
    let rows: string[] = []
    try { rows = (await $.fs.read(file)).split('\n').filter(Boolean) } catch {}
    rows.push(JSON.stringify({ t: Math.round(Date.now() / 10) / 100, ev, tool: tool.toLowerCase().slice(0, 60), sid: sid ? shortHash(sid) : '', turn: false, result, detail }))
    if (rows.length > 240) rows = rows.slice(-200)
    await $.fs.write(file, rows.join('\n') + '\n')
  } catch {}   // 記錄寫不了不能影響 Claude
}

// 寫狀態；同時按會話另外記一份 <角色>-sessions.json（{會話id: {pose, event, ts}}），
// 同時開著好幾個會話在幹活時，桌寵演「手忙腳亂」，每個會話一張小卡片
type State = { pose: string; event: string; tool: string; ts: number }
async function setState($: EngineInterface, state: State): Promise<void> {
  await write($, 'seen-claude-mod', String(Math.floor(Date.now() / 1000))).catch(() => {})   // 心跳：最近一次收到訊息
  let sid = ''
  try { sid = await $.session.id() } catch {}
  const who = actors.get(sid) ?? ID
  await write($, `${who}-state.json`, state)
  ledger($, state.event, state.tool, sid, 'shown', `${who} ${state.pose}`)   // 不等它寫完：記錄不該拖慢狀態更新
  try {
    const file = `${await stateDir($)}/${who}-sessions.json`
    let known: Record<string, { pose: string; event: string; ts: number }> = {}
    try { known = JSON.parse(await $.fs.read(file)) } catch {}
    known[sid] = { pose: state.pose, event: state.event, ts: state.ts }
    const now = Date.now() / 1000
    for (const k of Object.keys(known)) if (now - known[k].ts > 600) delete known[k]
    await $.fs.write(file, JSON.stringify(known))
  } catch {}
}
const BIG_JOB_TOOLS = 8

// 跟著 AI 出現：會話開始 / 發訊息時看一眼，桌寵沒開著就開啟。使用者從選單手動退出過（user-quit）就先不管，
// 直到 Claude 關掉重開或重啟電腦（Claude 是在退出之後才打開的，記號就作廢）；在設定裡關了（ai-autostart-off）就不管。
// 不等它跑完，失敗也不影響 Claude；一分鐘內最多看一次
let lastEnsure = 0
// Claude 程式是什麼時候開啟的（毫秒）：順著父程式往上找到 launchd 下面那一層。sh 的 $PPID 就是這個引擎程式
async function appStartedAt($: EngineInterface): Promise<number> {
  const { stdout } = await $.process.run(['/bin/sh', '-c', 'echo $PPID; /bin/ps -A -o pid=,ppid=,etime='])
  const [first, ...lines] = stdout.trim().split('\n')
  const procs = new Map<number, [number, number]>()
  for (const line of lines) {
    const [pid, ppid, etime] = line.trim().split(/\s+/)
    if (!etime) continue
    const [days, clock] = etime.includes('-') ? etime.split('-') : ['0', etime]
    const secs = clock.split(':').reduce((a, b) => a * 60 + Number(b), 0) + Number(days) * 86400
    procs.set(Number(pid), [Number(ppid), secs])
  }
  let pid = Number(first)
  while (procs.has(pid) && procs.get(pid)![0] > 1 && procs.has(procs.get(pid)![0])) pid = procs.get(pid)![0]
  return procs.has(pid) ? Date.now() - procs.get(pid)![1] * 1000 : 0
}
async function ensurePet($: EngineInterface): Promise<void> {
  if (Date.now() - lastEnsure < 60_000) return
  lastEnsure = Date.now()
  const local = await $.env.get('LOCALAPPDATA')
  if (local) {  // Windows：交給隨包 Python 跑鉤子指令碼的 --ensure（查單例項互斥量、用獨立程式開啟）
    await $.process.run([`${local}\\Programs\\CrossPet\\python\\python.exe`, `${local}\\CrossPet\\crosspet-hook.py`, '--ensure'], { timeoutMs: 10_000 })
    return
  }
  // macOS：不用 Python（沒裝開發者工具的 Mac 上調 python3 會彈安裝框）
  const data = `${await $.env.get('HOME')}/Library/Application Support/CrossPet`
  if (await $.fs.exists(`${data}/ai-autostart-off`)) return
  if (await $.fs.exists(`${data}/user-quit`) && await appStartedAt($) <= (await $.fs.stat(`${data}/user-quit`)).mtimeMs) return
  try {
    const pid = (await $.fs.read(`${await stateDir($)}/pet.pid`)).trim()
    if (/^\d+$/.test(pid) && (await $.process.run(['/bin/kill', '-0', pid])).exitCode === 0) return  // 在跑
  } catch {}
  await $.process.run(['/usr/bin/open', '-g', '-b', 'io.github.crosspet'], { timeoutMs: 10_000 })  // -g：後臺開啟，不搶焦點
}

type Limit = { kind: string; percentUsed: number; resetsAt?: string }

function poseForTool(tool: string): string {
  const t = tool.toLowerCase()
  if (t === 'askuserquestion') return 'asking'  // 停下來問你問題、讓你選選項
  if (/image_gen|imagegen|generate_image|draw|paint/.test(t)) return 'drawing'
  if (/read|grep|glob|list|view|cat/.test(t)) return 'reading'
  if (/write|edit|patch|replace|create/.test(t)) return 'writing'
  if (/web|fetch|browse|search|http/.test(t)) return 'searching'
  if (/agent|task|subagent|delegate/.test(t)) return 'delegating'
  return 'running'
}

// 各視窗顯示「剩餘」百分比，只給剩得最少的那個標重置時間
export function quotaOf(limits: Limit[]) {
  const shown = limits.filter(l => l.kind === 'five_hour' || l.kind === 'seven_day' || l.kind === 'spend_limit')
  if (shown.length === 0) return null
  const top = shown.reduce((a, b) => (b.percentUsed > a.percentUsed ? b : a))
  const pad = (n: number) => String(n).padStart(2, '0')
  const parts = shown.map(l => {
    const label = l.kind === 'five_hour' ? '5小時' : l.kind === 'seven_day' ? '本週' : '花費'
    let piece = `${label} 剩餘 ${Math.max(0, 100 - Math.round(l.percentUsed))}%`
    if (l === top && l.resetsAt) {
      const d = new Date(l.resetsAt)
      const soon = d.getTime() - Date.now() < 86_400_000
      piece += ` · ${soon ? `${pad(d.getHours())}:${pad(d.getMinutes())}` : `${d.getMonth() + 1}/${d.getDate()}`} 重置`
    }
    return piece
  })
  // 額度條樣式（CrossPet 設定裡「額度顯示樣式：額度條」）用的結構化資料：剩餘比例 0–1、重置時刻 Unix 秒
  const bars = shown.map(l => ({
    label: l.kind === 'five_hour' ? '5小時' : l.kind === 'seven_day' ? '本週' : '花費',
    left: Math.max(0, 100 - Math.round(l.percentUsed)) / 100,
    ...(l.resetsAt ? { reset: Math.round(new Date(l.resetsAt).getTime() / 1000) } : {}),
  }))
  return { text: parts.join(' ｜ '), low: top.percentUsed >= 90, bars }
}

export const register: Register = on => {
  let tools = 0

  on('session.start', async ($, e, next) => {
    void ensurePet($).catch(() => {})
    await setState($, { pose: 'idle', event: 'SessionStart', tool: '', ts: Date.now() / 1000 })
    const usage = await $.session.usage()
    const q = quotaOf(usage.rateLimits)
    if (q) await write($, `${ID}-quota.json`, q)
    return next(e)
  })

  on('prompt.submit', async ($, e, next) => {
    void ensurePet($).catch(() => {})
    tools = 0
    await setState($, { pose: 'listening', event: 'UserPromptSubmit', tool: '', ts: Date.now() / 1000 })
    return next(e)
  })

  // 每次向模型發請求前：這次用的是哪個模型（只看主對話，子任務的不算）
  on('turn.step', async function* ($, e, next) {
    if (!e.agentId) await noteModel($, e.model).catch(() => {})
    const r = yield* next(e)
    if (!e.agentId && r?.usage?.model) await noteModel($, r.usage.model).catch(() => {})   // API 報的實際作答模型
    return r
  })

  on('tool.call', async ($, e, next) => {
    tools += 1
    await setState($, { pose: poseForTool(e.tool), event: 'PreToolUse', tool: e.tool, ts: Date.now() / 1000 })
    const r = await next(e)
    const failed = 'deny' in r && r.deny !== undefined ? true : r.isError === true
    await setState($, { pose: failed ? 'oops' : 'thinking', event: 'PostToolUse', tool: e.tool, ts: Date.now() / 1000 })
    return r
  })

  on('turn.complete', async ($, e, next) => {
    const r = await next(e)
    const pose =
      e.reason === 'answer' ? (tools >= BIG_JOB_TOOLS ? 'proud' : 'happy') : e.reason === 'aborted' ? 'surprised' : 'oops'
    tools = 0
    await setState($, { pose, event: 'Stop', tool: '', ts: Date.now() / 1000 })
    return r
  })

  // 要你授權 / MCP 要你填表：只認真的彈到你面前的那一刻（和標準鉤子一樣用 Notification）。
  // 不用 tool.check 的「ask」判定：auto 模式、PreToolUse 鉤子會在彈框前自動放行，那樣她幹活時會一直舉著選項卡
  on('classic.Notification', async ($, e, next) => {
    const kind = String(e.notification_type ?? '').toLowerCase()
    if (kind === 'permission_prompt' || kind === 'elicitation_dialog')
      await setState($, { pose: 'asking', event: 'Notification', tool: '', ts: Date.now() / 1000 })
    return next(e)
  })

  // 壓縮上下文（/compact 或自動）：只管主對話，後臺預先壓縮（precompute）和子任務的不算
  on('session.compact', async ($, e, next) => {
    const shown = (e.trigger === 'manual' || e.trigger === 'auto') && !e.agentId
    if (shown) await setState($, { pose: 'compact', event: 'PreCompact', tool: '', ts: Date.now() / 1000 })
    const r = await next(e)
    // 自動壓縮發生在一輪中途，壓完接著幹活；手動 /compact 壓完這一輪就結束了，後面不會再有 Stop，得自己收尾
    if (shown) await setState($, e.trigger === 'manual'
      ? { pose: 'happy', event: 'Stop', tool: '', ts: Date.now() / 1000 }
      : { pose: 'thinking', event: 'PostCompact', tool: '', ts: Date.now() / 1000 })
    return r
  })

  on('session.measure', async ($, e, next) => {
    const q = quotaOf(e.rateLimits.map(l => ({ kind: l.kind, percentUsed: l.percentUsed, resetsAt: l.resetsAt })))
    if (q) await write($, `${ID}-quota.json`, q)
    return next(e)
  })
}
