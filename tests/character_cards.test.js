// 設定「角色」頁的卡片牆（app/web/character-cards.js）：node --test tests/character_cards.test.js
const test = require('node:test')
const assert = require('node:assert')
const C = require('../app/web/character-cards.js')

const img = id => ({ portrait: `${id}/portrait.webp`, idle: `${id}/idle.webp` })
const chars = [
  { id: 'claude', name: 'Claude', accent: '#e8a33d', images: { idle: 'claude/idle.webp' } },
  { id: 'deepseek', name: 'DeepSeek', accent: '#3b5bb5', images: img('deepseek'), skin: 'deepseek-pajamas',
    skins: [{ id: '', name: '女僕裝', images: img('deepseek') }, { id: 'deepseek-pajamas', name: '睡衣', images: { idle: 'deepseek-pajamas/idle.webp' } }] },
  { id: 'mine', name: '小八', images: {} },
]

test('卡片圖：portrait 優先，沒有用 idle，都沒有是 null', () => {
  assert.equal(C.imageOf({ portrait: 'p', idle: 'i' }), 'p')
  assert.equal(C.imageOf({ idle: 'i' }), 'i')
  assert.equal(C.imageOf({}), null)
  assert.equal(C.imageOf(undefined), null)
})

test('衣服卡：每套一張，正在穿的高亮', () => {
  assert.deepStrictEqual(C.outfitCards(chars, 'deepseek'), [
    { id: '', name: '女僕裝', image: 'deepseek/portrait.webp', preview: 'deepseek/portrait.webp', art: true, on: false },
    { id: 'deepseek-pajamas', name: '睡衣', image: 'deepseek-pajamas/idle.webp', preview: 'deepseek-pajamas/idle.webp', art: false, on: true },
  ])
})

test('原版卡用角色自己資料夾的圖（穿著皮膚時也一樣）', () => {
  assert.equal(C.outfitCards(chars, 'deepseek')[0].image, 'deepseek/portrait.webp')
})

test('沒有別的衣服：只有一張「原版」', () => {
  assert.deepStrictEqual(C.outfitCards(chars, 'claude'), [{ id: '', name: '原版', image: 'claude/idle.webp', preview: 'claude/idle.webp', art: false, on: true }])
})

test('當前角色不在列表裡：沒有衣服卡', () => {
  assert.deepStrictEqual(C.outfitCards(chars, 'nobody'), [])
})

test('角色卡：原版的圖、套數、當前角色高亮，順序不變', () => {
  assert.deepStrictEqual(C.characterCards(chars, 'deepseek'), [
    { id: 'claude', name: 'Claude', image: 'claude/idle.webp', avatar: false, preview: 'claude/idle.webp', art: false, accent: '#e8a33d', count: 1, on: false },
    { id: 'deepseek', name: 'DeepSeek', image: 'deepseek/portrait.webp', avatar: false, preview: 'deepseek/portrait.webp', art: true, accent: '#3b5bb5', count: 2, on: true },
    { id: 'mine', name: '小八', image: null, avatar: false, preview: null, art: false, accent: '#e0b48a', count: 1, on: false },
  ])
})

test('角色卡優先用探頭頭像 avatar，沒有再退回展示圖 / 待機圖；衣服卡不用 avatar', () => {
  const list = [{ id: 'gpt', name: 'GPT', images: { avatar: 'gpt/avatar.webp', portrait: 'gpt/portrait.webp', idle: 'gpt/idle.webp' } }]
  assert.equal(C.characterCards(list, 'gpt')[0].image, 'gpt/avatar.webp')
  assert.equal(C.characterCards(list, 'gpt')[0].avatar, true)
  assert.equal(C.characterCards(chars, 'claude')[0].avatar, false)
  assert.equal(C.outfitCards(list, 'gpt')[0].image, 'gpt/portrait.webp')
})

test('衣服卡：有展示圖（帶背景的整張皮膚圖）時 art 為 true，頁面鋪滿整張卡；退回待機圖時 false，縮排卡里', () => {
  const [maid, pajamas] = C.outfitCards(chars, 'deepseek')
  assert.equal(maid.art, true)
  assert.equal(pajamas.art, false)
})

// 換角色視窗（characters.html）：卡片用縮好的小圖 portrait-card（不縮到五分之一，Windows 上才不糊），
// 大立繪用大圖 portrait；懸停在衣服卡上時大立繪先預覽那一套
const big = id => ({ portrait: `${id}/portrait.webp`, 'portrait-card': `${id}/portrait-card.webp`, idle: `${id}/idle.webp` })
const two = [
  { id: 'grok', name: 'Grok', images: big('grok') },
  { id: 'deepseek', name: 'DeepSeek', images: big('deepseek'), skin: 'deepseek-hoodie',
    skins: [{ id: '', name: '女僕裝', images: big('deepseek') }, { id: 'deepseek-hoodie', name: 'Claude 衛衣', images: big('deepseek-hoodie') }] },
]

test('衣服卡用小圖 portrait-card，沒有小圖時退回 portrait；preview 是大圖', () => {
  const [maid] = C.outfitCards(two, 'deepseek')
  assert.equal(maid.image, 'deepseek/portrait-card.webp')
  assert.equal(maid.preview, 'deepseek/portrait.webp')
  assert.equal(C.outfitCards(chars, 'deepseek')[0].image, 'deepseek/portrait.webp')   // 自定義角色只放了 portrait
})

test('大立繪：當前角色穿著的那套，寫角色名和衣服名', () => {
  assert.deepEqual(C.stage(two, 'deepseek'), { name: 'DeepSeek', outfit: 'Claude 衛衣', image: 'deepseek-hoodie/portrait.webp', art: true })
  assert.deepEqual(C.stage(two, 'grok'), { name: 'Grok', outfit: '', image: 'grok/portrait.webp', art: true })
  // 只有待機圖（透明底）：art 為 false，頁面整身縮排框裡
  assert.deepEqual(C.stage(chars, 'claude'), { name: 'Claude', outfit: '', image: 'claude/idle.webp', art: false })
  assert.equal(C.stage(chars, 'nobody'), null)
})
