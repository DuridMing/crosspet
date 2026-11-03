const test = require('node:test')
const assert = require('node:assert')
const { rows, compact } = require('../app/web/quota-aggregate.js')

const names = { claude: 'Claude', gpt: 'GPT', deepseek: 'DeepSeek', gemini: 'Gemini' }
const nameOf = id => names[id] || id
const ALL = ['claude', 'deepseek', 'gemini', 'gpt']
const flat = parts => parts.map(p => p.text).join('')
const text = r => r.cells.map(flat)

test('一家一行；視窗名縮寫，重置時間標在原來的那個視窗上', () => {
  const quota = { claude: { text: '5小時 剩餘 98% · 07:50 重置 ｜ 本週 剩餘 99%', low: false } }
  const [r] = rows(quota, 'claude', ALL, nameOf)
  assert.strictEqual(r.name, 'Claude')
  assert.deepStrictEqual(text(r), ['5h 98% · 07:50', '週 99%'])
})

test('當前角色排最前，其餘按角色順序', () => {
  const quota = {
    claude: { text: '5小時 剩餘 80%', low: false },
    gpt: { text: '5小時 剩餘 97%', low: false },
    deepseek: { text: '餘額 ¥6.29', low: false },
  }
  assert.deepStrictEqual(rows(quota, 'gpt', ALL, nameOf).map(r => r.id), ['gpt', 'claude', 'deepseek'])
})

test('沒有額度的 AI（沒接入、沒開啟、讀不到、空文本）不佔行', () => {
  const quota = { claude: null, gpt: { text: '', low: false }, deepseek: { text: '餘額 ¥1.00', low: false } }
  assert.deepStrictEqual(rows(quota, 'claude', ALL, nameOf).map(r => r.id), ['deepseek'])
  assert.deepStrictEqual(rows({}, 'claude', ALL, nameOf), [])
})

test('只有剩餘 ≤ 10% 的百分比標紅，別的部分不動', () => {
  const [r] = rows({ gpt: { text: '5小時 剩餘 6% · 04:31 重置 ｜ 本週 剩餘 78%', low: true } }, 'gpt', ALL, nameOf)
  const lows = r.cells.map(c => c.filter(p => p.low).map(p => p.text))
  assert.deepStrictEqual(lows, [['6%'], []])
  assert.strictEqual(compact('本週 剩餘 10%', false).find(p => p.low).text, '10%')
  assert.strictEqual(compact('本週 剩餘 11%', false).find(p => p.low), undefined)
})

test('認不出的句式（餘額、模型池）原樣顯示，來源標了 low 才整段紅', () => {
  assert.deepStrictEqual(compact('餘額 ¥6.29', false), [{ text: '餘額 ¥6.29', low: false }])
  assert.deepStrictEqual(compact('餘額 ¥0.20', true), [{ text: '餘額 ¥0.20', low: true }])
  assert.strictEqual(flat(compact('Gemini/Claude 剩餘 80% · 14:20 重置', false)), 'Gemini/Claude 80% · 14:20')
})

test('GPT 問不到、用的是舊數時，「· 11:30 的資料」跟在後面', () => {
  const [r] = rows({ gpt: { text: '5小時 剩餘 9% · 12:30 重置 · 11:30 的資料', low: true } }, 'gpt', ALL, nameOf)
  assert.deepStrictEqual(text(r), ['5h 9% · 12:30 · 11:30 的資料'])
})

test('當前角色不在列表裡（自定義角色）：只排別家，不報錯', () => {
  const quota = { claude: { text: '5小時 剩餘 80%', low: false } }
  assert.deepStrictEqual(rows(quota, 'iris', ALL, nameOf).map(r => r.id), ['claude'])
})

test('名字太長按顯示寬度截斷（中日韓字元算 2 格），短名字原樣', () => {
  const { shortName } = require('../app/web/quota-aggregate.js')
  assert.strictEqual(shortName('Claude'), 'Claude')
  assert.strictEqual(shortName('Geoffrey Everest Hinton'), 'Geoffrey Ev…')
  assert.strictEqual(shortName('亞歷山大·馮·洪堡'), '亞歷山大·馮…')
  assert.strictEqual(shortName('亞歷山大·馮'), '亞歷山大·馮')
})

test('順序：當前角色置頂，其餘按角色清單的順序（角色目錄名的字母序），不受名字影響', () => {
  const all = ['claude', 'deepseek', 'gemini', 'glm', 'gpt', 'iris']
  const quota = Object.fromEntries(all.map(id => [id, { text: '餘額 ¥1.00', low: false }]))
  const renamed = id => ({ claude: '小藍', gpt: 'Zed' }[id] || id)   // 改名不改變位置
  assert.deepStrictEqual(rows(quota, 'gemini', all, renamed).map(r => r.id), ['gemini', 'claude', 'deepseek', 'glm', 'gpt', 'iris'])
  assert.deepStrictEqual(rows(quota, 'iris', all, renamed).map(r => r.id), ['iris', 'claude', 'deepseek', 'gemini', 'glm', 'gpt'])
})

test('表放不下時名字可以截得更短（最大寬度作引數）', () => {
  const { shortName } = require('../app/web/quota-aggregate.js')
  assert.strictEqual(shortName('Geoffrey Everest Hinton', 9), 'Geoffrey…')
  assert.strictEqual(shortName('亞歷山大·馮·洪堡', 9), '亞歷山大…')
  assert.strictEqual(shortName('DeepSeek', 9), 'DeepSeek')
})

test('5 小時視窗是滿的（100%）：不標重置時間，重置時間落在用得最多的週視窗上，日期格式 M/d', () => {
  // GPT 的真實形態：5h 沒用過，「用得最多」的是週視窗，重置時間標在它身上
  const [r] = rows({ gpt: { text: '5小時 剩餘 100% ｜ 本週 剩餘 78% · 10/18 重置', low: false } }, 'gpt', ALL, nameOf)
  assert.deepStrictEqual(text(r), ['5h 100%', '週 78% · 10/18'])
  assert.deepStrictEqual(r.cells.map(c => c.filter(p => p.low).length), [0, 0])   // 100% 和 78% 都不緊張
})

test('順序不依賴傳進來的清單順序（macOS 上清單每次啟動的順序都不同）', () => {
  const quota = Object.fromEntries(['claude', 'deepseek', 'gemini', 'gpt'].map(id => [id, { text: '餘額 ¥1.00', low: false }]))
  const shuffled = ['gpt', 'gemini', 'claude', 'deepseek']
  assert.deepStrictEqual(rows(quota, 'iris', shuffled, nameOf).map(r => r.id), ['claude', 'deepseek', 'gemini', 'gpt'])
  assert.deepStrictEqual(rows(quota, 'gpt', shuffled, nameOf).map(r => r.id), ['gpt', 'claude', 'deepseek', 'gemini'])
})

test('4 行以上不管寬不寬都要收緊（底欄高度預算：3 行正常字號 / 4 行收緊後）', () => {
  const { isCrowded } = require('../app/web/quota-aggregate.js')
  assert.deepStrictEqual([0, 1, 2, 3].map(isCrowded), [false, false, false, false])
  assert.deepStrictEqual([4, 5, 6].map(isCrowded), [true, true, true])
})

const now = () => Math.floor(Date.now() / 1000)
const bar = (label, left, group, resetIn) => ({ label, left, group, ...(resetIn == null ? {} : { reset: now() + resetIn }) })
const H = 3600, D = 86400

test('bars（Antigravity）：Gemini + Argon 一行，Claude/GPT 的 5h | 週另起一行「Agy C/G」，行名按角色名', () => {
  const bars = [bar('Gemini 5小時', 1, 'Gemini', 5 * H), bar('Gemini 本週', 0.99, 'Gemini', 3 * D), bar('Argon 本週', 0.46, 'Argon', 4 * D),
                bar('Claude/GPT 5小時', 1, 'Claude/GPT', 5 * H), bar('Claude/GPT 本週', 1, 'Claude/GPT', 6 * D)]
  const list = rows({ claude: { text: '5小時 剩餘 98% · 07:50 重置 ｜ 本週 剩餘 99%' }, gemini: { text: '不會被用到 剩餘 1%', bars } }, 'claude', ['claude', 'gemini'], nameOf)
  assert.deepStrictEqual(list.map(r => r.name), ['Claude', 'Gemini', 'Agy C/G'])
  assert.deepStrictEqual(list.map(r => r.id), ['claude', 'gemini', 'gemini'])
  const [, main, cg] = list.map(text)
  assert.match(main[0], /^週 99% · \d+\/\d+$/)       // Gemini 池：5h 滿了，用最緊的週視窗；池名不重複（名字那列就是 Gemini）
  assert.match(main[1], /^Arg 46% · \d+\/\d+$/)      // 獨立池：Argon → Arg，只有一個視窗不寫視窗名
  assert.match(cg[0], /^5h 100% · \d\d:\d\d$/)       // Claude/GPT 池：每個視窗一格，短視窗在前
  assert.match(cg[1], /^週 100% · \d+\/\d+$/)
})

test('bars 挑視窗：5h 沒滿就用 5h；一樣緊取重置更晚的；最緊 ≤10% 時不被 5h 蓋住；0 算用完', () => {
  const one = bars => text(rows({ gemini: { text: 'x 剩餘 1%', bars: [...bars, bar('Claude/GPT 本週', 1, 'Claude/GPT', D)] } }, 'gemini', ['gemini'], nameOf)[0])[0]
  assert.match(one([bar('Gemini 5小時', 0.5, 'Gemini', H), bar('Gemini 本週', 0.5, 'Gemini', 3 * D)]), /^5h 50% · /)
  assert.match(one([bar('Gemini 本週', 0.5, 'Gemini', 3 * D), bar('Gemini 5小時', 0.5, 'Gemini', H)]), /^5h 50% · /)
  assert.match(one([bar('Gemini 5小時', 0.8, 'Gemini', H), bar('Gemini 本週', 0.05, 'Gemini', 3 * D)]), /^週 5% · /)
  assert.match(one([bar('Gemini 5小時', 0, 'Gemini', H), bar('Gemini 本週', 0, 'Gemini', 3 * D)]), /^週 0% · /)   // 都用完：週更晚重置，限制你的是它
  assert.match(one([bar('Gemini 今日', 0.4, 'Gemini', H), bar('Gemini 本週', 1, 'Gemini', 3 * D)]), /^今日 40% · /)
})

test('bars 只有一個池（Claude / GPT 這類，沒有 group）：合併顯示照舊拆 text；沒有 bars 也照舊拆 text', () => {
  const claude = rows({ claude: { text: '5小時 剩餘 91% ｜ 本週 剩餘 59%', bars: [{ label: '5小時', left: 0.91, reset: now() + 2 * H }, { label: '本週', left: 0.59 }] } }, 'claude', ['claude'], nameOf)
  assert.strictEqual(claude.length, 1)
  assert.deepStrictEqual(text(claude[0]), ['5h 91%', '週 59%'])
  assert.deepStrictEqual(text(rows({ gemini: { text: 'Gemini 剩餘 80% ｜ Claude/GPT 剩餘 70%', bars: [] } }, 'gemini', ['gemini'], nameOf)[0]), ['Gemini 80%', 'Claude/GPT 70%'])
})

test('bars（Antigravity）：總行數超過 4 行（底欄放不下）時，「Agy C/G」並回 Gemini 那一行，只挑一格、不寫重置時間', () => {
  const bars = [bar('Gemini 5小時', 1, 'Gemini', 5 * H), bar('Gemini 本週', 0.99, 'Gemini', 3 * D), bar('Argon 本週', 0.46, 'Argon', 4 * D),
                bar('Claude/GPT 5小時', 1, 'Claude/GPT', 5 * H), bar('Claude/GPT 本週', 0.7, 'Claude/GPT', 6 * D)]
  const quota = { claude: { text: '5小時 剩餘 98% ｜ 本週 剩餘 99%' }, gpt: { text: '5小時 剩餘 12% ｜ 本週 剩餘 78%' },
                  deepseek: { text: '餘額 ¥23.60' }, gemini: { text: 'x 剩餘 1%', bars } }
  const list = rows(quota, 'gemini', ['claude', 'gpt', 'deepseek', 'gemini'], nameOf)
  assert.strictEqual(list.length, 4)
  assert.deepStrictEqual(list.map(r => r.name), ['Gemini', 'Claude', 'DeepSeek', 'GPT'])
  const gem = text(list[0])
  assert.strictEqual(gem.length, 3)
  assert.deepStrictEqual(gem, ['週 99%', 'Arg 46%', 'C/G 週 70%'])   // Claude/GPT 池按同樣的規則挑一格；併成一行時不寫重置時間，不然太寬
  // 不擁擠時照舊拆成兩行
  assert.deepStrictEqual(rows({ claude: quota.claude, gemini: quota.gemini }, 'claude', ['claude', 'gemini'], nameOf).map(r => r.name), ['Claude', 'Gemini', 'Agy C/G'])
})

// ---- 額度條樣式的合併顯示：每格換成短條（quota-bars 設計 2026-10-26 更新）----
test('compact 給百分比那一塊標上 pct、標籤那一塊標上 label', () => {
  const parts = compact('5小時 剩餘 98% · 07:50 重置', false)
  assert.strictEqual(parts.find(p => p.pct != null).pct, 98)
  assert.strictEqual(parts.find(p => p.label != null).label, '5h')
  assert.strictEqual(compact('餘額 ¥4.96', false).some(p => p.pct != null), false)
})

test('barCell：視窗名（5h、週、今日）不寫，池名（Arg、C/G）留著；顏色三檔；餘額不是條', () => {
  const { barCell, level } = require('../app/web/quota-aggregate.js')
  assert.deepStrictEqual(barCell(compact('5小時 剩餘 98% · 07:50 重置', false)), { label: null, pct: 98, level: 'ok' })
  assert.deepStrictEqual(barCell(compact('本週 剩餘 22%', false)), { label: null, pct: 22, level: 'mid' })
  assert.deepStrictEqual(barCell(compact('Arg 剩餘 8%', false)), { label: 'Arg', pct: 8, level: 'low' })
  assert.deepStrictEqual(barCell(compact('C/G 週 剩餘 70%', false)), { label: 'C/G', pct: 70, level: 'ok' })
  assert.deepStrictEqual(barCell(compact('Gemini 本週 剩餘 40%', false)), { label: 'Gemini', pct: 40, level: 'ok' })
  assert.strictEqual(barCell(compact('餘額 ¥4.96', false)), null)
  assert.deepStrictEqual([31, 30, 11, 10].map(level), ['ok', 'mid', 'mid', 'low'])
})

test('GPT、Claude 帶 bars（沒有 group）時，合併顯示和只給 text 時一模一樣（bars 只給額度條樣式的單家顯示用）', () => {
  const { view } = require('../app/web/codex-quota.js')
  const NOW = Math.floor(Date.now() / 1000)
  const v = view({ source: 'stale', at: NOW - 3600, rateLimits: { primary: { usedPercent: 10.5, windowDurationMins: 300, resetsAt: NOW + 7200 }, secondary: { usedPercent: 40, windowDurationMins: 10080, resetsAt: NOW - 60 } } }, NOW)
  const claude = { text: '5小時 剩餘 70% ｜ 本週 剩餘 40% · 10/29 重置', low: false, bars: [{ label: '5小時', left: 0.7, reset: NOW + 3600 }, { label: '本週', left: 0.4, reset: NOW + 3 * 86400 }] }
  const withBars = rows({ gpt: v, claude }, 'gpt', ['gpt', 'claude'], nameOf)
  const textOnly = rows({ gpt: { text: v.text, low: v.low }, claude: { text: claude.text, low: claude.low } }, 'gpt', ['gpt', 'claude'], nameOf)
  assert.deepStrictEqual(withBars.map(text), textOnly.map(text))
})
