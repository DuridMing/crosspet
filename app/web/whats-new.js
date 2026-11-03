// 「新功能」（What's New）：每版的新功能，寫給普通使用者看的短文案——只寫新功能，不寫修了什麼（修復看 CHANGELOG）。
// 更新後桌寵頭頂的卡片（index.html）數條數，設定裡的「新功能」頁（settings.html）列出來。兩個平臺共用，隨包釋出。
// 發版前在最上面加一版（沒有新功能的版本不寫）；go：「去設定 ›」跳到哪個標籤頁、哪個開關（key 是設定頁裡 data-key 的名字）。
// 設計見 docs/superpowers/specs/2026-10-16-whats-new-design.md；測試 tests/whats_new.test.js
;(function (root) {
  const LIST = [
    { version: '1.3.8', items: [
      { icon: '😈', title: '新角色：Grok', text: '戴著小王冠、愛查證愛抬槓的哥特小惡魔女王：說話直、敢懟，閒著會把手機懟到你面前「@grok is this true? 真的」。Grok Build 裡幹活時跟著做動作（在「接入 AI」頁接入），別家程式裡用 Grok 模型時也自動換成她。還藏著一個彩蛋：DeepSeek 偷戴她的王冠……', go: { tab: 'character' } },
      { icon: '🔌', title: '接入 Cursor', text: 'Cursor 裡幹活時她也會跟著動：用 Claude 模型演 Claude、GPT 模型演 GPT……換模型她就換人，Cursor 自家的模型由當前角色來演。在「接入 AI」頁點一下 Cursor 就好，新開的對話生效。', go: { tab: 'ai' } },
      { icon: '🃏', title: '換角色視窗', text: '右鍵「換角色…」開啟一個大視窗：左邊是這套衣服的大立繪，下面是她的每套衣服和所有角色，滑鼠停在卡片上先預覽，點一下就換。改名字、峰谷換裝也在這裡。', go: { tab: 'character' } },
      { icon: '📊', title: '額度條', text: '額度可以換成進度條顯示：每個額度一條，後面是剩餘百分比和重置時間，剩得越少顏色越暖（藍 → 橙 → 紅）。在「額度」頁把「額度顯示樣式」換成「額度條」。', go: { tab: 'quota', key: 'quotaStyle' } },
      { icon: '💬', title: 'QQ 交流群', text: 'CrossPet 有 QQ 群了！用著有問題、想要新角色、想先看到新版本，都可以來群裡聊。點下面的群號就能複製。', qq: true },
    ] },
    { version: '1.3.7', items: [
      { icon: '🪭', title: '新角色：Qwen（千問）', text: '溫柔大方的富家千金：大活幹完請你喝奶茶，額度快用完時掏出金卡，閒著會分小禮物給大家。在 Qwen Code 裡用千問模型時自動換成她，也可以在「角色」頁手動選。千問桌面客戶端沒有對外介面，她跟不了它幹活。', go: { tab: 'character' } },
      { icon: '🔌', title: '接入 Qwen Code', text: '阿里的 Qwen Code 命令列也能接了：她會跟著幹活做動作。在「接入 AI」頁點一下 Qwen Code 就好，新開的會話生效。這項接入還沒在真實的 Qwen Code 裡完整測過（幹活時的事件要登入才能觸發），遇到問題歡迎反饋。', go: { tab: 'ai' } },
      { icon: '⏰', title: '峰谷時段', text: 'DeepSeek、GLM 的額度下面多了一條今天的時間條：紅色高峰、綠色空閒，一眼看出現在貴不貴（嫌看不清可以換成一行文字）。還能在「角色」頁選高峰、空閒各穿哪件衣服，到點她自己換。', go: { tab: 'quota', key: 'peakStyle' } },
      { icon: '🐳', title: 'DeepSeek 新衣服：睡衣、冬裝', text: '兩套新皮膚：雲朵睡衣（更困、更想躺）和鯨魚兜帽冬裝（怕冷、惦記熱乎乎的白飯）。在「角色」頁換上；也可以讓她空閒時段自動換上睡衣。兩套衣服各藏著一個彩蛋：深夜偷穿睡衣的「聖女」Claude，和雪人裡的狐狸。', go: { tab: 'character' } },
      { icon: '😴', title: '新彩蛋', text: '千問在場時偶爾會撞見：DeepSeek 把你交代的活偷偷外包給了千問，自己抱著枕頭睡大覺。點她一下試試？' },
    ] },
    { version: '1.3.6', items: [
      { icon: '🔔', title: 'Codex 重置提醒', text: '官方提前宣佈要重置 Codex 額度時，GPT 的額度下方會多一行；右鍵「會重置嗎？」可以問她，她會按預測的機率回答。預設關著，在「額度」頁開啟。', go: { tab: 'quota', key: 'codexResets' } },
      { icon: '📱', title: 'GPT 的新動作', text: '被問「會重置嗎？」時她會查手機，再搖鈴鐺、期待、懷疑或者搖頭。原版和降智龍崽各一套。' },
      { icon: '✨', title: '「新功能」頁', text: '就是這裡。以後每次更新，新加的功能都會寫在這一頁，更新完她也會提醒你來看。' },
    ] },
    { version: '1.3.5', items: [
      { icon: '🔌', title: '外掛更新提醒', text: 'DeepSeek Harness 的外掛有新版本、或者更新了還沒重開 Harness 時，「接入 AI」頁會寫明怎麼做，她也會提一句。只提醒，不會自己改 Harness 的配置。', go: { tab: 'ai' } },
      { icon: '🎭', title: '自定義角色替別的 AI 演', text: '自己畫的角色可以替 Claude、GPT 這些 AI 演幹活的動作。在角色的 character.json 裡寫 reactTo，用法見倉庫裡的「自定義角色」檔案。' },
    ] },
    { version: '1.3.4', items: [
      { icon: '🐉', title: '降智龍崽皮膚', text: 'GPT 換上龍崽連體服，變成天然呆。穿著它時有專屬彩蛋：DeepSeek 端著白米飯路過，點一下龍崽就聞著飯香爬過來。在「角色」頁給 GPT 換衣服。', go: { tab: 'character' } },
      { icon: '🔤', title: '文字大小單獨調', text: '人調小了，名牌、氣泡、額度的字也能保持看得清：單獨拖「文字大小」滑塊就和人物分開了，點鎖鏈圖示再鎖回去。', go: { tab: 'look' } },
      { icon: '📊', title: '合併顯示各家額度', text: '額度小窗把各家 AI 的額度排成一張表，每家一行，不用來回切角色。', go: { tab: 'quota', key: 'quotaAggregate' } },
    ] },
  ]

  // 去掉開頭的 v 和 - 後面的部分：1.3.6-test.2 按 1.3.6 算
  const base = v => String(v || '').replace(/^v/i, '').split('-')[0]
  const parts = v => base(v).split('.').map(n => parseInt(n, 10) || 0)
  // a 比 b 新（只看正式版本號）
  function newer(a, b) {
    const x = parts(a), y = parts(b)
    for (let i = 0; i < Math.max(x.length, y.length); i++) if ((x[i] || 0) !== (y[i] || 0)) return (x[i] || 0) > (y[i] || 0)
    return false
  }
  // 比 last 新、不比 current 新的版本里的全部條目（從新到舊）；沒有 last（新安裝）就是空的
  function since(list, last, current) {
    if (!last) return []
    return list.filter(v => newer(v.version, last) && !newer(v.version, current)).flatMap(v => v.items)
  }
  // 「新功能」標籤要不要亮紅點
  const unseen = (list, seen, current) => since(list, seen, current).length > 0
  // 設定頁列哪幾版：不比 current 新的前 n 個
  const recent = (list, current, n = 5) => list.filter(v => !newer(v.version, current)).slice(0, n)

  const api = { LIST, base, newer, since, unseen, recent }
  root.WhatsNew = api
  if (typeof module !== 'undefined') module.exports = api
})(typeof window !== 'undefined' ? window : globalThis)
