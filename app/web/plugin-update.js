// 外掛更新提醒：外殼跑接入工具的只讀檢查（integrate.py plugins），把各外掛的三個版本交過來——
// CrossPet 自帶的 / 裝到對方那邊的 / 對方正在跑的。這裡決定要不要提醒、說什麼。兩個平臺共用這一份。
// 只提醒、不自動更新：改別家配置一律讓使用者自己點「更新」（1.3.0 自動重新整理 DeepSeek Harness 接入時刪過使用者的服務商配置）
;(function (root) {
  const parse = v => String(v).split(/[.-]/).map(n => parseInt(n, 10) || 0)
  function older(a, b) {   // a 比 b 舊
    const x = parse(a), y = parse(b)
    for (let i = 0; i < Math.max(x.length, y.length); i++) if ((x[i] || 0) !== (y[i] || 0)) return (x[i] || 0) < (y[i] || 0)
    return false
  }

  // 返回 null（不用提醒）或 { kind: 'update' | 'restart', text（設定頁那一行寫的）, say（桌寵說的） }
  function advice(p) {
    if (!p || !p.bundled) return null
    if (!p.installed || older(p.installed, p.bundled)) return {
      kind: 'update',
      text: `外掛有新版本（${p.installed ? p.installed + ' → ' : ''}${p.bundled}）：點右邊的「更新」，再完全退出 ${p.label} 重新開啟`,
      say: `${p.label} 外掛有新版本啦，去「設定…→接入 AI」點一下更新吧～`,
    }
    if (p.running && older(p.running, p.installed)) return {
      kind: 'restart',
      text: `外掛已經更新到 ${p.installed}，但 ${p.label} 還在跑舊的 ${p.running}：完全退出它（Windows 包括右下角托盤圖示）再開啟才生效`,
      say: `${p.label} 的外掛更新好了，完全退出再開啟一下才生效哦`,
    }
    return null
  }

  root.PluginUpdate = { advice }
  if (typeof module !== 'undefined') module.exports = { advice }
})(typeof window !== 'undefined' ? window : globalThis)
