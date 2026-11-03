const test = require('node:test')
const assert = require('node:assert/strict')
const { eventTarget } = require('../app/web/follow-policy.js')
const characters = ['gpt', 'deepseek', 'claude']
const decide = args => eventTarget({ eventId: 'deepseek', foregroundId: 'gpt', followApps: true, followEvents: true, characters, ...args })

test('Codex 在前臺，WorkBuddy 連續事件和前臺複查不應讓角色來回切換', () => {
  let current = 'gpt'
  const switches = []
  for (let i = 0; i < 5; i++) {
    const target = decide({})
    if (target && target !== current) { current = target; switches.push(target) }
    // Windows 在宿主模型記錄更新時重新檢查仍在前臺的 Codex。
    if (current !== 'gpt') { current = 'gpt'; switches.push(current) }
  }
  assert.deepEqual(switches, [])
})

test('反向也一樣：WorkBuddy 的 DeepSeek 在前臺時，後臺 GPT 不搶角色', () => {
  assert.equal(decide({ foregroundId: 'deepseek', eventId: 'gpt' }), null)
})

test('終端或普通程式在前臺，仍按事件換角色', () => {
  for (const foregroundId of [null, '', 'terminal', 'workbuddy']) {
    assert.equal(decide({ foregroundId }), 'deepseek')
  }
})

test('關閉跟隨前臺後，已識別的 AI 在前臺也允許跟隨事件', () => {
  assert.equal(decide({ followApps: false }), 'deepseek')
})

test('關閉跟隨事件、current 通道及未知角色不會自動切換', () => {
  assert.equal(decide({ followEvents: false }), null)
  for (const eventId of [null, '', 'current', 'unknown']) assert.equal(decide({ eventId }), null)
})
