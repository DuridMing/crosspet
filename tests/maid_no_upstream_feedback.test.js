// maid fork：不把回報送到上游作者的飛書群。合併上游後這個測試會擋下被加回來的端點與入口。
const test = require('node:test')
const assert = require('node:assert')
const fs = require('node:fs')
const path = require('node:path')

const ROOT = path.join(__dirname, '..')
const read = rel => fs.readFileSync(path.join(ROOT, rel), 'utf8')
const walk = dir => fs.readdirSync(path.join(ROOT, dir), { withFileTypes: true }).flatMap(e =>
  e.isDirectory() ? walk(path.join(dir, e.name)) : [path.join(dir, e.name)])
const SOURCE = /\.(cs|swift|js|mjs|ts|py|html|json|ps1|sh)$/

test('程式裡沒有飛書的端點', () => {
  const found = ['app', 'integrations', 'tools'].flatMap(walk)
    .filter(f => SOURCE.test(f) && !f.includes(`${path.sep}bin${path.sep}`) && !f.includes(`${path.sep}obj${path.sep}`))
    .filter(f => read(f).includes('open.feishu.cn'))
  assert.deepStrictEqual(found, [])
})

test('Windows 選單沒有回報入口', () => {
  assert.ok(!read('app/windows/src/Pet.Menu.cs').includes('OpenFeedback'))
})

test('設定頁沒有回報入口', () => {
  assert.ok(!read('app/web/settings.html').includes('data-action="feedback"'))
})
