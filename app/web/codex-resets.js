// Codex 重置提醒（設定裡預設關）：資料來自第三方網站 codex-resets.com（根據 @thsottiaux 的推文整理，預測不是 OpenAI 的承諾）。
// 這裡只做判斷：介面的資料整理成三塊（官方宣佈 / 預測 / 統計）、額度下方顯示什麼、問她時回答哪一檔、要不要主動說。
// 聯網、顯示、說話在 index.html；純函式，網頁和測試（tests/codex_resets.test.js）共用。設計見 docs/superpowers/specs/2026-10-14-codex-resets-design.md
;(function (root) {
  const sec = s => { const t = typeof s === 'string' ? Date.parse(s) : NaN; return isNaN(t) ? null : t / 1000 }

  function parse(j) {
    const d = j && typeof j === 'object' ? j.data : null
    if (!d || typeof d !== 'object') return null
    const s = d.scheduled_reset, w = d.active_watch, l = d.latest_reset, st = d.stats
    const announced = s && typeof s.id === 'string' && sec(s.announced_at) !== null
      ? { id: s.id, at: sec(s.announced_at), for: sec(s.scheduled_for), url: s.source?.url || '' } : null
    const watch = w && (w.level === 'elevated' || w.level === 'strong') && typeof w.observed_at === 'string'
      ? { id: w.observed_at, level: w.level, chance: Number.isInteger(w.reset_chance_percent) ? w.reset_chance_percent : null,
          window: typeof w.forecast_window === 'string' ? w.forecast_window : '', expires: sec(w.expires_at) || 0 } : null
    const latest = l && typeof l.id === 'string' ? { id: l.id, at: sec(l.announced_at) || 0 } : null
    const stats = st && typeof st.days_since_last === 'number' && typeof st.avg_interval_days === 'number'
      ? { since: st.days_since_last, avg: st.avg_interval_days } : null
    return { announced, watch, latest, stats }
  }

  // 還有效的宣佈：站點沒記為已執行，我們也沒在宣佈之後看到 Codex 額度重置（resetSeenAt：index.html 的 quotaEvent 判出 reset 的時間，秒）
  function liveAnnounced(d, resetSeenAt) {
    const a = d?.announced
    if (!a || d.latest?.id === a.id || (resetSeenAt && resetSeenAt > a.at)) return null
    return a
  }
  // 宣佈過、而且已經執行了（站點記為已執行，或我們在宣佈之後看到了 Codex 額度重置）
  const doneAnnounced = (d, resetSeenAt) => !!(d?.announced && !liveAnnounced(d, resetSeenAt))
  const liveWatch = (d, now) => (d?.watch && (!d.watch.expires || d.watch.expires > now) ? d.watch : null)
  // 剛重置過：站點記的最近一次重置在 5 小時內（一個 5 小時額度視窗）。官方常常不提前宣佈、直接發「已經重置」（10-20 那次），
  // 這段時間問她要說「剛重置過」，不能說「沒風聲」
  const RECENT_SEC = 5 * 3600
  const recentReset = (d, now) => (now && d?.latest?.at && now - d.latest.at >= -60 && now - d.latest.at < RECENT_SEC ? d.latest : null)
  function agoText(sec) {
    const m = Math.max(1, Math.floor(sec / 60))
    return m < 60 ? `${m} 分鐘前` : `${Math.floor(m / 60)} 小時前`
  }

  const pad = n => String(n).padStart(2, '0')
  function fmt(s) { const t = new Date(s * 1000); return `${pad(t.getMonth() + 1)}/${pad(t.getDate())} ${pad(t.getHours())}:${pad(t.getMinutes())}` }

  function notice(d, resetSeenAt, now) {
    const a = liveAnnounced(d, resetSeenAt)
    if (a) return a.for ? `🔔 官方已宣佈重置 · 預計 ${fmt(a.for)}` : '🔔 官方已宣佈重置，還沒生效'
    const r = recentReset(d, now)
    return r ? `✨ ${agoText(now - r.at)}剛重置過` : ''
  }

  // forecast_window 翻成中文：next 48h → 接下來 48 小時內；within / in 24h → 24 小時內（站點 10 月起用 within）。
  // 認不出返回 ''：帶 {window} 的臺詞就不挑了，不把英文硬塞進句子
  function windowText(s) {
    const m = /^(next|within|in)\s+(\d+)\s*(h|hrs?|hours?|d|days?)$/i.exec(String(s || '').trim())
    if (!m) return ''
    const unit = /^h/i.test(m[3]) ? '小時' : '天'
    return /^next$/i.test(m[1]) ? `接下來 ${m[2]} ${unit}內` : `${m[2]} ${unit}內`
  }

  // 專屬立繪（docs/prompts/重置提醒/）；沒畫的在 index.html 的 POSE_FALLBACK 裡退回 reset / happy / hum / sulk
  const POSE = { recent: 'reset', announced: 'reset_bell', high: 'reset_hope', mid: 'reset_maybe', low: 'reset_nope', none: 'reset_nope', offline: 'reset_nope' }
  function tierOf(d, now, resetSeenAt) {
    if (liveAnnounced(d, resetSeenAt)) return 'announced'
    const w = liveWatch(d, now)
    if (!w) return 'none'
    if (w.chance === null) return w.level === 'strong' ? 'mid' : 'low'
    return w.chance >= 70 ? 'high' : w.chance >= 30 ? 'mid' : 'low'
  }

  function answer(d, now, resetSeenAt) {
    if (!d) return { tier: 'offline', pose: POSE.offline, key: 'resetOffline', vars: {} }
    if (doneAnnounced(d, resetSeenAt)) return { tier: 'done', pose: 'reset', key: 'resetDone', vars: {} }   // 不能說「沒風聲」
    const r = !liveAnnounced(d, resetSeenAt) && recentReset(d, now)
    if (r) return { tier: 'recent', pose: POSE.recent, key: 'resetRecent', vars: { ago: agoText(now - r.at) } }
    const tier = tierOf(d, now, resetSeenAt)
    if (tier === 'announced') { const a = liveAnnounced(d, resetSeenAt); return { tier, pose: POSE[tier], key: 'resetAnnounced', vars: { when: a.for ? fmt(a.for) : '很快' } } }
    if (tier === 'none') return d.stats
      ? { tier, pose: POSE.none, key: 'resetNone', vars: { since: d.stats.since.toFixed(1), avg: d.stats.avg.toFixed(1) } }
      : { tier, pose: POSE.none, key: 'resetQuiet', vars: {} }
    const w = liveWatch(d, now)
    const key = { high: 'resetHigh', mid: 'resetMid', low: 'resetLow' }[tier]
    const vars = {}
    if (w.chance !== null) vars.chance = w.chance
    const window = windowText(w.window)
    if (window) vars.window = window
    return { tier, pose: POSE[tier], key, vars }
  }

  // 主動說：新宣佈，或新的強預測（strong 或 ≥70%）；told 是已經說過的 id（宣佈 'a:<id>'，預測 'w:<observed_at>'）
  function toAnnounce(d, now, resetSeenAt, told) {
    const a = liveAnnounced(d, resetSeenAt)
    if (a) return told.includes('a:' + a.id) ? null : { id: 'a:' + a.id }
    const r = recentReset(d, now)
    if (r && !(resetSeenAt && resetSeenAt >= r.at)) return told.includes('r:' + r.id) ? null : { id: 'r:' + r.id }   // 我們自己已經看到重置、慶祝過了就不說
    const w = liveWatch(d, now)
    if (!w || !(w.level === 'strong' || (w.chance ?? 0) >= 70)) return null
    return told.includes('w:' + w.id) ? null : { id: 'w:' + w.id }
  }

  // 能用的臺詞：句子裡的 {佔位} 在 vars 裡都有。角色自己的（own）一句都用不了就退回預設句，再不行就不說話
  function usableLines(own, key, vars) {
    const ok = list => (list || []).filter(l => [...String(l).matchAll(/\{(\w+)\}/g)].every(m => m[1] in vars))
    const mine = ok(own)
    if (mine.length) return mine
    const dflt = ok(LINES[key])
    return dflt.length ? dflt : ['']
  }

  const fill = (line, vars) => String(line).replace(/\{(\w+)\}/g, (m, k) => (k in vars ? String(vars[k]) : m))

  // 預設臺詞（角色 character.json 的 lines 裡寫了同名鍵就用角色自己的）
  const LINES = {
    resetAnnounced: ['官方說了，要重置！預計 {when}。'],
    resetDone: ['官方說的重置已經到了，額度滿啦！'],
    resetRecent: ['剛重置過啦！{ago}，額度滿滿～', '{ago}剛重置過，放心用！'],
    resetHigh: ['這次很有戲：{chance}%，{window}。', '這次很有戲：{chance}%！'],
    resetMid: ['有點風聲，{chance}% 吧，別太指望。', '有點風聲……別太指望。'],
    resetLow: ['有動靜，但不太像。'],
    resetNone: ['最近沒風聲。上次重置是 {since} 天前，平均 {avg} 天一次。'],
    resetQuiet: ['最近沒風聲。'],
    resetOffline: ['我這會兒連不上那個網站……'],
  }

  const api = { parse, notice, answer, toAnnounce, windowText, usableLines, fill, fmt, LINES }
  root.CodexResets = api
  if (typeof module !== 'undefined') module.exports = api
})(typeof window !== 'undefined' ? window : globalThis)
