// 反饋規則（網頁層 app/web/feedback-plan.js）：每類附哪些段、日誌怎麼篩、記錄怎麼排、超長怎麼縮。
// 設計見 docs/superpowers/specs/2026-10-21-feedback-redesign-design.md：node --test tests/feedback_plan.test.js
const test = require('node:test')
const assert = require('node:assert/strict')
const P = require('../app/web/feedback-plan.js')

// 接入診斷的真實格式（取自使用者反饋，路徑已改成示例）
const DIAG = [
  '接入工具：Python 3.9.6（/usr/bin/python3），資料目錄 ~/Library/Application Support/CrossPet，狀態目錄 /tmp/crosspet',
  '已接入：codex；想一直接著：無',
  '[Claude] 配置目錄 ~/.claude（不在），環境變數 無',
  '  settings.json：~/.claude/settings.json（不存在）；標準鉤子 無',
  '[Codex] hooks.json：~/.codex/hooks.json（2618 位元組，有 CrossPet 條目）',
  '  codex 程式：/Applications/ChatGPT.app/Contents/Resources/codex-cli/bin/codex（codex-cli 0.163.0）',
  '  最近訊息：24 分鐘前',
  '[DeepSeek Harness] profile ~/.dsh/profiles/desktop（在）',
  '[Antigravity] ~/.gemini/config/hooks.json（不存在）；最近訊息 沒有記錄',
  '  WorkBuddy 最近訊息 沒有記錄；環境變數 無',
  '[ZCode] ~/.zcode/cli/config.json（不存在）',
  '[狀態] gpt=happy/Stop（10 小時前）',
].join('\n')

const facts = (sections = {}) => ({
  header: { version: '1.3.8-beta.2', platform: 'mac', os: 'macOS 26.7 arm64', install: 'abcdef1234567890', character: 'gpt', skin: '', uptimeMin: 104 },
  integrations: ['codex', 'deepseek'], characters: [{ id: 'gpt', name: 'GPT' }, { id: 'deepseek', name: 'DeepSeek' }], quotas: ['gpt'],
  sections,
})

test('九個型別，文案和順序照設計', () => {
  assert.deepEqual(P.KINDS.map(k => k.id), ['gone', 'action', 'character', 'quota', 'display', 'update', 'perf', 'idea', 'other'])
  assert.equal(P.KINDS.find(k => k.id === 'action').label, 'AI 幹活時她沒反應')
  assert.equal(P.KINDS.find(k => k.id === 'idea').askStill, false)
  assert.equal(P.KINDS.find(k => k.id === 'gone').askStill, true)
})

test('每類附的段：AI 沒反應附這一家的記錄，不附更新日誌和角色表；更新失敗才附更新日誌；建議什麼都不附', () => {
  const a = P.sectionsFor('action', 'codex')
  for (const id of ['events:codex', 'petEvents', 'state:gpt|gpt', 'diag', 'seen', 'page']) assert.ok(a.includes(id), id)
  for (const id of ['updateLog', 'characters']) assert.ok(!a.includes(id), id)
  assert.ok(P.sectionsFor('update').includes('updateLog'))
  assert.ok(!P.sectionsFor('gone').includes('updateLog'))
  assert.deepEqual(P.sectionsFor('idea'), [])
  assert.ok(P.sectionsFor('action', 'codex-desktop').includes('events:codex'))
  assert.ok(P.sectionsFor('character', 'missing').includes('characters'))
  assert.ok(P.sectionsFor('quota', 'gemini').includes('quota:gemini'))
})

test('「不確定」附所有已接入的事件記錄', () => {
  const s = P.sectionsFor('action', 'unsure', facts())
  assert.ok(s.includes('events:codex') && s.includes('events:deepseek'))
})

test('第二步的選項：ChatGPT 桌面版緊跟 Codex，最後是不確定', () => {
  assert.deepEqual(P.followOptions('action', facts()).map(o => o.id), ['codex', 'codex-desktop', 'deepseek', 'unsure'])
  assert.equal(P.followOptions('action', facts())[1].label, 'ChatGPT 桌面版')
  assert.deepEqual(P.followOptions('character', facts()).map(o => o.id), ['gpt', 'deepseek', 'missing'])
  assert.deepEqual(P.followOptions('quota', facts()).map(o => o.id), ['gpt'])
  assert.deepEqual(P.followOptions('gone', facts()), [])
})

test('接入診斷只切出這一家', () => {
  const c = P.sliceDiag(DIAG, 'codex')
  assert.match(c, /\[Codex\]/)
  assert.match(c, /最近訊息：24 分鐘前/)
  assert.doesNotMatch(c, /\[Claude\]|\[DeepSeek Harness\]/)
  assert.match(P.sliceDiag(DIAG, 'claude-mod'), /\[Claude\]/)
  assert.match(P.sliceDiag(DIAG, 'workbuddy'), /^\s*WorkBuddy 最近訊息/)
  assert.equal(P.sliceDiag(DIAG, 'hermes'), '')
})

test('日誌按型別篩行，最多 60 行', () => {
  const log = ['10:00 全屏中，先藏起來', '10:01 GPT 額度：本週已用 3%', '10:02 換角色 gpt', ...Array.from({ length: 100 }, (_, i) => `10:03 置頂 ${i}`)].join('\n')
  const gone = P.filterLog(log, 'gone').split('\n')
  assert.ok(gone.length <= 60)
  assert.ok(!gone.some(l => l.includes('額度')))
  assert.match(P.filterLog(log, 'quota'), /額度/)
  assert.doesNotMatch(P.filterLog(log, 'quota'), /置頂/)
})

test('記錄排成一行一條；沒有記錄時說明可能的原因', () => {
  const jsonl = JSON.stringify({ t: 1792571450.12, ev: 'PreToolUse', tool: 'bash', sid: 'a1b2c3', turn: true, result: 'dropped', detail: 'codex-suggestion' }) + '\n'
  const out = P.formatLedger(jsonl, 10)
  assert.match(out, /PreToolUse bash → dropped codex-suggestion/)
  assert.match(out, /a1b2c3/)
  assert.match(P.formatLedger('', 10), /外掛版本早於 1\.3\.8，或從沒收到訊息/)
  const many = Array.from({ length: 50 }, (_, i) => JSON.stringify({ t: 1792571450 + i, ev: 'E' + i, tool: '', sid: '', turn: false, result: 'shown', detail: 'gpt idle' })).join('\n')
  const lines = P.formatLedger(many, 10).split('\n')
  assert.equal(lines.length, 10)
  assert.match(lines.at(-1), /E49/)
})

test('卡片不超過 19000 位元組，開頭和使用者的話完整', () => {
  const big = Array.from({ length: 3000 }, (_, i) => `2026-10-21 10:00:00 全屏中，先藏起來 第 ${i} 行`).join('\n')
  const text = '她突然不見了，托盤還在'.repeat(5)
  const card = P.buildCard({ kind: 'gone', target: '', still: 'now', text, attach: true, facts: facts({ log: big, window: '視窗 1,2 260×362' }) })
  const body = JSON.stringify(card)
  assert.ok(Buffer.byteLength(body) <= 19000, String(Buffer.byteLength(body)))
  assert.ok(body.includes(text))
  assert.ok(body.includes('1.3.8-beta.2'))
  assert.ok(body.includes('視窗 1,2 260×362'))
})

test('不勾「附上排查資訊」：只發版本、平臺、三項選擇和使用者的話', () => {
  const card = P.buildCard({ kind: 'action', target: 'codex', still: 'now', text: '沒反應', attach: false, facts: facts({ diag: DIAG }) })
  const body = JSON.stringify(card)
  assert.ok(body.includes('1.3.8-beta.2') && body.includes('沒反應') && body.includes('還在'))
  for (const s of ['macOS 26.7', 'abcdef12', '104', 'Codex]']) assert.ok(!body.includes(s), s)
})

test('卡片標題寫型別和補選；「AI 沒反應」帶上那一家的事件記錄', () => {
  const ev = JSON.stringify({ t: 1792571450, ev: 'Stop', tool: '', sid: '', turn: true, result: 'shown', detail: 'gpt happy' })
  const card = P.buildCard({ kind: 'action', target: 'codex-desktop', still: 'earlier', text: 'Work 模式不動', attach: true,
    facts: facts({ 'events:codex': ev, diag: DIAG }) })
  assert.match(card.card.header.title.content, /AI 幹活時她沒反應 · ChatGPT 桌面版/)
  const body = JSON.stringify(card)
  assert.ok(body.includes('Stop  → shown gpt happy') || body.includes('Stop → shown gpt happy'), body.slice(0, 2000))
  assert.ok(body.includes('更早'))
})

// ---- 審查修復 ----
test('診斷特別長也不超過 19000 位元組（飛書會拒收，使用者發不出去）', () => {
  const diag = '[Codex] ' + '很長的診斷內容'.repeat(3000)
  const card = P.buildCard({ kind: 'action', target: 'codex', still: 'now', text: '沒反應', attach: true, facts: facts({ diag, page: 'x'.repeat(30000) }) })
  assert.ok(Buffer.byteLength(JSON.stringify(card)) <= 19000)
  assert.ok(JSON.stringify(card).includes('沒反應'))
})

test('WorkBuddy 的診斷：帶方括號的那幾行也切出來', () => {
  const d = DIAG + '\n[WorkBuddy] 鉤子 已裝\n  最近訊息 3 分鐘前\n[ZCode] x'
  const w = P.sliceDiag(d, 'workbuddy')
  assert.match(w, /\[WorkBuddy\] 鉤子 已裝/)
  assert.match(w, /最近訊息 3 分鐘前/)
  assert.doesNotMatch(w, /\[ZCode\]/)
})

test('多模型宿主的狀態段：狀態按角色、輪次和換模型記錄按宿主名', () => {
  assert.ok(P.sectionsFor('action', 'workbuddy').includes('state:current|workbuddy'))
  assert.ok(P.sectionsFor('action', 'codex').includes('state:gpt|gpt'))
  assert.ok(P.sectionsFor('action', 'zcode').includes('state:glm|zcode'))
  assert.ok(P.sectionsFor('action', 'claude-hooks').includes('state:claude|claude'))
})

test('沒有記錄時分清：收到過訊息卻沒記錄 / 從沒收到 / 桌寵自己沒記錄', () => {
  const card = s => JSON.stringify(P.buildCard({ kind: 'action', target: 'codex', still: 'now', text: 'x', attach: true, facts: facts(s) }))
  assert.match(card({ 'events:codex': '', seen: JSON.stringify({ codex: Date.now() / 1000 - 60 }) }), /收到過訊息卻沒有記錄/)
  assert.match(card({ 'events:codex': '', seen: JSON.stringify({ codex: 0 }) }), /從沒收到/)
  assert.doesNotMatch(card({ petEvents: '' }), /外掛版本/)
})

test('排查資訊沒收齊就發了：卡片裡寫明，不是靜默發空卡', () => {
  const body = JSON.stringify(P.buildCard({ kind: 'action', target: 'codex', still: 'now', text: 'x', attach: true, partial: true, facts: facts({}) }))
  assert.match(body, /排查資訊沒來得及收齊/)
  assert.doesNotMatch(JSON.stringify(P.buildCard({ kind: 'action', target: 'codex', still: 'now', text: 'x', attach: true, facts: facts({}) })), /沒來得及收齊/)
})

test('「其他」是說不清哪類時用的：每類都帶一點，已接入的 AI 各附 10 條事件記錄', () => {
  const s = P.sectionsFor('other', '', facts())
  for (const id of ['window', 'displays', 'front', 'hidden', 'page', 'settings', 'seen', 'events:codex', 'events:deepseek', 'petEvents', 'switches', 'install', 'log'])
    assert.ok(s.includes(id), id)
  const many = Array.from({ length: 50 }, (_, i) => JSON.stringify({ t: 1792571450 + i, ev: 'E' + i, tool: '', sid: '', turn: false, result: 'shown', detail: 'gpt idle' })).join('\n')
  const body = JSON.stringify(P.buildCard({ kind: 'other', target: '', still: 'now', text: 'x', attach: true, facts: facts({ 'events:codex': many, seen: '{}' }) }))
  assert.ok(body.includes('E49') && !body.includes('E39 '), '每家只附最近 10 條')
})
