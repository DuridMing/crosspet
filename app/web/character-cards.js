// 換角色視窗（characters.html）：左上是當前這套衣服的大立繪，下面一排是當前角色的衣服（用帶背景的展示圖），再下面一排是所有角色（用探頭頭像 avatar）。
// 這裡只決定有哪些卡、用哪張圖、哪張高亮；畫和發訊息在 characters.html。設計見 docs/superpowers/specs/2026-10-20-character-picker-design.md
// 展示圖兩種尺寸：portrait（大立繪用）、portrait-card（卡片用，按卡片大小縮好，Windows 上縮得太狠會糊）；自定義角色只放 portrait 也行
// 輸入是外殼交給設定頁的 characters：[{ id, name, accent?, images: { portrait?, avatar?, idle? }, skins?: [{ id, name, images }], skin? }]，
// skins[0] 是原版（id 為空），它的 images 是角色自己資料夾裡的圖——穿著皮膚時也不會變成皮膚的圖
;(function (root) {
  const DEFAULT_ACCENT = '#e0b48a'

  // 卡片圖：專門畫的展示立繪優先（卡片尺寸的小圖優先），沒有就用待機圖；都沒有（自己加的角色只有 default 圖）返回 null，由頁面畫色塊
  const imageOf = images => images?.['portrait-card'] || images?.portrait || images?.idle || null
  // 大立繪：展示圖的大圖，沒有就用待機圖
  const previewOf = images => images?.portrait || images?.idle || null

  // 衣服卡：展示圖 portrait 是帶場景背景的整張皮膚圖（art: true，頁面鋪滿整張卡）；沒有就退回透明底的待機圖，縮排卡里
  function outfitCards(chars, currentId) {
    const c = chars.find(x => x.id === currentId)
    if (!c) return []
    const card = (id, name, images, on) => ({ id, name, image: imageOf(images), preview: previewOf(images), art: !!images?.portrait, on })
    if (!c.skins?.length) return [card('', '原版', c.images, true)]
    const worn = c.skin || ''
    return c.skins.map(k => card(k.id, k.name, k.images, k.id === worn))
  }

  // 角色卡優先用專門畫的探頭頭像 avatar（從卡片左下角探出腦袋，鋪滿整張卡），沒有再用展示圖 / 待機圖（整身縮排卡里）
  function characterCards(chars, currentId) {
    // preview / art：滑鼠停在角色卡上時，大立繪預覽她的原版
    return chars.map(c => ({ id: c.id, name: c.name, image: c.images?.avatar || imageOf(c.images), avatar: !!c.images?.avatar,
      preview: previewOf(c.images), art: !!c.images?.portrait,
      accent: c.accent || DEFAULT_ACCENT, count: c.skins?.length || 1, on: c.id === currentId }))
  }

  // 大立繪：當前角色穿著的那套（原版不寫衣服名）
  function stage(chars, currentId) {
    const c = chars.find(x => x.id === currentId)
    if (!c) return null
    const worn = c.skins?.find(k => k.id === (c.skin || '')) || null
    const images = worn ? worn.images : c.images
    return { name: c.name, outfit: worn && worn.id ? worn.name : '', image: previewOf(images), art: !!images?.portrait }
  }

  const api = { imageOf, previewOf, outfitCards, characterCards, stage }
  root.CharacterCards = api
  if (typeof module !== 'undefined') module.exports = api
})(typeof window !== 'undefined' ? window : globalThis)
