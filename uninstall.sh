#!/bin/sh
# CrossPet 解除安裝：撤銷所有 AI 接入（只刪 CrossPet 自己加的條目，改動前都會備份），刪除 App 和執行目錄
cd "$(dirname "$0")"
pkill -x CrossPet 2>/dev/null || true
for t in claude-hooks claude-mod codex deepseek gemini antigravity workbuddy zcode hermes qwen cursor grok; do python3 tools/integrate.py uninstall "$t"; done
rm -rf "$HOME/Applications/CrossPet.app" "/Applications/CrossPet.app"
printf "要刪除你的角色和設定嗎（~/Library/Application Support/CrossPet，包括自己加的角色）？[y/N] "
read -r a
if [ "$a" = "y" ] || [ "$a" = "Y" ]; then
  rm -rf "$HOME/Library/Application Support/CrossPet"
  defaults delete io.github.crosspet 2>/dev/null || true
fi
rm -rf /tmp/crosspet
echo "已解除安裝 CrossPet。"
