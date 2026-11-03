// 額度條樣式（app/web/quota-bars.js）：node --test tests/quota_bars.test.js
process.env.TZ = 'Asia/Shanghai'
const test = require('node:test')
const assert = require('node:assert')
const B = require('../app/web/quota-bars.js')

const bj = s => Date.parse(s + ':00+08:00') / 1000
const now = bj('2026-10-21T10:00')   // 週三

test('顏色三檔：多於 30% 藍、10%–30% 橙、10% 及以下紅', () => {
  assert.deepStrictEqual([100, 31, 30, 11, 10, 0].map(B.level), ['ok', 'ok', 'mid', 'mid', 'low', 'low'])
})

test('重置時間短寫：24 小時內、7 天內、更遠', () => {
  assert.equal(B.shortWhen(now + 5.6 * 3600, now), '15:36')
  assert.equal(B.shortWhen(bj('2026-10-22T03:40'), now), '3:40')   // 不補零
  assert.equal(B.shortWhen(bj('2026-10-26T08:00'), now), '週一')
  assert.equal(B.shortWhen(bj('2026-11-02T08:00'), now), '11/2')
})

test('有 bars、只有一個池：每個視窗一行', () => {
  const r = B.of({ text: 'x', bars: [{ label: '5小時', left: 0.91, reset: now + 3600 }, { label: '本週', left: 0.25 }] }, now)
  assert.deepStrictEqual(r.rows, [
    { kind: 'bar', label: '5小時', pct: 91, level: 'ok', when: '11:00' },
    { kind: 'bar', label: '本週', pct: 25, level: 'mid', when: null }])
  assert.equal(r.note, null)
})

test('Gemini 有幾個池：每個池只畫一條（和文字樣式一樣按 pickBar 挑），不是每個視窗一條', () => {
  const H = 3600, D = 86400
  const bars = [
    { label: 'Gemini 5小時', left: 1, reset: now + 5 * H, group: 'Gemini' }, { label: 'Gemini 本週', left: 0.99, reset: now + 3 * D, group: 'Gemini' },
    { label: 'Argon 本週', left: 0.46, reset: now + 4 * D, group: 'Argon' },
    { label: 'Claude/GPT 5小時', left: 1, reset: now + 5 * H, group: 'Claude/GPT' }, { label: 'Claude/GPT 本週', left: 1, reset: now + 6 * D, group: 'Claude/GPT' }]
  const r = B.of({ text: 'x', bars }, now)
  assert.deepStrictEqual(r.rows.map(x => [x.label, x.pct]), [['Gemini 本週', 99], ['Argon 本週', 46], ['Claude/GPT 本週', 100]])
})

test('剩餘 10% 算快用完（紅），過了重置時間算滿、不寫時間', () => {
  assert.equal(B.of({ text: 'x', bars: [{ label: '5小時', left: 0.1, reset: now + 60 }] }, now).rows[0].level, 'low')
  assert.deepStrictEqual(B.of({ text: 'x', bars: [{ label: '5小時', left: 0.05, reset: now - 60 }] }, now).rows[0],
    { kind: 'bar', label: '5小時', pct: 100, level: 'ok', when: null })
})

test('沒有 bars 時從文字拆（舊版 Claude mod）：時間用文字裡原樣的', () => {
  const r = B.of({ text: '5小時 剩餘 91% ｜ 本週 剩餘 8% · 10/26 重置', low: true }, now)
  assert.deepStrictEqual(r.rows.map(x => [x.label, x.pct, x.level, x.when]), [['5小時', 91, 'ok', null], ['本週', 8, 'low', '10/26']])
})

test('拆字：「· 15:20 的資料」進 note；q.note 優先', () => {
  assert.equal(B.of({ text: '5小時 剩餘 40% · 15:20 的資料' }, now).note, '15:20 的資料')
  assert.equal(B.of({ text: 'x', bars: [{ label: '本週', left: 0.4 }], note: '15:20 的資料' }, now).note, '15:20 的資料')
})

test('拆不動的段落保留成文字行；一根條都沒有（只有餘額）時返回 null，整塊照舊按文字樣式畫', () => {
  assert.deepStrictEqual(B.of({ text: '5小時 剩餘 50% ｜ 餘額 ¥4.96', low: true }, now).rows[1], { kind: 'text', text: '餘額 ¥4.96', low: true })
  assert.equal(B.of({ text: '餘額 ¥4.96', low: true }, now), null)
})

test('沒有額度 → null', () => {
  assert.equal(B.of(null, now), null)
  assert.equal(B.of({ text: '' }, now), null)
})
