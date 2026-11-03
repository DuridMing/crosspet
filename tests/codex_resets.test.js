process.env.TZ = 'Asia/Shanghai'   // fmt 用本地時間，測試固定時區
const test = require('node:test')
const assert = require('node:assert')
const R = require('../app/web/codex-resets.js')

const NOW = Date.parse('2026-10-14T15:00:00Z') / 1000
const base = (data) => ({ data: { latest_reset: null, scheduled_reset: null, active_watch: null, stats: { total: 61, last_reset_at: '2026-10-13T05:06:06.000Z', days_since_last: 1.4, avg_interval_days: 6.5 }, ...data }, meta: { api_version: 'v1' } })
const watch = (level, chance, extra = {}) => ({ level, reset_chance_percent: chance, forecast_window: 'next 48h', observed_at: '2026-10-14T10:00:00.000Z', expires_at: '2026-10-16T10:00:00.000Z', text: 't', source: { type: 'x_post', author: 'thsottiaux', url: 'https://x.com/a/status/1' }, ...extra })
const sched = (extra = {}) => ({ id: '999', status: 'scheduled', reset_type: 'regular', announced_at: '2026-10-14T12:00:00.000Z', scheduled_for: '2026-10-16T06:00:00.000Z', text: 't', source: { type: 'x_post', author: 'thsottiaux', url: 'https://x.com/a/status/999' }, ...extra })

test('parse：全空（真實介面 10-14 的樣子）', () => {
  const d = R.parse(base({ latest_reset: { id: '210', reset_type: 'regular', announced_at: '2026-10-13T05:06:06.000Z', text: 't', source: { type: 'x_post', author: 'thsottiaux', url: 'u' } } }))
  assert.strictEqual(d.announced, null)
  assert.strictEqual(d.watch, null)
  assert.deepStrictEqual(d.latest, { id: '210', at: Date.parse('2026-10-13T05:06:06Z') / 1000 })
  assert.deepStrictEqual(d.stats, { since: 1.4, avg: 6.5 })
})

test('parse：不認識的形狀返回 null 或把那一塊當沒有', () => {
  for (const bad of [null, {}, { data: null }, { data: 3 }, 'x']) assert.strictEqual(R.parse(bad), null)
  const d = R.parse(base({ active_watch: { level: 'huge' }, scheduled_reset: { id: 5 }, stats: { days_since_last: 'a' } }))
  assert.strictEqual(d.watch, null)
  assert.strictEqual(d.announced, null)
  assert.strictEqual(d.stats, null)
})

test('notice：有宣佈才有一行；帶時間 / 不帶時間', () => {
  assert.strictEqual(R.notice(R.parse(base({})), 0), '')
  assert.strictEqual(R.notice(R.parse(base({ active_watch: watch('strong', 90) })), 0), '')
  assert.strictEqual(R.notice(R.parse(base({ scheduled_reset: sched() })), 0), '🔔 官方已宣佈重置 · 預計 10/16 14:00')
  assert.strictEqual(R.notice(R.parse(base({ scheduled_reset: sched({ scheduled_for: null }) })), 0), '🔔 官方已宣佈重置，還沒生效')
})

test('notice：宣佈已執行就不顯示（站點記為已執行，或我們在宣佈之後看到額度重置）', () => {
  const done = R.parse(base({ scheduled_reset: sched(), latest_reset: { id: '999', reset_type: 'regular', announced_at: '2026-10-14T13:00:00.000Z', text: 't', source: { type: 'x_post', author: 'thsottiaux', url: 'u' } } }))
  assert.strictEqual(R.notice(done, 0), '')
  const d = R.parse(base({ scheduled_reset: sched() }))
  assert.strictEqual(R.notice(d, Date.parse('2026-10-14T13:00:00Z') / 1000), '')   // 宣佈 12:00，13:00 我們看到重置
  assert.notStrictEqual(R.notice(d, Date.parse('2026-10-14T11:00:00Z') / 1000), '') // 宣佈之前的重置不算
})

test('answer：各檔的動作、臺詞鍵和填空', () => {
  const a = (data) => R.answer(R.parse(base(data)), NOW, 0)
  assert.deepStrictEqual(a({ scheduled_reset: sched() }), { tier: 'announced', pose: 'reset_bell', key: 'resetAnnounced', vars: { when: '10/16 14:00' } })
  assert.deepStrictEqual(a({ active_watch: watch('strong', 85) }), { tier: 'high', pose: 'reset_hope', key: 'resetHigh', vars: { chance: 85, window: '接下來 48 小時內' } })
  assert.strictEqual(a({ active_watch: watch('elevated', 70) }).tier, 'high')
  assert.strictEqual(a({ active_watch: watch('elevated', 50) }).tier, 'mid')
  assert.strictEqual(a({ active_watch: watch('elevated', 50) }).pose, 'reset_maybe')
  assert.strictEqual(a({ active_watch: watch('elevated', 10) }).pose, 'reset_nope')
  assert.strictEqual(a({ active_watch: watch('elevated', 29) }).tier, 'low')
  assert.strictEqual(a({ active_watch: watch('strong', null) }).tier, 'mid')     // 沒給機率但等級高：不能說「不太像」
  assert.strictEqual(a({ active_watch: watch('elevated', null) }).tier, 'low')
  assert.deepStrictEqual(a({}), { tier: 'none', pose: 'reset_nope', key: 'resetNone', vars: { since: '1.4', avg: '6.5' } })
  assert.strictEqual(R.answer(R.parse(base({ stats: null })), NOW, 0).key, 'resetQuiet')
  assert.deepStrictEqual(R.answer(null, NOW, 0), { tier: 'offline', pose: 'reset_nope', key: 'resetOffline', vars: {} })
  assert.strictEqual(R.answer(R.parse(base({ scheduled_reset: sched({ scheduled_for: null }) })), NOW, 0).vars.when, '很快')
})

test('answer：宣佈之後已經重置了（站點記為已執行，或我們看到額度重置）→ 說已經重置了，不說沒風聲', () => {
  const done = R.parse(base({ scheduled_reset: sched(), latest_reset: { id: '999', reset_type: 'regular', announced_at: '2026-10-14T13:00:00.000Z', text: 't', source: { type: 'x_post', author: 'thsottiaux', url: 'u' } } }))
  assert.deepStrictEqual(R.answer(done, NOW, 0), { tier: 'done', pose: 'reset', key: 'resetDone', vars: {} })
  const seen = R.parse(base({ scheduled_reset: sched() }))
  assert.strictEqual(R.answer(seen, NOW, Date.parse('2026-10-14T13:00:00Z') / 1000).tier, 'done')
  assert.strictEqual(R.toAnnounce(seen, NOW, Date.parse('2026-10-14T13:00:00Z') / 1000, []), null)   // 已經重置了：不主動說
})

test('answer：過期的預測按沒有預測回答', () => {
  const d = R.parse(base({ active_watch: watch('strong', 90, { expires_at: '2026-10-14T14:00:00.000Z' }) }))
  assert.strictEqual(R.answer(d, NOW, 0).tier, 'none')
})

test('toAnnounce：新的強預測 / 新宣佈說一次，說過的、弱的、過期的不說', () => {
  const strong = R.parse(base({ active_watch: watch('strong', 60) }))
  assert.deepStrictEqual(R.toAnnounce(strong, NOW, 0, []), { id: 'w:2026-10-14T10:00:00.000Z' })
  assert.strictEqual(R.toAnnounce(strong, NOW, 0, ['w:2026-10-14T10:00:00.000Z']), null)
  assert.ok(R.toAnnounce(R.parse(base({ active_watch: watch('elevated', 75) })), NOW, 0, []))
  assert.strictEqual(R.toAnnounce(R.parse(base({ active_watch: watch('elevated', 50) })), NOW, 0, []), null)
  assert.strictEqual(R.toAnnounce(R.parse(base({ active_watch: watch('elevated', null) })), NOW, 0, []), null)
  assert.strictEqual(R.toAnnounce(R.parse(base({ active_watch: watch('strong', 90, { expires_at: '2026-10-14T14:00:00.000Z' }) })), NOW, 0, []), null)
  assert.deepStrictEqual(R.toAnnounce(R.parse(base({ scheduled_reset: sched(), active_watch: watch('strong', 90) })), NOW, 0, []), { id: 'a:999' })
  assert.strictEqual(R.toAnnounce(null, NOW, 0, []), null)
})

test('windowText / fill', () => {
  assert.strictEqual(R.windowText('next 48h'), '接下來 48 小時內')
  assert.strictEqual(R.windowText('next 2 days'), '接下來 2 天內')
  assert.strictEqual(R.windowText('within 24h'), '24 小時內')   // 站點 10 月實際給的寫法（@chin190 在 #16 報的）
  assert.strictEqual(R.windowText('Within 2 days'), '2 天內')
  assert.strictEqual(R.windowText('in 12 hours'), '12 小時內')
  assert.strictEqual(R.windowText('this week'), '')   // 認不出：不硬塞英文進句子
  assert.strictEqual(R.fill('{chance}%，{window}', { chance: 85, window: '接下來 48 小時內' }), '85%，接下來 48 小時內')
  assert.strictEqual(R.fill('沒有 {x}', {}), '沒有 {x}')
})

test('LINES：每個臺詞鍵都有預設句', () => {
  for (const k of ['resetAnnounced', 'resetDone', 'resetHigh', 'resetMid', 'resetLow', 'resetNone', 'resetQuiet', 'resetOffline'])
    assert.ok(Array.isArray(R.LINES[k]) && R.LINES[k].length, k)
})

test('answer：視窗認不出時不帶 window', () => {
  const d = R.parse(base({ active_watch: watch('strong', 85, { forecast_window: 'sometime soon' }) }))
  const a = R.answer(d, NOW, 0)
  assert.strictEqual(a.tier, 'high')
  assert.deepStrictEqual(a.vars, { chance: 85 })
  assert.deepStrictEqual(R.answer(R.parse(base({ active_watch: watch('strong', 85, { forecast_window: 'within 24h' }) })), NOW, 0).vars, { chance: 85, window: '24 小時內' })
})

test('usableLines：只挑資料齊全的句子，角色的都用不了時退回預設句', () => {
  const own = ['{chance}%。{window}，可以期待一下。', '{chance}%，這次真有戲。']
  assert.deepStrictEqual(R.usableLines(own, 'resetHigh', { chance: 85 }), ['{chance}%，這次真有戲。'])
  assert.deepStrictEqual(R.usableLines(own, 'resetHigh', { chance: 85, window: '24 小時內' }), own)
  assert.deepStrictEqual(R.usableLines(['{window}……要重置了吧？吧？'], 'resetHigh', { chance: 85 }), R.LINES.resetHigh.filter(l => !l.includes('{window}')))
  assert.ok(R.usableLines(['{window}……'], 'resetHigh', { chance: 85 }).length > 0)
  assert.deepStrictEqual(R.usableLines(['有點風聲，{chance}% 吧'], 'resetMid', {}), ['有點風聲……別太指望。'])
  assert.deepStrictEqual(R.usableLines(['{x}'], 'nope', {}), [''])
})

// 官方沒提前宣佈、直接發「已經重置」（10-20 那次就是這樣）：重置後 5 小時內算「剛重置過」
const latestAt = (sec) => ({ id: 'r1', reset_type: 'regular', announced_at: new Date(sec * 1000).toISOString(), text: 't', source: { type: 'x_post', author: 'thsottiaux', url: 'u' } })
test('剛重置過：5 小時內問她答 recent，過了答 none', () => {
  const d = R.parse(base({ latest_reset: latestAt(NOW - 2 * 3600) }))
  const a = R.answer(d, NOW, 0)
  assert.strictEqual(a.tier, 'recent')
  assert.strictEqual(a.pose, 'reset')
  assert.strictEqual(a.key, 'resetRecent')
  assert.strictEqual(a.vars.ago, '2 小時前')
  assert.strictEqual(R.answer(R.parse(base({ latest_reset: latestAt(NOW - 20 * 60) })), NOW, 0).vars.ago, '20 分鐘前')
  assert.strictEqual(R.answer(R.parse(base({ latest_reset: latestAt(NOW - 6 * 3600) })), NOW, 0).tier, 'none')
})
test('剛重置過：額度下方的一行', () => {
  assert.strictEqual(R.notice(R.parse(base({ latest_reset: latestAt(NOW - 2 * 3600) })), 0, NOW), '✨ 2 小時前剛重置過')
  assert.strictEqual(R.notice(R.parse(base({ latest_reset: latestAt(NOW - 6 * 3600) })), 0, NOW), '')
})
test('剛重置過：新宣佈優先於「剛重置過」', () => {
  const d = R.parse(base({ latest_reset: latestAt(NOW - 3600), scheduled_reset: sched() }))
  assert.strictEqual(R.answer(d, NOW, 0).tier, 'announced')
  assert.ok(R.notice(d, 0, NOW).startsWith('🔔'))
})
test('剛重置過：主動說一次；說過或我們自己已經看到重置就不說', () => {
  const d = R.parse(base({ latest_reset: latestAt(NOW - 3600) }))
  assert.deepStrictEqual(R.toAnnounce(d, NOW, 0, []), { id: 'r:r1' })
  assert.strictEqual(R.toAnnounce(d, NOW, 0, ['r:r1']), null)
  assert.strictEqual(R.toAnnounce(d, NOW, NOW - 600, []), null)   // 額度變化已經判出重置、慶祝過了
  assert.strictEqual(R.toAnnounce(R.parse(base({ latest_reset: latestAt(NOW - 6 * 3600) })), NOW, 0, []), null)
})
test('LINES：resetRecent 有預設句', () => {
  assert.ok(R.LINES.resetRecent.length && R.LINES.resetRecent.every(l => l.includes('{ago}') || !l.includes('{')))
})
