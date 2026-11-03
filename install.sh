#!/bin/sh
# CrossPet 一鍵安裝：編譯 → 裝進「應用程式」→ 逐個詢問要接入哪些 AI
set -e
cd "$(dirname "$0")"

say() { printf "\n\033[1m%s\033[0m\n" "$1"; }
ask() { printf "%s [y/N] " "$1"; read -r a; [ "$a" = "y" ] || [ "$a" = "Y" ]; }

say "1/3 檢查編譯環境"
if ! xcode-select -p >/dev/null 2>&1 || ! command -v swiftc >/dev/null 2>&1; then
  echo "需要蘋果的命令列工具。請執行下面這條命令，裝好後再執行本指令碼："
  echo "  xcode-select --install"
  exit 1
fi
echo "OK"

say "2/3 編譯並安裝 CrossPet.app"
app/build.sh
mkdir -p "$HOME/Applications"
pkill -x CrossPet 2>/dev/null || true
rm -rf "$HOME/Applications/CrossPet.app"
cp -R build/CrossPet.app "$HOME/Applications/CrossPet.app"
echo "已安裝到 ~/Applications/CrossPet.app"

say "3/3 接入 AI（可以以後再執行本指令碼補接，或用 python3 tools/integrate.py）"
if ask "接入 Claude Code（標準鉤子，所有版本可用）？"; then
  python3 tools/integrate.py install claude-hooks
elif ask "  或者接入 Claude Code 增強版 mod（多顯示額度，需要 Claude Code 2.1.28x+）？"; then
  python3 tools/integrate.py install claude-mod
fi
ask "接入 Codex（寫入 ~/.codex/hooks.json）？" && python3 tools/integrate.py install codex
ask "接入 DeepSeek Harness 桌面版（安裝外掛，顯示餘額）？" && python3 tools/integrate.py install deepseek
ask "接入 Antigravity（寫入 ~/.gemini/config/hooks.json，Gemini 角色跟著 Antigravity 幹活）？" && python3 tools/integrate.py install antigravity
ask "接入 Gemini CLI（寫入 ~/.gemini/settings.json；Gemini 桌面 App 無法接入）？" && python3 tools/integrate.py install gemini
ask "接入 WorkBuddy（騰訊，寫入 ~/.workbuddy-ai 或 ~/.workbuddy 的 settings.json，按當前模型換角色）？" && python3 tools/integrate.py install workbuddy
ask "接入 ZCode（智譜，寫入 ~/.zcode/cli/config.json）？" && python3 tools/integrate.py install zcode
ask "接入 Hermes Agent（Nous Research，裝外掛到 ~/.hermes/plugins/crosspet，按當前模型換角色）？" && python3 tools/integrate.py install hermes
ask "接入 Qwen Code（阿里，寫入 ~/.qwen/settings.json，按當前模型換角色）？" && python3 tools/integrate.py install qwen
ask "接入 Grok Build（xAI，寫入 ~/.grok/hooks/crosspet.json）？" && python3 tools/integrate.py install grok
ask "接入 Cursor（寫入 ~/.cursor/hooks.json，按當前模型換角色）？" && python3 tools/integrate.py install cursor

open "$HOME/Applications/CrossPet.app"
say "裝好了！桌寵已經出現在螢幕右下角。"
echo "· 單擊摸摸頭，拖動換位置，右鍵開啟選單（改名、換角色、開機自啟……）"
echo "· 接入狀態隨時可查：python3 tools/integrate.py status"
echo "· 解除安裝：./uninstall.sh"
