process.env.TZ = 'Asia/Shanghai'
const test = require('node:test')
const assert = require('node:assert')
const { view } = require('../app/web/codex-quota.js')

const NOW = Date.UTC(2026, 9, 12, 4, 0, 0) / 1000   // 2026-10-12 12:00 北京時間
const win = (used, mins, reset) => ({ usedPercent: used, windowDurationMins: mins, resetsAt: reset })

test('即時：兩個視窗，重置時間標在用得最多的那個上', () => {
  const v = view({ source: 'live', at: NOW, rateLimits: { primary: win(3, 300, NOW + 3600), secondary: win(40, 10080, NOW + 3 * 86400) } }, NOW)
  assert.strictEqual(v.text, '5小時 剩餘 97% ｜ 本週 剩餘 60% · 10/15 重置')
  assert.strictEqual(v.low, false)
  assert.deepStrictEqual(v.windows, [{ label: '5小時', used: 3, reset: NOW + 3600 }, { label: '本週', used: 40, reset: NOW + 3 * 86400 }])
  assert.strictEqual(v.source, 'live')
})

test('問不到時保留最後一次的數，標上那是幾點的資料', () => {
  const v = view({ source: 'stale', at: NOW - 1800, rateLimits: { primary: win(91, 300, NOW + 1800), secondary: null } }, NOW)
  assert.strictEqual(v.text, '5小時 剩餘 9% · 12:30 重置 · 11:30 的資料')
  assert.strictEqual(v.low, true)
  assert.strictEqual(v.source, 'stale')
})

test('舊資料裡已經過了重置時間的視窗顯示 100%，不參與 low', () => {
  const v = view({ source: 'stale', at: NOW - 7200, rateLimits: { primary: win(95, 300, NOW - 10), secondary: win(20, 10080, NOW + 86400 * 2) } }, NOW)
  assert.strictEqual(v.text, '5小時 剩餘 100% ｜ 本週 剩餘 80% · 10/14 重置 · 10:00 的資料')
  assert.strictEqual(v.low, false)
})

test('小數百分比四捨五入；只有一個視窗', () => {
  const v = view({ source: 'live', at: NOW, rateLimits: { primary: win(12.6, 10080, NOW + 86400 * 5) } }, NOW)
  assert.strictEqual(v.text, '本週 剩餘 87% · 10/17 重置')
})

test('沒有可用視窗 / 空 payload 返回 null', () => {
  assert.strictEqual(view(null, NOW), null)
  assert.strictEqual(view({ source: 'live', rateLimits: { primary: null, secondary: null } }, NOW), null)
  assert.strictEqual(view({ source: 'live', rateLimits: { primary: { windowDurationMins: 300 } } }, NOW), null)
})

test('「今日」視窗標籤', () => {
  const v = view({ source: 'live', at: NOW, rateLimits: { primary: win(0, 1440, 0) } }, NOW)
  assert.strictEqual(v.text, '今日 剩餘 100%')
})

test('額度條樣式：view 帶 bars（剩餘比例、重置時刻），問不到時 note 寫是幾點的資料', () => {
  const v = view({ source: 'live', at: NOW, rateLimits: { primary: win(3, 300, NOW + 3600), secondary: win(40, 10080, NOW + 3 * 86400) } }, NOW)
  assert.deepStrictEqual(v.bars, [{ label: '5小時', left: 0.97, reset: NOW + 3600 }, { label: '本週', left: 0.6, reset: NOW + 3 * 86400 }])
  assert.strictEqual(v.note, undefined)
  const s = view({ source: 'stale', at: NOW - 1800, rateLimits: { primary: win(91, 300, NOW + 1800), secondary: null } }, NOW)
  assert.strictEqual(s.note, '11:30 的資料')
  assert.deepStrictEqual(s.bars, [{ label: '5小時', left: 0.09, reset: NOW + 1800 }])
  const noReset = view({ source: 'live', at: NOW, rateLimits: { primary: { usedPercent: 50, windowDurationMins: 300 } } }, NOW)
  assert.deepStrictEqual(noReset.bars, [{ label: '5小時', left: 0.5 }])
})

test('額度條的百分比和文字一致：used 是小數時先取整（69.5 → 文字 30%，條也是 30%，顏色同一檔）', () => {
  const v = view({ source: 'live', at: NOW, rateLimits: { primary: win(69.5, 300, NOW + 3600) } }, NOW)
  assert.match(v.text, /剩餘 30%/)
  assert.strictEqual(v.bars[0].left, 0.3)
})
