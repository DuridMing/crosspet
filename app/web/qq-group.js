// QQ 交流群：群號和「點一下複製」的按鈕。反饋視窗（feedback.html）、設定（settings.html 的「通用」和「新功能」頁）共用，改群號只改這裡。
// 複製先用 navigator.clipboard，不行（macOS 的 WKWebView 有時不給）就退回 execCommand('copy')
;(function (root) {
  const NUMBER = '1106613980'

  async function copy(text) {
    try { await navigator.clipboard.writeText(text); return true } catch (e) {}
    try {
      const t = document.createElement('textarea')
      t.value = text; t.setAttribute('readonly', ''); t.style.position = 'fixed'; t.style.opacity = '0'
      document.body.appendChild(t); t.select()
      const ok = document.execCommand('copy')
      t.remove()
      return ok
    } catch (e) { return false }
  }

  // 一個按鈕：「💬 QQ 群 1106613980 · 點選複製」，點了變成「已複製」兩秒
  function button(className) {
    const b = document.createElement('button')
    b.type = 'button'
    b.className = className || 'qq-copy'
    const idle = () => { b.replaceChildren('💬 QQ 群 ', Object.assign(document.createElement('b'), { textContent: NUMBER }), Object.assign(document.createElement('span'), { textContent: ' · 點選複製' })) }
    idle()
    b.title = '複製群號，到 QQ 裡搜尋加群'
    let timer
    b.onclick = async () => {
      const ok = await copy(NUMBER)
      b.replaceChildren(ok ? '✓ 已複製群號，到 QQ 裡搜尋加群' : '複製失敗，請手動輸入 ' + NUMBER)
      b.classList.toggle('copied', ok)
      clearTimeout(timer)
      timer = setTimeout(() => { b.classList.remove('copied'); idle() }, 2500)
    }
    return b
  }

  root.QQGroup = { NUMBER, copy, button }
})(typeof window !== 'undefined' ? window : globalThis)
