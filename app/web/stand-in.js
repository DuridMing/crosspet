// 自定義角色響應其他 AI 的事件：character.json 裡寫 "reactTo": ["claude", "gpt"]（或 "*" 代表所有 AI），
// 這個角色在場時，這些 AI 的工作狀態、多會話、慶祝（reset / 吃飯）也由她來演，不用等換成那個 AI 自己的角色。
// 內建角色不受影響，照常響應自己的 AI。
//
// 程式碼裡叫“代演 / standIn / standsIn”：是 current 通道（沒有對應角色的模型由當前角色來演）的通用化。
// 對使用者只說“響應 / 來演”，不說“代演”，免得被理解成別的角色被頂掉了。
// 只在她是當前角色時生效，不會自己切出去——想讓她常駐，要關掉「跟隨前臺應用」（Windows 還有「跟隨 AI 工作事件」）。
// 純函式，網頁和測試（tests/stand_in.test.js）共用。
;(function (root) {
  // reactTo 可以寫 "*"、["claude", "gpt"]，也容忍只寫一個 "claude"；別的寫法當沒寫
  function listOf(spec) {
    if (spec === '*') return '*'
    if (Array.isArray(spec)) return spec.filter(x => typeof x === 'string')
    return typeof spec === 'string' && spec ? [spec] : null
  }
  // spec：她的 reactTo 欄位；own：她自己的角色 id；host：事件來自哪個 AI（角色 id）
  function covers(spec, own, host) {
    const list = listOf(spec)
    if (!list || typeof host !== 'string' || host === own) return false
    return list === '*' || list.includes(host)
  }
  // 額度小窗裡該顯示哪些 AI 的額度：她自己加上她響應的。沒寫或不認識就原樣返回全部
  function quotaIds(spec, own, all) {
    const list = listOf(spec)
    if (!list || list === '*') return all
    return all.filter(id => id === own || list.includes(id))
  }
  root.StandIn = { covers, quotaIds }
  if (typeof module !== 'undefined') module.exports = { covers, quotaIds }
})(typeof window !== 'undefined' ? window : globalThis)
