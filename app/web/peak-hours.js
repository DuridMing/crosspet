// 峰谷時段：有的 AI 按時段分兩檔價（DeepSeek 空閒半價、GLM 程式設計套餐非高峰積分減半）。
// 這裡只做判斷：某一刻是高峰還是空閒、這一段從幾點到幾點、今天的時間條怎麼畫、要不要按時段換衣服。
// 一律按北京時間（UTC+8），和電腦設的時區無關；法定節假日暫不識別（週末全天空閒）。
// 純函式，網頁和測試（tests/peak_hours.test.js）共用。設計見 docs/superpowers/specs/2026-10-19-peak-valley-design.md
;(function (root) {
  const BJ = 8 * 3600e3, DAY = 86400e3

  // 規則：days = 有高峰的星期幾（1 = 週一），peak = 高峰區間（左閉右開）。key 是角色 id
  const RULES = {
    // https://api-docs.deepseek.com/zh-cn/quick_start/pricing 注 (2)，2026-10-19 查
    deepseek: { days: [1, 2, 3, 4, 5], peak: [['09:00', '12:00'], ['14:00', '18:00']], label: '工作日 9–12、14–18 點高峰（價格翻倍），其餘時間和週末半價' },
    // https://docs.bigmodel.cn/cn/coding-plan/overview「積分抵扣」，2026-10-19 查
    glm: { days: [1, 2, 3, 4, 5], peak: [['14:00', '18:00']], label: '工作日 14–18 點高峰，其餘時間和週末積分減半' },
  }

  const mins = s => { const [h, m] = s.split(':').map(Number); return h * 60 + m }
  const dayStart = ms => Math.floor((ms + BJ) / DAY) * DAY - BJ   // 北京時間當天 0 點
  const weekday = ms => new Date(ms + BJ).getUTCDay()

  // 北京時間當天的色塊（分鐘，覆蓋 0–1440）
  function daySegments(rule, ms) {
    if (!rule.days.includes(weekday(ms))) return [{ kind: 'valley', from: 0, to: 1440 }]
    const out = []
    let at = 0
    for (const [a, b] of rule.peak) {
      if (mins(a) > at) out.push({ kind: 'valley', from: at, to: mins(a) })
      out.push({ kind: 'peak', from: mins(a), to: mins(b) })
      at = mins(b)
    }
    if (at < 1440) out.push({ kind: 'valley', from: at, to: 1440 })
    return out
  }

  // 這一刻在哪一段：{ kind, start, until }（毫秒）。跨夜、跨週末的空閒連成一段（週五 18:00 到週一 9:00）
  function periodAt(rule, ms) {
    const blocks = []
    for (let d = -8; d <= 8; d++) {
      const base = dayStart(ms) + d * DAY
      for (const s of daySegments(rule, base)) {
        const from = base + s.from * 60e3, to = base + s.to * 60e3, last = blocks[blocks.length - 1]
        if (last && last.kind === s.kind && last.until === from) last.until = to
        else blocks.push({ kind: s.kind, start: from, until: to })
      }
    }
    return blocks.find(b => b.start <= ms && ms < b.until)
  }

  const segments = (rule, ms) => daySegments(rule, dayStart(ms))
  const ticks = rule => [0, ...rule.peak.flat().map(s => mins(s) / 60), 24]
  const nowMinute = ms => Math.floor((ms - dayStart(ms)) / 60e3)

  // 按時段換衣服：choice = { peak: 皮膚, valley: 皮膚 }（'' = 原版，沒寫 = 不換）；appliedKey = 上次按規則處理到的那一段。
  // 還在同一段就不換（這一段裡你手動換的不會被換回去）；進入了新的一段才按設定換
  function decide(rule, ms, choice, appliedKey) {
    const p = periodAt(rule, ms), key = String(p.start)
    const want = choice?.[p.kind]
    return { key, kind: p.kind, skin: key !== appliedKey && typeof want === 'string' ? want : null }
  }

  // 文字款（設定裡「峰谷時段顯示：文字」）：只寫「高峰」或「空閒」（loki：不要太多字）
  const NAME = { peak: '高峰', valley: '空閒' }
  function statusText(rule, ms) {
    const kind = periodAt(rule, ms).kind
    return { kind, text: NAME[kind] }
  }

  const api = { RULES, periodAt, segments, ticks, nowMinute, decide, statusText }
  root.PeakHours = api
  if (typeof module !== 'undefined') module.exports = api
})(typeof window !== 'undefined' ? window : globalThis)
