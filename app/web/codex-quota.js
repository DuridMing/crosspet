// GPT（Codex）額度排版：外殼交來問 Codex 服務得到的原始額度，這裡排成名牌下面那一行。兩個平臺共用這一份，規則只寫一遍。
// source：live = 剛問到的；stale = 這次沒問到（斷網、沒登入……），外殼把最後一次問到的數交過來，末尾標上那是幾點的資料
;(function (root) {
  const pad = n => String(n).padStart(2, '0')
  const label = mins => mins >= 7 * 24 * 60 ? '本週' : mins >= 24 * 60 ? '今日' : `${Math.max(1, Math.floor(mins / 60))}小時`
  function windowsOf(rl) {
    const out = []
    for (const key of ['primary', 'secondary']) {
      const w = rl && rl[key]
      if (!w) continue
      const used = w.usedPercent
      if (typeof used !== 'number') continue
      const mins = w.windowDurationMins ?? 0
      const reset = w.resetsAt
      out.push({ label: label(mins), used, reset: typeof reset === 'number' ? reset : 0 })
    }
    return out
  }
  function when(sec, nowSec) {
    const d = new Date(sec * 1000)
    return sec - nowSec < 86400 ? `${pad(d.getHours())}:${pad(d.getMinutes())}` : `${d.getMonth() + 1}/${d.getDate()}`
  }
  function view(p, nowSec) {
    if (!p || !p.rateLimits) return null
    const windows = windowsOf(p.rateLimits)
    if (!windows.length) return null
    // 過了重置時間、又沒有新資料的視窗現在是滿的，不算「快用完」
    const live = windows.filter(w => !w.reset || w.reset > nowSec)
    const maxUsed = live.length ? Math.max(...live.map(w => w.used)) : 0
    let marked = false
    const parts = windows.map(w => {
      if (w.reset > 0 && w.reset <= nowSec) return `${w.label} 剩餘 100%`
      let s = `${w.label} 剩餘 ${Math.max(0, 100 - Math.round(w.used))}%`
      if (!marked && w.used === maxUsed && w.reset > 0) { marked = true; s += ` · ${when(w.reset, nowSec)} 重置` }
      return s
    })
    let text = parts.join(' ｜ '), note
    if (p.source === 'stale' && typeof p.at === 'number') { const d = new Date(p.at * 1000); note = `${pad(d.getHours())}:${pad(d.getMinutes())} 的資料`; text += ` · ${note}` }
    // 額度條樣式（quota-bars.js）用的結構化資料：剩餘比例和重置時刻；過了重置時間的由那邊當成滿的。
    // 剩餘比例和文字一樣先取整，卡在顏色檔位邊上時百分比和顏色才對得上
    const bars = windows.map(w => ({ label: w.label, left: Math.max(0, 100 - Math.round(w.used)) / 100, ...(w.reset > 0 ? { reset: w.reset } : {}) }))
    return { text, low: maxUsed >= 90, windows, bars, ...(note ? { note } : {}), source: p.source || 'live' }
  }
  root.CodexQuota = { view }
  if (typeof module !== 'undefined') module.exports = { view }
})(typeof window !== 'undefined' ? window : globalThis)
