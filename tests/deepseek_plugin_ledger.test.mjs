// DeepSeek Harness 外掛的事件記錄（資料目錄 events/deepseek.jsonl，格式同 crosspet-hook.py）：node --test tests/deepseek_plugin_ledger.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, readFileSync, existsSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { apply } from '../integrations/deepseek/dsh-plugin-crosspet/lib/index.js'

function harness() {
  globalThis.fetch = async () => ({ ok: true, json: async () => ({ is_available: false, balance_infos: [] }) })
  const dataDir = mkdtempSync(join(tmpdir(), 'crosspet-dsh-ledger-'))
  const handlers = {}
  const ctx = { on(n, f) { (handlers[n] ||= []).push(f) }, inject() { throw new Error('none') }, effect() {}, logger: { warn() {} },
    credentials: { resolve: async () => ({ value: 'sk-test' }) } }
  apply(ctx, { stateDir: join(dataDir, 'state'), dataDir, autostart: false, balanceGapMs: 1e9 })
  const fire = async (n, ...a) => { for (const h of handlers[n] || []) await h(...a, async () => {}) }
  const rows = async () => {
    await new Promise((r) => setTimeout(r, 80))
    const p = join(dataDir, 'events', 'deepseek.jsonl')
    return existsSync(p) ? readFileSync(p, 'utf8').split('\n').filter(Boolean).map((l) => JSON.parse(l)) : []
  }
  return { fire, rows, dataDir }
}

test('收到訊息記一行 shown，欄位和鉤子的一樣', async () => {
  const h = harness()
  await h.fire('agent/pre-step', { messages: [{ role: 'user', content: 'hi' }] })
  const last = (await h.rows()).at(-1)
  assert.equal(last.result, 'shown')
  assert.equal(last.ev, 'UserPromptSubmit')
  assert.match(last.detail, /^deepseek listening$/)
  for (const k of ['t', 'ev', 'tool', 'sid', 'turn', 'result', 'detail']) assert.ok(k in last, k)
})

test('不記內容：訊息、工具引數都不進記錄', async () => {
  const h = harness()
  await h.fire('agent/pre-step', { messages: [{ role: 'user', content: 'SECRET-DS-1' }] })
  await h.fire('tools/pre-execute', { name: 'Bash', arguments: { command: 'cat SECRET-DS-2' } })
  await h.rows()
  const text = readFileSync(join(h.dataDir, 'events', 'deepseek.jsonl'), 'utf8')
  assert.ok(!text.includes('SECRET'), text)
  assert.ok(text.includes('"tool":"bash"'))
})

test('超過 240 行截回 200 行，最後一行是最新的', async () => {
  const h = harness()
  for (let i = 0; i < 245; i++) await h.fire('tools/pre-execute', { name: `t${i}`, arguments: {} })
  const rows = await h.rows()
  assert.ok(rows.length >= 200 && rows.length <= 240, String(rows.length))
  assert.equal(rows.at(-1).tool, 't244')
})
