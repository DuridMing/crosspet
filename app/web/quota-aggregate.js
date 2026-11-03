// 額度聚合：設定裡「合併顯示各家額度」開啟後，小窗不只顯示當前角色的額度，而是每家一行排成表：
//   Claude   5h 98% · 07:50 │ 週 99%
//   GPT      5h 12% · 04:31 │ 週 78%
// 只做排版，不碰姿態：「累了」「額度緊張」仍然只看她自己的額度（見 index.html 的 setQuota / restPose）。
// 純函式 rows() 和測試（tests/quota_aggregate.test.js）共用；render() 把結果畫進小窗，index.html 和圖例指令碼共用。
;(function (root) {
  const SHORT = { '5小時': '5h', '本週': '週' }   // 視窗名縮寫；別的（花費、Gemini 的模型池）照原樣
  const LOW_LEFT = 10                             // 剩餘 ≤ 10% 算緊張，和各家接入的 low 判斷（用了 90%）一致

  // 一段額度原文（「5小時 剩餘 98% · 07:50 重置」）拆成幾塊，只有百分比可以單獨標紅。
  // 認不出句式（餘額、以後新增的寫法）就整段原樣顯示，來源標了 low 才整段變紅
  function compact(seg, sourceLow) {
    const m = /^(.+?) 剩餘 (\d+)%(?: · (\S+) 重置)?(.*)$/.exec(seg)
    if (!m) return [{ text: seg, low: sourceLow }]
    const [, label, pct, time, tail] = m
    const short = SHORT[label] || label
    // label、pct 給額度條樣式用（barCell）：文字樣式照舊只看 text、low
    const parts = [{ text: short + ' ', label: short }, { text: pct + '%', low: +pct <= LOW_LEFT, pct: +pct }]
    if (time) parts.push({ text: ' · ' + time })
    if (tail.trim()) parts.push({ text: ' ' + tail.trim() })   // GPT 問不到時的「· 11:30 的資料」
    return parts
  }

  // ---- 額度條樣式（設定「額度顯示樣式：額度條」）：每格換成短條 ----
  // 顏色三檔：多於 30% 藍（ok）、10%–30% 橙（mid）、10% 及以下紅（low，和文字樣式變紅的門檻一致）。單家顯示（quota-bars.js）也用這一份
  const level = pct => (pct > 30 ? 'ok' : pct > LOW_LEFT ? 'mid' : 'low')
  const WINDOWS = new Set(['5h', '週', '今日', '5小時', '本週'])
  // 一格（compact 拆好的幾塊）→ { label, pct, level }：視窗名不寫（同一列都是同一種視窗），池名（Arg、C/G）留著；認不出的（餘額）返回 null，照舊顯示文字
  function barCell(parts) {
    const p = parts.find(x => x.pct != null)
    if (!p) return null
    const words = (parts.find(x => x.label != null)?.label || '').split(' ').filter(w => !WINDOWS.has(w))
    return { label: words.join(' ') || null, pct: p.pct, level: level(p.pct) }
  }

  // ---- 額度條 bars → 格子 ----
  // 一項 { label, left（剩餘比例 0–1）, reset?（Unix 秒）, group? }。同一個池的視窗挨著排；group 是池名（沒有就當成同一個池）
  const pctOf = left => Math.round(Math.max(0, Math.min(1, +left || 0)) * 100)
  function resetText(sec) {
    const d = new Date(sec * 1000), p2 = n => String(n).padStart(2, '0')
    return d - Date.now() < 86400e3 ? p2(d.getHours()) + ':' + p2(d.getMinutes()) : (d.getMonth() + 1) + '/' + d.getDate()
  }
  // 拼成和 text 一樣的一段原文，交給 compact 去縮寫、標紅
  const seg = (label, b) => (label ? label + ' ' : '') + '剩餘 ' + pctOf(b.left) + '%' + (b.reset ? ' · ' + resetText(b.reset) + ' 重置' : '')
  const windowOf = (b, group) => (group && b.label.startsWith(group) ? b.label.slice(group.length) : b.label).trim()
  const POOL_SHORT = { Argon: 'Arg' }   // 池名放進格子裡太長的縮短
  const CG = /claude|gpt/i, CG_ROW = 'Agy C/G'

  // 一個池只挑一個視窗放進格子（和解析端 text 的挑法一致）：5小時沒滿就用 5小時；否則用最緊的；
  // 例外：最緊的 ≤10% 時不管 5小時怎樣都用它；一樣緊的取重置更晚的
  function pickBar(list, group) {
    let tight = null
    for (const b of list) if (!tight || b.left < tight.left || (b.left === tight.left && (b.reset || 0) > (tight.reset || 0))) tight = b
    const five = list.find(b => windowOf(b, group) === '5小時')
    return tight.left > 0.1 && five && five.left < 1 ? five : tight
  }

  // 只有一個池（Claude / GPT 這類）：每個視窗一格，一家一行，和拆 text 的結果一樣。
  // 有幾個池（Antigravity）：第一個池（角色自己的）+ 獨立池（Argon 等）各一格放一行，Claude/GPT 池的各個視窗另起一行「Agy C/G」，
  // 和 Claude / GPT 自己的「5h | 週」兩格對齊
  // fold：底欄放不下時（見 rows），Claude/GPT 池不另起一行，和獨立池一樣挑一格並回第一行（「C/G 週 70%」）
  function barRows(bars, ownName, fold) {
    const cell = (label, b) => seg(label, fold ? { left: b.left } : b)   // 併成一行時不寫重置時間，不然三格太寬
    const groups = []
    for (const b of bars) {
      const g = b.group || ''
      let e = groups.find(x => x.g === g)
      if (!e) groups.push(e = { g, list: [] })
      e.list.push(b)
    }
    if (groups.length < 2) return [{ name: ownName, cells: bars.map(b => seg(b.label, b)) }]
    const main = [], cg = []
    groups.forEach((e, i) => {
      if (i > 0 && CG.test(e.g) && !fold) { e.list.forEach(b => cg.push(seg(windowOf(b, e.g), b))); return }
      if (i > 0 && CG.test(e.g)) { const b = pickBar(e.list, e.g); const win = windowOf(b, e.g); main.push(cell('C/G ' + (SHORT[win] || win), b)); return }
      const b = pickBar(e.list, e.g), win = windowOf(b, e.g)
      main.push(cell(i === 0 ? win : [POOL_SHORT[e.g] || e.g, e.list.length > 1 ? win : ''].filter(Boolean).join(' '), b))
    })
    return [main.length && { name: ownName, cells: main }, cg.length && { name: CG_ROW, cells: cg }].filter(Boolean)
  }

  // quota：{ <角色 id>: { text, low } | null }；own：當前角色 id，排在最前；all：角色 id 的順序；
  // nameOf：id → 名字。返回 [{ id, name, cells: [parts] }]，一家 AI 一行（來源給了 bars 且有幾個池的可以有幾行），每個額度視窗一格（原文用「｜」隔開）。
  // 沒有額度（沒接入、沒開啟開關、讀不到）的 AI 直接跳過，不佔行
  function rows(quota, own, all, nameOf, fold = false) {
    const out = []
    // 其餘按 id 字母序：不能指望 all 自帶順序——macOS 上角色清單是 Swift 字典序列化出來的，每次啟動的順序都不一樣
    for (const id of [own, ...all.filter(i => i !== own).sort()]) {
      const q = quota[id]
      if (!q || !q.text) continue
      // 來源給了分池的結構化額度 bars（[{ label, left, reset?, group }]，目前是 Antigravity）：不拆 text，從 bars 排版。
      // 不分池的 bars（GPT、Claude）只給額度條樣式的單家顯示用；合併顯示照舊拆 text，過期算滿、「幾點的資料」、只給最緊的那格寫時間都在 text 裡排好了
      if (Array.isArray(q.bars) && q.bars.some(b => b.group)) {
        for (const r of barRows(q.bars, nameOf(id), fold)) out.push({ id, name: r.name, cells: r.cells.map(seg => compact(seg, !!q.low)) })
        continue
      }
      const cells = String(q.text).split(/\s*｜\s*/).filter(Boolean).map(seg => compact(seg, !!q.low))
      if (cells.length) out.push({ id, name: nameOf(id), cells })
    }
    // Antigravity 拆成兩行後總共超過收緊字號也放不下的行數（底欄高度預算，見 render）：把它的兩行並回一行再排一次
    if (!fold && out.length > LOOSE_ROWS + 1 && out.length > new Set(out.map(r => r.id)).size) return rows(quota, own, all, nameOf, true)
    return out
  }

  // 名字太長就截斷：按顯示寬度算（中日韓字元算 2，其餘算 1），不靠 CSS 的省略號——表格裡列寬會被擠到比名字還窄
  const NAME_WIDTH = 12, NAME_WIDTH_TIGHT = 9   // 表放不下、收緊了字號還不夠時，名字再短一點
  const widthOf = ch => (ch.codePointAt(0) >= 0x2e80 ? 2 : 1)
  function shortName(name, max = NAME_WIDTH) {
    const chars = [...name]
    if (chars.reduce((w, c) => w + widthOf(c), 0) <= max) return name
    let w = 0, out = ''
    for (const c of chars) {
      if (w + widthOf(c) > max - 1) break   // 留一格給「…」
      w += widthOf(c); out += c
    }
    return out + '…'
  }

  const LOOSE_ROWS = 3   // 正常字號放得下的行數（見 render 裡的說明）
  const isCrowded = rowCount => rowCount > LOOSE_ROWS

  // 畫成對齊的表：名字一列、每個視窗一列，列寬自動撐開，不要求等寬字型。
  // 格子比別的行少的行，最後一格佔滿剩下的列（餘額、Gemini 的長文本不撐歪別的行）
  // style：'bars' 時認得出百分比的格子畫成「池名（有才畫）+ 短條 + 百分比」，其餘照舊（見 barCell）
  function render(el, list, nameWidth = NAME_WIDTH, style = 'text') {
    el.replaceChildren()
    const n = Math.max(0, ...list.map(r => r.cells.length))
    el.className = 'agg'
    el.style.gridTemplateColumns = n ? `auto repeat(${n}, auto)` : ''
    const div = (cls, text) => { const d = document.createElement('div'); d.className = cls; if (text != null) d.textContent = text; return d }
    for (const r of list) {
      const name = div('qn', shortName(r.name, nameWidth))
      name.title = r.name   // 名字被截斷時，滑鼠停上去能看全
      el.append(name)
      r.cells.forEach((parts, i) => {
        const cell = div('qc')
        if (i === r.cells.length - 1 && r.cells.length < n) cell.style.gridColumn = `span ${n - r.cells.length + 1}`
        const bar = style === 'bars' && barCell(parts)
        if (bar) {
          cell.classList.add('qbarcell', bar.level)
          const span = (cls, text) => { const s = document.createElement('span'); s.className = cls; if (text != null) s.textContent = text; return s }
          const track = span('qtrack'), fill = document.createElement('i')
          fill.style.width = bar.pct + '%'
          track.append(fill)
          if (bar.label) cell.append(span('ql', bar.label))
          cell.append(track, span('qp', bar.pct + '%'))
        } else for (const p of parts) {
          const s = document.createElement('span'); s.textContent = p.text
          if (p.low) s.className = 'low'
          cell.append(s)
        }
        el.append(cell)
      })
    }
    // 收緊（字號 9px、行高 1.1）的兩個條件：
    // 1. 行太多：底欄（#foot）留給額度小窗的高度預算只夠 3 行正常字號，或 4 行收緊後的字號；
    //    4 家都有時不管寬不寬都要收緊，不然人物會被頂上去
    // 2. 視窗放不下（Gemini 的模型池名字長）：字和間距收緊一點，別把名字列擠沒
    if (isCrowded(list.length) || el.scrollWidth > el.clientWidth + 1) el.classList.add('tight')
    // 收緊了還放不下（名字長 + 四家都有 + 微軟雅黑比蘋方寬）：名字再截短一點，重畫一次
    if (el.scrollWidth > el.clientWidth + 1 && nameWidth > NAME_WIDTH_TIGHT) render(el, list, NAME_WIDTH_TIGHT, style)
  }

  root.QuotaAggregate = { rows, render, compact, shortName, isCrowded, pickBar, windowOf, barCell, level }
  if (typeof module !== 'undefined') module.exports = { rows, compact, shortName, isCrowded, pickBar, windowOf, barCell, level }   // pickBar、windowOf 額度條樣式（quota-bars.js）也用
})(typeof window !== 'undefined' ? window : globalThis)
