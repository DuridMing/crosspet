// 峰谷時段（app/web/peak-hours.js）：故意把本機時區設成美西，證明一律按北京時間算
process.env.TZ = 'America/Los_Angeles'
const test = require('node:test')
const assert = require('node:assert')
const P = require('../app/web/peak-hours.js')

const DS = P.RULES.deepseek, GLM = P.RULES.glm
const bj = s => Date.parse(s + '+08:00')   // 北京時間的某一刻

// 2026-10-19 是週一
test('DeepSeek 工作日邊界（左閉右開）', () => {
  for (const [t, k] of [['08:59', 'valley'], ['09:00', 'peak'], ['11:59', 'peak'], ['12:00', 'valley'], ['13:59', 'valley'], ['14:00', 'peak'], ['17:59', 'peak'], ['18:00', 'valley']])
    assert.strictEqual(P.periodAt(DS, bj(`2026-10-19T${t}:00`)).kind, k, t)
})

test('週末全天空閒，週五 18:00 到週一 9:00 是同一段', () => {
  const a = P.periodAt(DS, bj('2026-10-23T18:00:00')), b = P.periodAt(DS, bj('2026-10-25T23:00:00')), c = P.periodAt(DS, bj('2026-10-26T08:59:00'))
  assert.strictEqual(a.kind, 'valley')
  assert.strictEqual(a.start, b.start)
  assert.strictEqual(b.start, c.start)
  assert.strictEqual(a.start, bj('2026-10-23T18:00:00'))
  assert.strictEqual(a.until, bj('2026-10-26T09:00:00'))
})

test('午休是單獨一段，半夜不算換段', () => {
  assert.strictEqual(P.periodAt(DS, bj('2026-10-19T12:30:00')).until, bj('2026-10-19T14:00:00'))
  assert.strictEqual(P.periodAt(DS, bj('2026-10-19T23:00:00')).start, P.periodAt(DS, bj('2026-10-20T03:00:00')).start)
})

test('高峰段的起止', () => {
  const p = P.periodAt(DS, bj('2026-10-19T10:00:00'))
  assert.deepStrictEqual(p, { kind: 'peak', start: bj('2026-10-19T09:00:00'), until: bj('2026-10-19T12:00:00') })
})

test('GLM 只有下午高峰', () => {
  assert.strictEqual(P.periodAt(GLM, bj('2026-10-19T10:00:00')).kind, 'valley')
  assert.strictEqual(P.periodAt(GLM, bj('2026-10-19T14:00:00')).kind, 'peak')
  assert.strictEqual(P.periodAt(GLM, bj('2026-10-19T10:00:00')).start, bj('2026-10-16T18:00:00'))   // 上週五 18:00 起
})

test('segments：工作日分五塊，週末一整塊', () => {
  assert.deepStrictEqual(P.segments(DS, bj('2026-10-19T10:00:00')).map(s => [s.kind, s.from, s.to]),
    [['valley', 0, 540], ['peak', 540, 720], ['valley', 720, 840], ['peak', 840, 1080], ['valley', 1080, 1440]])
  assert.deepStrictEqual(P.segments(DS, bj('2026-10-24T10:00:00')).map(s => [s.kind, s.from, s.to]), [['valley', 0, 1440]])
})

test('ticks / nowMinute', () => {
  assert.deepStrictEqual(P.ticks(DS), [0, 9, 12, 14, 18, 24])
  assert.deepStrictEqual(P.ticks(GLM), [0, 14, 18, 24])
  assert.strictEqual(P.nowMinute(bj('2026-10-19T15:20:00')), 920)
})

test('decide：同一段不換、新一段按設定換、沒設不換', () => {
  const t = bj('2026-10-19T09:00:00'), ch = { peak: 'deepseek-hoodie', valley: '' }
  const d = P.decide(DS, t, ch, null)
  assert.deepStrictEqual(d, { key: String(t), kind: 'peak', skin: 'deepseek-hoodie' })
  assert.strictEqual(P.decide(DS, t + 3600e3, ch, d.key).skin, null)                 // 同一段（你手動換了也不管）
  assert.strictEqual(P.decide(DS, bj('2026-10-19T12:00:00'), ch, d.key).skin, '')   // 進入空閒 → 原版
  assert.strictEqual(P.decide(DS, t, { valley: '' }, null).skin, null)              // 高峰沒設 → 不換
  assert.strictEqual(P.decide(DS, t, undefined, null).skin, null)                   // 這個角色完全沒設
})

test('decide：睡眠跨過兩段，只按最新一段', () => {
  const ch = { peak: 'a', valley: 'b' }
  const k = P.decide(DS, bj('2026-10-19T09:30:00'), ch, null).key
  assert.deepStrictEqual(P.decide(DS, bj('2026-10-19T15:00:00'), ch, k), { key: String(bj('2026-10-19T14:00:00')), kind: 'peak', skin: 'a' })
})

test('文字款：只寫現在是高峰還是空閒', () => {
  assert.deepStrictEqual(P.statusText(DS, bj('2026-10-19T10:00:00')), { kind: 'peak', text: '高峰' })
  assert.deepStrictEqual(P.statusText(DS, bj('2026-10-19T12:30:00')), { kind: 'valley', text: '空閒' })
  assert.deepStrictEqual(P.statusText(DS, bj('2026-10-24T10:00:00')), { kind: 'valley', text: '空閒' })   // 週六
  assert.deepStrictEqual(P.statusText(GLM, bj('2026-10-19T10:00:00')), { kind: 'valley', text: '空閒' })
  assert.deepStrictEqual(P.statusText(GLM, bj('2026-10-19T15:00:00')), { kind: 'peak', text: '高峰' })
})
