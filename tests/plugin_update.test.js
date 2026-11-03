// 外掛更新提醒（網頁層規則 app/web/plugin-update.js）：node --test tests/plugin_update.test.js
const test = require('node:test')
const assert = require('node:assert')
const { advice } = require('../app/web/plugin-update.js')

const p = (installed, running, bundled = '1.3.5') => ({ id: 'deepseek', label: 'DeepSeek Harness', bundled, installed, running })

test('裝的比自帶的舊：提醒去設定裡點「更新」', () => {
  const a = advice(p('1.3.4', '1.3.4'))
  assert.equal(a.kind, 'update')
  assert.match(a.text, /1\.3\.4 → 1\.3\.5/)
  assert.match(a.say, /接入 AI/)   // 桌寵說的話告訴使用者去哪；設定頁那一行就在「接入 AI」裡，寫「點右邊的更新」
})

test('裝的舊、跑的更舊：先提醒更新（更新完再提醒重開）', () => {
  assert.equal(advice(p('1.3.4', '1.3.2')).kind, 'update')
})

test('裝好了新的、Harness 還跑著舊的：提醒完全退出再開啟', () => {
  const a = advice(p('1.3.5', '1.3.2'))
  assert.equal(a.kind, 'restart')
  assert.match(a.text, /完全退出/)
})

test('都是新的：不提醒', () => {
  assert.equal(advice(p('1.3.5', '1.3.5')), null)
})

test('Harness 還沒跑過外掛（沒有自檢）：只看裝的', () => {
  assert.equal(advice(p('1.3.5', null)), null)
  assert.equal(advice(p('1.3.4', null)).kind, 'update')
})

test('接入了但外掛檔案不在：也提醒更新', () => {
  assert.equal(advice(p(null, null)).kind, 'update')
})

test('版本號按數字比，不按字串（1.3.10 比 1.3.9 新）', () => {
  assert.equal(advice(p('1.3.10', '1.3.10', '1.3.9')), null)
  assert.equal(advice(p('1.3.9', '1.3.9', '1.3.10')).kind, 'update')
})

test('自帶的版本讀不到：不提醒（免得亂報）', () => {
  assert.equal(advice(p('1.3.4', '1.3.4', null)), null)
})

test('隨包的外掛比 1.3.8-beta.1 裡那版（1.3.8，沒有事件記錄）新：裝過 beta.1 的人會被提醒更新', () => {
  const src = require('fs').readFileSync(require('path').join(__dirname, '../integrations/deepseek/dsh-plugin-crosspet/lib/index.js'), 'utf8')
  const bundled = src.match(/PLUGIN_VERSION = "([^"]+)"/)[1]
  assert.equal(advice(p('1.3.8', '1.3.8', bundled)).kind, 'update')
})

// DeepSeek Harness 裝 / 更新任何外掛時會校驗所有外掛 package.json 的版本號：1.3.8-beta.2 帶的 "1.3.8.1"（四段）
// 不是合法版本號，會把使用者裝別的外掛也攔住。外掛版本必須是三段的合法版本號，並且和隨包的 package.json 一致
test('外掛版本號是合法的三段版本號，和隨包 package.json 一致', () => {
  const fs = require('fs'), path = require('path')
  const dir = path.join(__dirname, '../integrations/deepseek/dsh-plugin-crosspet')
  const bundled = fs.readFileSync(path.join(dir, 'lib/index.js'), 'utf8').match(/PLUGIN_VERSION = "([^"]+)"/)[1]
  assert.match(bundled, /^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$/)
  assert.equal(JSON.parse(fs.readFileSync(path.join(dir, 'package.json'), 'utf8')).version, bundled)
})

test('裝過 beta.2（外掛 1.3.8.1）的人也會被提醒更新，好把錯的版本號換掉', () => {
  const src = require('fs').readFileSync(require('path').join(__dirname, '../integrations/deepseek/dsh-plugin-crosspet/lib/index.js'), 'utf8')
  const bundled = src.match(/PLUGIN_VERSION = "([^"]+)"/)[1]
  assert.equal(advice(p('1.3.8.1', '1.3.8.1', bundled)).kind, 'update')
})
