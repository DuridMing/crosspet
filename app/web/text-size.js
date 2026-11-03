// 文字大小和人物大小分開調（設計：docs/superpowers/specs/2026-10-13-text-size-design.md）。兩個平臺共用這一份，規則只寫一遍：
// - 鎖著（預設）時文字 = 人物，和以前一樣；設定裡單獨拖了文字滑塊就解鎖，各調各的
// - k = 文字 ÷ 人物：氣泡、名牌、額度按 k 放大；字比人大時畫布（網頁的 CSS 尺寸）跟著變大給字騰地方，外殼照著設視窗
// - 畫布只由這兩個設定值決定，不看當前有沒有氣泡：視窗只在調設定時變，平時不動
;(function (root) {
  const BASE = { w: 260, h: 362, foot: 62 }   // 原來固定的畫布：人物上方 59 給氣泡，下方 62 給名牌和額度
  const clamp = v => { const n = Number(v); return Number.isFinite(n) ? Math.min(1.6, Math.max(0.6, n)) : 1 }

  function effectiveText(size, textSize, linked) {
    return linked === false ? clamp(textSize === undefined ? 1 : textSize) : clamp(size)
  }

  function layout(size, text) {
    const k = clamp(text) / clamp(size)
    if (k <= 1) return { k, ...BASE }   // 字小了本來就放得下；底欄也不縮，人物不往下掉
    return {
      k,
      w: Math.max(BASE.w, Math.round(240 * k + 20)),   // 氣泡最寬 228、額度最寬 236，放大後兩邊再留點
      h: BASE.h + Math.round(121 * (k - 1)),            // 氣泡區 59 + 底欄 62，都按 k 放大
      foot: Math.round(BASE.foot * k),
    }
  }

  root.TextSize = { effectiveText, layout }
  if (typeof module !== 'undefined') module.exports = { effectiveText, layout }
})(typeof window !== 'undefined' ? window : globalThis)
