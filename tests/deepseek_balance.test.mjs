// DeepSeek 餘額彙總（外掛 lib/balance.js）：node --test tests/deepseek_balance.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { summarize, lowThresholds } from '../integrations/deepseek/dsh-plugin-crosspet/lib/balance.js'

const usd = { currency: 'USD', amount: '4.97' }, cny = { currency: 'CNY', amount: '10.00' }

test('只有一種幣種：和以前寫的一模一樣（再加上 balances）', () => {
  assert.deepEqual(summarize([cny]), { text: '餘額 ¥10.00', balance: 10, balances: { CNY: 10 }, low: false })
  assert.deepEqual(summarize([{ currency: 'CNY', amount: '3.2' }]).low, true)
})

test('兩種幣種：順序對調結果完全一樣（#10 實測介面順序會變）', () => {
  const a = summarize([usd, cny]), b = summarize([cny, usd])
  assert.deepEqual(a, b)
  assert.equal(a.text, '餘額 ¥10.00｜$4.97')
  assert.equal(a.balance, 10)
  assert.deepEqual(a.balances, { CNY: 10, USD: 4.97 })
})

test('餘額低按幣種門檻：$4.97 不算低（以前被當成 < 5）', () => {
  assert.equal(summarize([usd]).low, false)
  assert.equal(summarize([{ currency: 'USD', amount: '0.5' }]).low, true)
})

test('所有幣種都低才算低', () => {
  assert.equal(summarize([{ currency: 'CNY', amount: '1' }, usd]).low, false)
  assert.equal(summarize([{ currency: 'CNY', amount: '1' }, { currency: 'USD', amount: '0.3' }]).low, true)
})

test('賬號不可用（is_available=false）一律算低', () => {
  assert.equal(summarize([cny, usd], { available: false }).low, true)
})

test('同一幣種的現金和贈送錢包加在一起', () => {
  const q = summarize([{ currency: 'CNY', amount: 3 }, { currency: 'CNY', amount: '2.5' }])
  assert.equal(q.text, '餘額 ¥5.50')
  assert.equal(q.low, false)
})

test('餘額為 0 的幣種不顯示；全是 0 時顯示人民幣', () => {
  assert.equal(summarize([{ currency: 'CNY', amount: '0.00' }, usd]).text, '餘額 $4.97')
  assert.equal(summarize([{ currency: 'USD', amount: '0' }, { currency: 'CNY', amount: '0' }]).text, '餘額 ¥0.00')
})

test('沒有任何幣種返回 null（外掛記成 no-balance）', () => {
  assert.equal(summarize([]), null)
  assert.equal(summarize(undefined), null)
  assert.equal(summarize([{ amount: '3' }]), null)
})

test('門檻配置：數字是人民幣門檻（老寫法），物件按幣種', () => {
  assert.deepEqual(lowThresholds(undefined), { CNY: 5, USD: 0.7 })
  assert.deepEqual(lowThresholds(10), { CNY: 10, USD: 0.7 })
  assert.deepEqual(lowThresholds({ USD: 2 }), { CNY: 5, USD: 2 })
  assert.equal(summarize([usd], { low: lowThresholds({ USD: 5 }) }).low, true)
})
