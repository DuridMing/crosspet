// DeepSeek Harness 外掛什麼時候查餘額：node --test tests/deepseek_plugin_poll.test.mjs
// 用假的 Harness（只記事件處理函式）和假的餘額介面，數外掛發了幾次餘額請求
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { apply } from '../integrations/deepseek/dsh-plugin-crosspet/lib/index.js'

async function run(config) {
  let calls = 0
  globalThis.fetch = async () => { calls++; return { ok: true, json: async () => ({ is_available: true, balance_infos: [{ currency: 'CNY', total_balance: '10.00' }] }) } }
  const handlers = {}, effects = []
  const ctx = { on(n, f) { (handlers[n] ||= []).push(f) }, inject() { throw new Error('none') }, effect(f) { effects.push(f) }, logger: { warn() {} },
    credentials: { resolve: async () => ({ value: 'sk-test' }) } }
  const tmp = mkdtempSync(join(tmpdir(), 'crosspet-dsh-'))
  apply(ctx, { stateDir: tmp, dataDir: tmp, autostart: false, ...config })   // dataDir：事件記錄也寫進臨時目錄，別寫到真實的資料目錄
  const stops = effects.map((f) => f())
  const wait = (ms) => new Promise((r) => setTimeout(r, ms))
  const fire = async (n, ...a) => { for (const h of handlers[n] || []) await h(...a, async () => {}); await wait(50) }
  await wait(50); const counts = [calls]
  await fire('agent/pre-step', { messages: [{ role: 'user', content: 'hi' }] }); counts.push(calls)
  await fire('agent/turn-stopping'); counts.push(calls)
  stops.forEach((s) => typeof s === 'function' && s())
  return counts
}

test('啟動時查一次，發訊息、一輪結束時各查一次（充值後一說話就更新）', async () => {
  assert.deepEqual(await run({ balanceGapMs: 0 }), [1, 2, 3])
})

test('30 秒內查過就不再查（連著對話時不會問得太勤）', async () => {
  assert.deepEqual(await run({}), [1, 1, 1])
})

// #29（@mkinnqijn）：定時查餘額的間隔可配，0 = 不定時查（只在啟動、發訊息、一輪結束時查）
async function timed(config, ms) {
  let calls = 0
  globalThis.fetch = async () => { calls++; return { ok: true, json: async () => ({ is_available: true, balance_infos: [{ currency: 'CNY', total_balance: '10.00' }] }) } }
  const effects = []
  const ctx = { on() {}, inject() { throw new Error('none') }, effect(f) { effects.push(f) }, logger: { warn() {} },
    credentials: { resolve: async () => ({ value: 'sk-test' }) } }
  const tmp = mkdtempSync(join(tmpdir(), 'crosspet-dsh-'))
  apply(ctx, { stateDir: tmp, dataDir: tmp, autostart: false, ...config })   // dataDir：事件記錄也寫進臨時目錄，別寫到真實的資料目錄
  const stops = effects.map((f) => f())
  await new Promise((r) => setTimeout(r, ms))
  stops.forEach((s) => typeof s === 'function' && s())
  return calls
}

test('balanceIntervalMs 可以調定時查餘額的間隔', async () => {
  assert.ok(await timed({ balanceIntervalMs: 40 }, 150) >= 3)
})

// 記下外掛設了哪些定時器（間隔），不真的等
async function intervals(config) {
  const real = globalThis.setInterval, seen = []
  globalThis.setInterval = (f, ms) => { seen.push(ms); return real(f, 1e9) }
  try { return { calls: await timed(config, 30), seen } } finally { globalThis.setInterval = real }
}

test('balanceIntervalMs 為 0 時不定時查，只在啟動時查一次', async () => {
  assert.deepEqual(await intervals({ balanceIntervalMs: 0 }), { calls: 1, seen: [] })
})

test('不寫 balanceIntervalMs（或寫錯）時還是每分鐘查（老使用者不變）', async () => {
  assert.deepEqual((await intervals({})).seen, [60000])
  assert.deepEqual((await intervals({ balanceIntervalMs: '5' })).seen, [60000])
  assert.deepEqual((await intervals({ balanceIntervalMs: -1 })).seen, [60000])
})
