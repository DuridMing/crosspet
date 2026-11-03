#!/bin/sh
# CrossPet 更新（從原始碼安裝的使用者用）：拉取最新程式碼 → 重新編譯安裝 → 把已經接入的 AI 按新版重新整理一遍
set -e
cd "$(dirname "$0")"
git pull --ff-only
app/build.sh
pkill -x CrossPet 2>/dev/null || true
rm -rf "$HOME/Applications/CrossPet.app"
cp -R build/CrossPet.app "$HOME/Applications/CrossPet.app"
python3 tools/integrate.py refresh
open "$HOME/Applications/CrossPet.app"
echo "已更新到 $(cat VERSION)。"
