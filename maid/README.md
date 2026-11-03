# maid fork 改動說明

這個分支（`maid/claude-zh-tw`）是 AI 女僕計畫用的 CrossPet：只有 Claude、台灣繁體、不會被上游更新蓋掉。

## 改了什麼

| 項目 | 做法 | 位置 |
|---|---|---|
| 更新來源 | 改指向 `DuridMing/crosspet`；預設不自動檢查，只在設定裡手動按「檢查更新」時查 | `app/mac/App.swift`、`app/windows/src/Pet.cs`、`get.sh`、兩平台的 Update 檔 |
| 只有 Claude | 打包時只帶 `maid/ship-characters.txt` 列出的角色；其他角色留在 repo（測試會用到） | `app/build.sh`、`tools/build-windows.sh` |
| 不自動換角色 | `followApps`、`followEvents` 兩平台預設都關 | `App+Settings.swift`、`Store.cs` |
| 台灣繁體 | `tools/zh_tw.py`（OpenCC s2twp）轉程式、畫面、Claude 台詞、接入腳本與測試；docs、README 不轉 | 見腳本開頭的 `TARGETS` |
| 國慶特效 | 改在 10/10 | `app/web/index.html` 的 `festivalOf` |

恢復自動檢查更新：macOS `defaults write io.github.crosspet autoUpdateCheck -bool true`；Windows 在 `settings.json` 加 `"autoUpdateCheck": true`。

## 跟上游合併

繁體化是腳本產生的，合併時不要手動解翻譯衝突：

```bash
pip install opencc
git fetch upstream
git rebase upstream/main          # 遇到「執行 tools/zh_tw.py」那個 commit 的衝突：git rebase --skip
python3 tools/zh_tw.py            # 重新轉換
python3 tools/zh_tw.py --check    # 確認沒有漏轉
git commit -am "maid: 重跑 tools/zh_tw.py"
```

## 已知限制

- 已經裝過原版的電腦，資料目錄裡的其他角色資料夾不會自動刪除（程式只會補、不會刪），要手動刪掉或乾淨重裝。
- 設定頁「接入 AI」的清單仍列出 Codex、DeepSeek 等；沒有對應角色時它們不會出現在畫面上，可以不理。
- 繁體化是機器轉換，台詞語氣（例如「早上好」）可以直接改 `characters/claude/character.json`，下次重跑腳本不會蓋掉已經是繁體的字。
