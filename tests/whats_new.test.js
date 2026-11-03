const test = require('node:test')
const assert = require('node:assert')
const W = require('../app/web/whats-new.js')

const it = t => ({ icon: '•', title: t, text: t })
const L = [
  { version: '1.3.9', items: [it('還沒發的')] },
  { version: '1.3.6', items: [it('a'), it('b')] },
  { version: '1.3.5', items: [it('c')] },
  { version: '1.3.4', items: [it('d')] },
  { version: '1.3.2', items: [it('e')] },
  { version: '1.3.0', items: [it('f')] },
  { version: '1.2.0', items: [it('g')] },
]
const titles = list => list.map(x => x.title)

test('since：比上次新、不比當前新的條目，跳版本合在一起', () => {
  assert.deepStrictEqual(titles(W.since(L, '1.3.5', '1.3.6')), ['a', 'b'])
  assert.deepStrictEqual(titles(W.since(L, '1.3.4', '1.3.6')), ['a', 'b', 'c'])
})

test('since：純修 bug 的版本為空；跳到純修 bug 版本時帶上跳過的', () => {
  assert.deepStrictEqual(W.since(L, '1.3.6', '1.3.7'), [])
  assert.deepStrictEqual(titles(W.since(L, '1.3.5', '1.3.7')), ['a', 'b'])
})

test('since：測試版按正式版算，同一正式版的測試版之間為空', () => {
  assert.deepStrictEqual(titles(W.since(L, '1.3.5', '1.3.6-test.2')), ['a', 'b'])
  assert.deepStrictEqual(W.since(L, '1.3.6-test.1', '1.3.6-test.2'), [])
  assert.deepStrictEqual(W.since(L, '1.3.6-test.3', '1.3.6'), [])
})

test('since：還沒發的版本、上次比當前新、新安裝都不列', () => {
  assert.ok(!titles(W.since(L, '1.3.5', '1.3.6')).includes('還沒發的'))
  assert.deepStrictEqual(W.since(L, '1.3.8', '1.3.6'), [])
  assert.deepStrictEqual(W.since(L, null, '1.3.6'), [])
  assert.deepStrictEqual(W.since(L, undefined, '1.3.6'), [])
})

test('base / newer', () => {
  assert.strictEqual(W.base('v1.3.6-test.1'), '1.3.6')
  assert.strictEqual(W.newer('1.3.10', '1.3.9'), true)
  assert.strictEqual(W.newer('1.3.6', '1.3.6-test.2'), false)
  assert.strictEqual(W.newer('1.4', '1.3.9'), true)
})

test('unseen：有比看過的新的就亮紅點', () => {
  assert.strictEqual(W.unseen(L, '1.3.5', '1.3.6'), true)
  assert.strictEqual(W.unseen(L, '1.3.6', '1.3.6'), false)
  assert.strictEqual(W.unseen(L, '1.3.5', '1.3.7'), true)
})

test('recent：不比當前新的前 5 個，從新到舊', () => {
  const r = W.recent(L, '1.3.6')
  assert.deepStrictEqual(r.map(v => v.version), ['1.3.6', '1.3.5', '1.3.4', '1.3.2', '1.3.0'])
  assert.deepStrictEqual(W.recent(L, '1.3.6', 2).map(v => v.version), ['1.3.6', '1.3.5'])
})

test('真實文案：格式齊全', () => {
  assert.ok(Array.isArray(W.LIST) && W.LIST.length >= 3)
  const tabs = new Set(['look', 'character', 'quota', 'ai', 'general'])
  for (const v of W.LIST) {
    assert.match(v.version, /^\d+\.\d+\.\d+$/, v.version)
    assert.ok(v.items.length > 0, v.version)
    for (const x of v.items) {
      assert.ok(x.icon && x.title && x.text, `${v.version} ${x.title}`)
      if (x.go) assert.ok(tabs.has(x.go.tab), `${v.version} ${x.title} go.tab`)
    }
  }
  for (let i = 1; i < W.LIST.length; i++) assert.ok(W.newer(W.LIST[i - 1].version, W.LIST[i].version), '從新到舊')
})

test('峰谷時段那條說明換衣服在「角色」頁選（1.3.8 從額度頁搬過去了）', () => {
  const item = W.LIST.flatMap(v => v.items).find(i => i.title === '峰谷時段')
  assert.match(item.text, /「角色」頁/)
})
