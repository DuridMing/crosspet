// 額度條樣式（設定「額度顯示樣式：額度條」）：單家顯示時每個額度一行——名字、條、剩餘百分比、短重置時間；條按剩餘分三檔變色。
// 只做排版判斷；畫進小窗的 render 在瀏覽器裡用。設計見 docs/superpowers/specs/2026-10-21-quota-bars-design.md，測試 tests/quota_bars.test.js
// 資料：額度物件 q = { text, low, bars?, note? }。bars = [{ label, left（剩餘 0–1）, reset?（Unix 秒）, group? }]；
// 沒有 bars（舊版 Claude mod、餘額）就按 text 的固定格式拆，拆不動的那段照舊顯示文字
;(function (root) {
  const QA = root.QuotaAggregate || (typeof require !== 'undefined' ? require('./quota-aggregate.js') : null)

  // 顏色三檔（多於 30% 藍、10%–30% 橙、10% 及以下紅）：和合並顯示共用 quota-aggregate.js 的那一份
  const level = QA.level

  // 重置時間短寫：24 小時內「3:40」（小時不補零），7 天內「週一」，再遠「10/26」，按本機時區
  const WEEK = '日一二三四五六'
  function shortWhen(sec, nowSec) {
    const d = new Date(sec * 1000), ahead = sec - nowSec
    if (ahead < 86400) return `${d.getHours()}:${String(d.getMinutes()).padStart(2, '0')}`
    if (ahead < 7 * 86400) return `週${WEEK[d.getDay()]}`
    return `${d.getMonth() + 1}/${d.getDate()}`
  }

  const pctOf = left => Math.max(0, Math.min(100, Math.round((+left || 0) * 100)))
  function barRow(b, label, nowSec) {
    if (b.reset && b.reset <= nowSec) return { kind: 'bar', label, pct: 100, level: 'ok', when: null }   // 過了重置時間、還沒新資料：算滿
    const pct = pctOf(b.left)
    return { kind: 'bar', label, pct, level: level(pct), when: b.reset ? shortWhen(b.reset, nowSec) : null }
  }

  // 有幾個池（Antigravity：Gemini / Argon / Claude/GPT）時每個池只畫一條，和文字樣式一樣按 pickBar 挑；只有一個池時每個視窗一條
  function fromBars(bars, nowSec) {
    const groups = []
    for (const b of bars) {
      const g = b.group || ''
      let e = groups.find(x => x.g === g)
      if (!e) groups.push(e = { g, list: [] })
      e.list.push(b)
    }
    if (groups.length < 2) return bars.map(b => barRow(b, b.label, nowSec))
    return groups.map(e => { const b = QA.pickBar(e.list, e.g); return barRow(b, b.label, nowSec) })
  }

  const SEG = /^(.+?) 剩餘 (\d+)%(?: · (\S+) 重置)?(.*)$/
  function fromText(q) {
    let note = null
    const rows = String(q.text).split(/\s*｜\s*/).filter(Boolean).map(seg => {
      const m = SEG.exec(seg)
      if (!m) return { kind: 'text', text: seg, low: !!q.low }
      const [, label, p, when, tail] = m
      const t = tail.trim().replace(/^·\s*/, '')
      if (t) note = t   // GPT 問不到時的「· 15:20 的資料」
      const pct = Math.min(100, +p)
      return { kind: 'bar', label, pct, level: level(pct), when: when || null }
    })
    return { rows, note }
  }

  function of(q, nowSec) {
    if (!q || !q.text) return null
    const r = Array.isArray(q.bars) && q.bars.length ? { rows: fromBars(q.bars, nowSec), note: null } : fromText(q)
    if (!r.rows.some(x => x.kind === 'bar')) return null   // 一根條都沒有（只有餘額）：整塊照舊按文字樣式畫
    if (q.note) r.note = q.note
    return r
  }

  // 畫進名牌下面的小窗（只在瀏覽器裡用）
  function render(el, result) {
    const div = (cls, text) => { const d = document.createElement('div'); if (cls) d.className = cls; if (text != null) d.textContent = text; return d }
    const span = (cls, text) => { const s = document.createElement('span'); s.className = cls; if (text != null) s.textContent = text; return s }
    const timed = result.rows.some(r => r.when)   // 有一行帶重置時間就每行都留出那一格，幾根條才對得齊
    el.replaceChildren(...result.rows.map(r => {
      if (r.kind === 'text') return div(r.low ? 'low' : '', r.text)
      const row = div('qbar ' + r.level)
      const track = span('qtrack'), fill = document.createElement('i')
      fill.style.width = r.pct + '%'
      track.append(fill)
      row.append(span('ql', r.label), track, span('qp', r.pct + '%'))
      if (timed) row.append(span('qt', r.when || ''))
      return row
    }))
    if (result.note) el.append(div('qnote', result.note))
  }

  const api = { level, shortWhen, of, render }
  root.QuotaBars = api
  if (typeof module !== 'undefined') module.exports = api
})(typeof window !== 'undefined' ? window : globalThis)
