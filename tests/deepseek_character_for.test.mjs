// DeepSeek Harness 外掛按模型選角色：node --test tests/deepseek_character_for.test.mjs
// 每條會話事件看一眼請求路由，把對應角色寫進 <狀態目錄>/deepseek-host.json；桌寵切到 Harness 視窗時按它換角色
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtempSync, readFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { apply } from '../integrations/deepseek/dsh-plugin-crosspet/lib/index.js'

async function hostCharacterFor(provider, model, config = {}) {
  globalThis.fetch = async () => ({ ok: true, json: async () => ({ is_available: false, balance_infos: [] }) })
  const stateDir = mkdtempSync(join(tmpdir(), 'crosspet-dsh-char-'))
  const handlers = {}, effects = []
  const ctx = { on(n, f) { (handlers[n] ||= []).push(f) }, inject() { throw new Error('none') }, effect(f) { effects.push(f) },
    logger: { warn() {} }, credentials: { resolve: async () => ({ value: 'sk-test' }) } }
  apply(ctx, { stateDir, dataDir: stateDir, autostart: false, ...config })   // dataDir：事件記錄也寫進臨時目錄，別寫到真實的資料目錄
  for (const h of handlers['session/event'] || []) h({ requestContext: () => ({ provider, model }) }, {})
  await new Promise((r) => setTimeout(r, 50))
  effects.map((f) => f()).forEach((s) => typeof s === 'function' && s())
  return JSON.parse(readFileSync(join(stateDir, 'deepseek-host.json'), 'utf8')).character
}

test('千問：模型名或服務商名認出來都換成 qwen', async () => {
  for (const [provider, model] of [['', 'qwen3.8-max'], ['', 'Qwen3-Coder'], ['dashscope', 'qwen-plus'], ['bailian', 'qwen-max'], ['tongyi', 'qwen-turbo']]) {
    assert.equal(await hostCharacterFor(provider, model), 'qwen', `${provider}/${model}`)
  }
})
test('別家的模型照舊對映，不受千問那條影響', async () => {
  assert.equal(await hostCharacterFor('deepseek', 'deepseek-chat'), 'deepseek')
  assert.equal(await hostCharacterFor('zhipu', 'glm-4.6'), 'glm')
  assert.equal(await hostCharacterFor('anthropic', 'claude-sonnet-4'), 'claude')
  assert.equal(await hostCharacterFor('', 'gpt-5'), 'gpt')
})
test('Grok：模型名或服務商名（xai / x-ai）認出來換成 grok', async () => {
  for (const [provider, model] of [['', 'grok-4.7'], ['openrouter', 'x-ai/grok-code-fast-2'], ['xai', 'grok-4-fast']]) {
    assert.equal(await hostCharacterFor(provider, model), 'grok', `${provider}/${model}`)
  }
})
test('認不出的還是由 DeepSeek 自己來演', async () => {
  assert.equal(await hostCharacterFor('some-gateway', 'mystery-model-1'), 'deepseek')
})
test('配置裡的 characters 覆蓋優先於內建判斷', async () => {
  assert.equal(await hostCharacterFor('dashscope', 'qwen3.8-max', { characters: { dashscope: 'gpt' } }), 'gpt')
})
