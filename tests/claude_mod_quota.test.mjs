// Claude Code 增強版 mod 的額度（integrations/claude-code/mod/hooks/register.ts 的 quotaOf）：node --test tests/claude_mod_quota.test.mjs
// node 24 直接剝掉 TypeScript 的型別來跑；register.ts 只有 import type，不需要 Claude Code 的執行環境
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { quotaOf } from '../integrations/claude-code/mod/hooks/register.ts'

test('額度條樣式：quotaOf 帶 bars（剩餘比例、重置時刻 Unix 秒），text 不變', () => {
  const q = quotaOf([{ kind: 'five_hour', percentUsed: 9, resetsAt: '2026-10-21T05:00:00Z' }, { kind: 'seven_day', percentUsed: 41 }])
  assert.deepEqual(q.bars, [{ label: '5小時', left: 0.91, reset: 1792558800 }, { label: '本週', left: 0.59 }])
  assert.match(q.text, /^5小時 剩餘 91% ｜ 本週 剩餘 59%$/)
  assert.equal(q.low, false)
})

test('不顯示的種類不進 bars；花費也是一條', () => {
  const q = quotaOf([{ kind: 'opus_weekly', percentUsed: 50 }, { kind: 'spend_limit', percentUsed: 95 }])
  assert.deepEqual(q.bars, [{ label: '花費', left: 0.05 }])
})
