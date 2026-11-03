// 反饋規則：使用者選了哪類問題，就附這一類排查用得上的資訊（設計見 docs/superpowers/specs/2026-10-21-feedback-redesign-design.md）。
// 只做判斷和排版：選哪些段、日誌怎麼篩、記錄怎麼排、超長怎麼縮；段落的原始內容由外殼收集（已打碼）。
// 純函式，反饋視窗（feedback.html）和測試（tests/feedback_plan.test.js）共用
;(function (root) {
  const KINDS = [
    { id: 'gone', label: '她不見了 / 打不開', sub: '崩潰、閃退、找不到她', follow: null, askStill: true, hint: '什麼時候不見的、之前在做什麼？例：右鍵換了個角色她就沒了' },
    { id: 'action', label: 'AI 幹活時她沒反應', sub: '或者動作、表情不對', follow: 'ai', askStill: true, hint: '它在幹什麼的時候？例：它在思考時，她還在睡覺' },
    { id: 'character', label: '角色換得不對', sub: '換錯人、少了角色', follow: 'character', askStill: true, hint: '換成了誰、應該是誰？例：用 Codex 時變成了 DeepSeek' },
    { id: 'quota', label: '額度 / 餘額不對', sub: '', follow: 'quota', askStill: true, hint: '顯示成了什麼、應該是多少？例：餘額一直顯示 0' },
    { id: 'display', label: '畫面不對', sub: '錯位、模糊、大小不對', follow: null, askStill: true, hint: '哪裡不對？例：氣泡被擠到視窗外面' },
    { id: 'update', label: '更新失敗', sub: '', follow: null, askStill: true, hint: '到哪一步出的問題、提示了什麼？' },
    { id: 'perf', label: '卡頓 / 耗電', sub: '', follow: null, askStill: true, hint: '什麼時候卡、卡成什麼樣？例：開著 Antigravity 時風扇狂轉' },
    { id: 'idea', label: '建議 / 想要 / 誇誇', sub: '立繪建議、想要新角色或新 AI、誇誇', follow: null, askStill: false, hint: '想說什麼都行（可以不填）' },
    { id: 'other', label: '其他', sub: '', follow: null, askStill: true, hint: '具體是哪裡？' },
  ]
  const STILL = [{ id: 'now', label: '還在' }, { id: 'recent', label: '剛過去（幾分鐘內）' }, { id: 'earlier', label: '更早' }]
  const AI_NAMES = { codex: 'Codex', 'claude-hooks': 'Claude Code', 'claude-mod': 'Claude Code（增強版）', deepseek: 'DeepSeek Harness', antigravity: 'Antigravity',
    gemini: 'Gemini CLI', qwen: 'Qwen Code', workbuddy: 'WorkBuddy', zcode: 'ZCode', hermes: 'Hermes Agent', grok: 'Grok Build' }
  const QUOTA_NAMES = { gpt: 'GPT（Codex）額度', gemini: 'Gemini（Antigravity）額度', deepseek: 'DeepSeek 餘額', claude: 'Claude 額度' }
  // 接入 → 它的狀態寫在哪個角色名下（多模型宿主認不出的模型寫 current）、輪次和換模型記錄用的宿主名（<宿主>-turns.json / -host.json）
  const STATE_OF = { codex: 'gpt', 'claude-hooks': 'claude', 'claude-mod': 'claude', deepseek: 'deepseek', antigravity: 'gemini', gemini: 'gemini',
    qwen: 'qwen', workbuddy: 'current', zcode: 'glm', hermes: 'current', grok: 'grok' }
  const HOST_OF = { codex: 'gpt', 'claude-hooks': 'claude', 'claude-mod': 'claude', deepseek: 'deepseek', antigravity: 'gemini', gemini: 'gemini',
    qwen: 'qwen', workbuddy: 'workbuddy', zcode: 'zcode', hermes: 'hermes', grok: 'grok' }
  const DIAG_HEADING = { codex: '[Codex]', 'claude-hooks': '[Claude]', 'claude-mod': '[Claude]', deepseek: '[DeepSeek Harness]', gemini: '[Gemini CLI]',
    qwen: '[Qwen Code]', antigravity: '[Antigravity]', zcode: '[ZCode]', hermes: '[Hermes]', workbuddy: '[WorkBuddy]', grok: '[Grok Build]' }
  const TITLES = { window: '視窗', displays: '顯示器', front: '前臺', hidden: '藏起來的原因', page: '網頁自報', renderRestart: '渲染重啟記錄', crashes: '崩潰報告',
    install: '安裝', log: '相關日誌', updateLog: '更新日誌', seen: '最近收到訊息', diag: '接入診斷', petEvents: '桌寵收到事件後的處理', switches: '最近換角色',
    settings: '設定', characters: '角色資料夾', perf: '效能', dpi: '縮放和畫布' }
  const LOG_WORDS = {
    gone: ['全屏', '視窗', '置頂', '層級', '渲染', '啟動', '崩潰', '介面異常', '藏', '立繪載入失敗', '接班', '安裝不完整'],
    action: ['狀態', '接入', '自動出現', '鉤子'],
    character: ['同步角色', '換角色', '角色'],
    quota: ['額度', '餘額'],
    display: ['縮放', '畫布', '網頁大小'],
    perf: ['渲染', '定時任務'],
  }
  const hostOf = target => (target === 'codex-desktop' ? 'codex' : target)

  function followOptions(kind, facts) {
    const k = KINDS.find(x => x.id === kind)
    if (k?.follow === 'ai') return [...(facts.integrations || []).flatMap(id => id === 'codex'
      ? [{ id, label: 'Codex' }, { id: 'codex-desktop', label: 'ChatGPT 桌面版' }] : [{ id, label: AI_NAMES[id] || id }]), { id: 'unsure', label: '不確定' }]
    if (k?.follow === 'character') return [...(facts.characters || []).map(c => ({ id: c.id, label: c.name })), { id: 'missing', label: '角色列表裡少了' }]
    if (k?.follow === 'quota') return (facts.quotas || []).map(id => ({ id, label: QUOTA_NAMES[id] || id }))
    return []
  }

  function sectionsFor(kind, target, facts) {
    switch (kind) {
      case 'gone': return ['window', 'displays', 'front', 'hidden', 'page', 'renderRestart', 'crashes', 'install', 'log']
      case 'action': {
        if (target === 'unsure') return ['seen', ...(facts?.integrations || []).map(i => 'events:' + i), 'petEvents', 'page', 'settings', 'log']
        const host = hostOf(target)
        return ['diag', 'seen', 'events:' + host, 'petEvents', 'page', `state:${STATE_OF[host] || host}|${HOST_OF[host] || host}`, 'settings', 'log']
      }
      case 'character': return ['characters', 'switches', 'front', 'settings', 'log']
      case 'quota': return ['quota:' + target, 'settings', 'log']
      case 'display': return ['dpi', 'displays', 'page', 'log']
      case 'update': return ['updateLog', 'install']
      case 'perf': return ['perf', 'settings', 'log']
      // 其他：說不清是哪類時用，每類都帶一點（體積由 buildCard 的縮減兜著）
      case 'other': return ['window', 'displays', 'front', 'hidden', 'page', 'settings', 'seen', ...(facts?.integrations || []).map(i => 'events:' + i), 'petEvents', 'switches', 'install', 'log']
      default: return []
    }
  }

  function sliceDiag(diag, integration) {
    const lines = String(diag || '').split('\n')
    const head = DIAG_HEADING[integration]
    const i = head ? lines.findIndex(l => l.startsWith(head)) : -1
    if (i < 0) return integration === 'workbuddy' ? lines.filter(l => /^\s*WorkBuddy/.test(l)).join('\n') : ''   // 舊版診斷只有縮排的一行
    let j = i + 1
    while (j < lines.length && !lines[j].startsWith('[')) j++
    return lines.slice(i, j).join('\n')
  }

  function filterLog(text, kind, max = 60) {
    const words = LOG_WORDS[kind]
    const lines = String(text || '').split('\n').filter(l => l.trim() && (!words || words.some(w => l.includes(w))))
    return lines.slice(-max).join('\n')
  }

  const hms = t => { const d = new Date(Number(t) * 1000); return [d.getHours(), d.getMinutes(), d.getSeconds()].map(n => String(n).padStart(2, '0')).join(':') }
  function parseJsonl(text) {
    return String(text || '').split('\n').map(l => { try { return l.trim() ? JSON.parse(l) : null } catch { return null } }).filter(Boolean)
  }
  // 鉤子事件記錄和桌寵處理記錄都用這個：時間 [角色] 事件 工具或姿態 → 結果 詳情 [會話]
  function formatLedger(jsonl, n, empty = '沒有記錄（外掛版本早於 1.3.8，或從沒收到訊息）') {
    const rows = parseJsonl(jsonl).slice(-n)
    if (!rows.length) return empty
    return rows.map(r => `${hms(r.t)} ${[r.char, r.ev, r.tool || r.pose].filter(Boolean).join(' ')} → ${[r.result, r.detail].filter(Boolean).join(' ')}${r.sid ? ` [${r.sid}]` : ''}`).join('\n')
  }
  function formatSwitches(jsonl, n) {
    const rows = parseJsonl(jsonl).slice(-n)
    if (!rows.length) return '沒有記錄'
    return rows.map(r => `${hms(r.t)} ${r.from || '—'} → ${r.to}${r.skin ? '·' + r.skin : ''}  ${r.why}${r.via ? ' ' + r.via : ''}`).join('\n')
  }
  function formatSeen(json) {
    let m; try { m = JSON.parse(json) } catch { return String(json || '') }
    const now = Date.now() / 1000
    return Object.keys(m).sort().map(k => `${AI_NAMES[k] || k} ${m[k] ? Math.round((now - m[k]) / 60) + ' 分鐘前' : '從沒收到'}`).join('，')
  }
  function formatPage(json, n) {
    let p; try { p = JSON.parse(json) } catch { return String(json || '') }
    const head = `角色 ${p.char}${p.skin ? '·' + p.skin : ''}，姿態 ${p.pose}；圖 ${p.img?.file || '—'}（${p.img?.complete ? '已載入' : '沒載入完'} ${p.img?.w}×${p.img?.h}），`
      + `備用 ${p.spare?.file || '—'}；換圖中 ${p.swapping ? '是' : '否'}，縮邊 ${p.tucked || '否'}，彩蛋 ${p.egg || '否'}，頁面藏著 ${p.pageHidden ? '是' : '否'}`
    const errors = (p.imageErrors || []).map(e => `${hms(e.t)} ${e.file}`).join('，')
    const verdicts = (p.verdicts || []).slice(-n).map(v => `${hms(v.t)} ${v.id} ${v.ev} ${v.pose} → ${v.verdict}`).join('\n')
    return [head, errors && '圖片載入失敗：' + errors, verdicts && '網頁的判斷：\n' + verdicts].filter(Boolean).join('\n')
  }

  // 某一家沒有事件記錄：結合「最近收到訊息」分清是收到過卻沒記（外掛比這次改版舊、或寫不了資料目錄）還是從沒收到
  function emptyEvents(integration, seenJson) {
    let seen = 0; try { seen = Number(JSON.parse(seenJson || '{}')[integration]) || 0 } catch {}
    return seen ? '收到過訊息卻沒有記錄（外掛版本比這次改版舊，或者寫不了資料目錄）' : '沒有記錄：從沒收到這個 AI 的訊息'
  }
  function render(id, raw, kind, target, n, sections) {
    return String(renderRaw(id, raw, kind, target, n, sections)).slice(0, n.text)   // 每段都有上限，整張卡才縮得進 19000 位元組
  }
  function renderRaw(id, raw, kind, target, n, sections) {
    if (id === 'diag') return sliceDiag(raw, hostOf(target)) || '（診斷裡沒有這一家）'
    if (id === 'log') return filterLog(raw, kind, n.log)
    if (id === 'seen') return formatSeen(raw)
    if (id === 'page') return formatPage(raw, n.ledger)
    if (id === 'switches') return formatSwitches(raw, n.ledger)
    if (id === 'petEvents') return formatLedger(raw, n.ledger, '沒有記錄')
    if (id.startsWith('events:')) return formatLedger(raw, target === 'unsure' || kind === 'other' ? Math.min(10, n.ledger) : n.ledger, emptyEvents(id.slice(7), sections.seen))
    return String(raw).slice(0, n.text)
  }
  function title(id) {
    if (id.startsWith('events:')) return `事件記錄 · ${AI_NAMES[id.slice(7)] || id.slice(7)}`
    if (id.startsWith('state:')) return `狀態檔案 · ${id.slice(6).split('|')[0]}`
    if (id.startsWith('quota:')) return `額度 · ${QUOTA_NAMES[id.slice(6)] || id.slice(6)}`
    return TITLES[id] || id
  }

  const LIMIT = 19000
  function buildCard({ kind, target, still, text, attach, facts, partial }) {
    const k = KINDS.find(x => x.id === kind) || KINDS[KINDS.length - 1]
    const opt = followOptions(kind, facts).find(o => o.id === target)
    const h = facts.header || {}
    const pt = content => ({ tag: 'plain_text', content })
    const stillLabel = STILL.find(s => s.id === still)?.label
    const choice = [`型別 ${k.label}`, opt && `補選 ${opt.label}`, stillLabel && `問題還在嗎 ${stillLabel}`].filter(Boolean)
    const meta = [`版本 ${h.version || '?'}（${h.platform || '?'}）`, ...choice]
    if (attach) meta.push(`系統 ${h.os || '?'}`, `機器 ${String(h.install || '').slice(0, 8)}`, `角色 ${h.character || '—'}${h.skin ? '·' + h.skin : ''}`, `已執行 ${h.uptimeMin ?? '?'} 分鐘`)
    const head = [{ tag: 'div', text: pt(text || '（沒寫）') }, { tag: 'hr' }, { tag: 'div', fields: meta.map(m => ({ is_short: true, text: pt(m) })) }]
    if (attach && partial) head.push({ tag: 'note', elements: [pt('排查資訊沒來得及收齊（外殼 8 秒內沒交回來），這份只附了已收到的部分')] })
    const ids = attach ? sectionsFor(kind, target, facts).filter(id => facts.sections?.[id] != null) : []
    const make = n => ({
      msg_type: 'interactive',
      card: {
        header: { template: kind === 'gone' || kind === 'update' ? 'red' : kind === 'idea' ? 'green' : 'orange',
          title: pt(`CrossPet 反饋 · ${k.label}${opt ? ' · ' + opt.label : ''}`) },
        elements: [...head, ...ids.map(id => ({ tag: 'note', elements: [pt(`${title(id)}：\n${render(id, facts.sections[id], kind, target, n, facts.sections)}`)] }))],
      },
    })
    // 超長按順序縮：先日誌行數，再記錄條數，最後每段文字；開頭和使用者的話不縮
    const n = { log: 60, ledger: 40, text: 4000 }
    const size = c => new TextEncoder().encode(JSON.stringify(c)).length
    let card = make(n)
    for (const key of ['log', 'ledger', 'text']) {
      while (size(card) > LIMIT && n[key] > 5) { n[key] = Math.floor(n[key] * 0.7); card = make(n) }
    }
    while (size(card) > LIMIT && n.text > 50) { n.text = Math.floor(n.text * 0.7); card = make(n) }
    return card
  }

  const api = { KINDS, STILL, followOptions, sectionsFor, sliceDiag, filterLog, formatLedger, buildCard }
  root.FeedbackPlan = api
  if (typeof module !== 'undefined') module.exports = api
})(typeof window !== 'undefined' ? window : globalThis)
