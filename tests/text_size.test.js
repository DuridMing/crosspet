const test = require('node:test')
const assert = require('node:assert')
const { effectiveText, layout } = require('../app/web/text-size.js')

test('鎖著（預設）：文字 = 人物；沒存過 textLinked 也算鎖著', () => {
  assert.equal(effectiveText(0.7, 1.3, true), 0.7)
  assert.equal(effectiveText(0.7, 1.3, undefined), 0.7)
  assert.equal(effectiveText(0.7, undefined, undefined), 0.7)
})

test('解鎖：用文字大小', () => {
  assert.equal(effectiveText(0.7, 1.3, false), 1.3)
  assert.equal(effectiveText(0.7, undefined, false), 1)   // 解鎖了但沒存過文字大小：按 100%
})

test('超出範圍收回 60%～160%，不是數字按 100%', () => {
  assert.equal(effectiveText(0.7, 5, false), 1.6)
  assert.equal(effectiveText(0.7, 0.1, false), 0.6)
  assert.equal(effectiveText(0.7, 'x', false), 1)
  assert.equal(effectiveText(9, 1, true), 1.6)
})

test('字和人一樣大：畫布不變（鎖著的使用者和以前一樣）', () => {
  assert.deepEqual(layout(1, 1), { k: 1, w: 260, h: 362, foot: 62 })
  assert.deepEqual(layout(0.6, 0.6), { k: 1, w: 260, h: 362, foot: 62 })
  assert.deepEqual(layout(1.6, 1.6), { k: 1, w: 260, h: 362, foot: 62 })
})

test('字比人小：畫布不變，底欄不縮', () => {
  const L = layout(1.6, 0.6)
  assert.ok(Math.abs(L.k - 0.375) < 1e-9)
  assert.deepEqual([L.w, L.h, L.foot], [260, 362, 62])
})

test('人 60%、字 100%：畫布 420×443，底欄 103', () => {
  const L = layout(0.6, 1)
  assert.ok(Math.abs(L.k - 1 / 0.6) < 1e-9)
  assert.deepEqual([L.w, L.h, L.foot], [420, 443, 103])
})

test('最極端：人 60%、字 160%', () => {
  const L = layout(0.6, 1.6)
  assert.deepEqual([L.w, L.h, L.foot], [660, 564, 165])
})

test('只看兩個數：同樣的輸入每次結果一樣', () => {
  assert.deepEqual(layout(0.8, 1.2), layout(0.8, 1.2))
})
