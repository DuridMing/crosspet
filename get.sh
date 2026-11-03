#!/bin/bash
# CrossPet 一條命令安裝 / 更新 / 解除安裝（不用 git、不用編譯、第一次開啟也不用去「隱私與安全性」裡放行）
#
#   安裝或更新：curl -fsSL https://raw.githubusercontent.com/DuridMing/crosspet/main/get.sh | bash
#   解除安裝：      curl -fsSL https://raw.githubusercontent.com/DuridMing/crosspet/main/get.sh | bash -s -- uninstall
#
# 做的事：從 GitHub Releases 下載最新的 macOS 安裝包（CrossPet-macOS.zip，1.2.1 及以前叫 CrossPet.zip）→ 裝進「應用程式」→ 開啟 → 逐個問你要接入哪些 AI。
# 為什麼不用放行：macOS 只攔帶「從網上下載」標記的檔案，瀏覽器下載的會帶，curl 下載的不會。
# 接入 AI 用的是同一版本的原始碼裡的 tools/integrate.py（下載到臨時資料夾，用完就刪），只新增 CrossPet 自己的條目，改動前都會備份。
#
# 可選環境變數（一般用不到）：
#   CROSSPET_CONNECT="codex,claude-mod"   不逐個問，直接接入這些（all = 全部，none = 都不接）
#   CROSSPET_APP_DIR=/path                 裝到別的資料夾（這時不會關掉正在執行的 CrossPet，也不動別處的舊版）
#   CROSSPET_NO_OPEN=1                     裝完不自動開啟
#   CROSSPET_TAG=v1.4.0-beta.2             裝指定版本（測試版使用者在 App 裡一鍵更新到下一個測試版時用）；不設就是最新正式版
set -euo pipefail

REPO_SLUG="DuridMing/crosspet"   # maid fork
ACTION="${1:-install}"
TARGETS="claude-mod claude-hooks codex deepseek antigravity gemini workbuddy zcode hermes qwen"

bold() { printf "\n\033[1m%s\033[0m\n" "$1"; }
die() { printf "\n\033[31m%s\033[0m\n" "$1" >&2; exit 1; }

# 通過 curl | bash 執行時，標準輸入是指令碼本身，提問要從終端讀
ask() {
  local a=""
  if [ -r /dev/tty ]; then printf "%s [y/N] " "$1" > /dev/tty; read -r a < /dev/tty || a=""; fi
  [ "$a" = "y" ] || [ "$a" = "Y" ]
}

[ "$(uname)" = "Darwin" ] || die "CrossPet 只支援 macOS。"
major=$(sw_vers -productVersion | cut -d. -f1)
[ "$major" -ge 13 ] || die "CrossPet 需要 macOS 13 或以上（你的是 $(sw_vers -productVersion)）。"

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

# 最新版本號：releases/latest 會跳轉到 .../releases/tag/vX.Y.Z
latest_tag() {
  curl -fsSLI -o /dev/null -w '%{url_effective}' "https://github.com/$REPO_SLUG/releases/latest" | sed 's#.*/tag/##'
}

# 下載同一版本的原始碼，裡面有接入工具
fetch_source() {
  local tag="$1"
  curl -fsSL "https://github.com/$REPO_SLUG/archive/refs/tags/$tag.tar.gz" | tar -xz -C "$TMP"
  SRC=$(find "$TMP" -maxdepth 1 -type d -name 'crosspet-*' | head -1)
  [ -n "$SRC" ] || die "下載原始碼失敗。"
}

have_python() { command -v python3 >/dev/null 2>&1 && python3 -c 'import sys' >/dev/null 2>&1; }

if [ "$ACTION" = "uninstall" ]; then
  bold "解除安裝 CrossPet"
  [ -n "${CROSSPET_APP_DIR:-}" ] || pkill -x CrossPet 2>/dev/null || true
  if have_python; then
    fetch_source "$(latest_tag)"
    for t in $TARGETS; do python3 "$SRC/tools/integrate.py" uninstall "$t"; done
  else
    echo "沒有 python3，跳過撤銷 AI 接入（各 AI 的配置裡 CrossPet 的條目需要手動刪）。"
  fi
  if [ -n "${CROSSPET_APP_DIR:-}" ]; then rm -rf "$CROSSPET_APP_DIR/CrossPet.app"
  else rm -rf "/Applications/CrossPet.app" "$HOME/Applications/CrossPet.app"; fi
  if ask "要刪除你的角色和設定嗎（~/Library/Application Support/CrossPet，包括自己加的角色）？"; then
    rm -rf "$HOME/Library/Application Support/CrossPet"
    defaults delete io.github.crosspet 2>/dev/null || true
  fi
  rm -rf /tmp/crosspet
  bold "已解除安裝 CrossPet。"
  exit 0
fi

[ "$ACTION" = "install" ] || die "不認識的引數：${ACTION}（可以用 install 或 uninstall）"

bold "1/3 下載最新版"
TAG=${CROSSPET_TAG:-$(latest_tag)}
[ -n "$TAG" ] || die "查不到最新版本，檢查一下網路。"
echo "最新版本：$TAG"
# 1.2.2 起 macOS 包叫 CrossPet-macOS.zip（和 CrossPet-Windows.zip 區分開）；沒有就是舊版本，用原來的名字
ASSET="https://github.com/$REPO_SLUG/releases/download/$TAG/CrossPet-macOS.zip"
curl -fsIL -o /dev/null "$ASSET" || ASSET="https://github.com/$REPO_SLUG/releases/download/$TAG/CrossPet.zip"
# App 裡點「更新」時會給一個進度檔案，下載進度寫進去，桌寵讀它在氣泡裡顯示百分比
if [ -n "${CROSSPET_PROGRESS_FILE:-}" ]; then
  curl -fL --progress-bar -o "$TMP/CrossPet.zip" "$ASSET" 2>"$CROSSPET_PROGRESS_FILE" || die "下載失敗，檢查一下網路再試。"
else
  curl -fL --progress-bar -o "$TMP/CrossPet.zip" "$ASSET"
fi
ditto -x -k "$TMP/CrossPet.zip" "$TMP/app"
[ -d "$TMP/app/CrossPet.app" ] || die "下載的安裝包不完整，再試一次。"

bold "2/3 安裝"
if [ -n "${CROSSPET_APP_DIR:-}" ]; then DEST="$CROSSPET_APP_DIR"
# 更新時裝回原來的位置：App 裡點「更新」時會告訴我們它裝在哪；只裝在「我的應用程式」裡的也留在那裡
elif [ "${CROSSPET_PREFER_DIR:-}" = "/Applications" ] || [ "${CROSSPET_PREFER_DIR:-}" = "$HOME/Applications" ]; then DEST="$CROSSPET_PREFER_DIR"
elif [ -d "$HOME/Applications/CrossPet.app" ] && [ ! -d /Applications/CrossPet.app ]; then DEST="$HOME/Applications"
elif [ -w /Applications ]; then DEST="/Applications"
else DEST="$HOME/Applications"; fi
mkdir -p "$DEST"
if [ -z "${CROSSPET_APP_DIR:-}" ]; then   # 裝在預設位置：先關掉正在執行的舊版
  # -a：App 裡點「更新」時這個指令碼是 CrossPet 啟動的，pkill 預設會跳過自己的上級程式，那樣舊版關不掉
  pkill -a -x CrossPet 2>/dev/null || true
  sleep 0.5
fi
rm -rf "$DEST/CrossPet.app"
ditto "$TMP/app/CrossPet.app" "$DEST/CrossPet.app"
xattr -dr com.apple.quarantine "$DEST/CrossPet.app" 2>/dev/null || true
# 裝在預設位置時，另一個預設位置留著的舊版刪掉，免得開啟錯（指定了 CROSSPET_APP_DIR 時不動別處）
if [ -z "${CROSSPET_APP_DIR:-}" ]; then
  for other in /Applications "$HOME/Applications"; do
    if [ "$other" != "$DEST" ] && [ -d "$other/CrossPet.app" ]; then rm -rf "$other/CrossPet.app"; echo "已刪除舊位置的 $other/CrossPet.app"; fi
  done
fi
echo "已安裝到 $DEST/CrossPet.app"
[ -n "${CROSSPET_NO_OPEN:-}" ] || open "$DEST/CrossPet.app"

bold "3/3 接入 AI"
if ! have_python; then
  echo "接入 AI 需要 python3。沒有的話先執行 xcode-select --install 裝上蘋果命令列工具，再重新執行這條命令。"
  echo "不接也能用：切到各個 AI 的 App 時照樣會換角色。"
  exit 0
fi
# 1.2.3 起 App 裡自帶接入工具，直接用剛裝好的那份；舊版本的包裡沒有，才下載同一版本的原始碼
export PYTHONDONTWRITEBYTECODE=1   # 別往 App 包裡寫 __pycache__（會破壞簽名）
if [ -f "$DEST/CrossPet.app/Contents/Resources/tools/integrate.py" ]; then
  SRC="$DEST/CrossPet.app/Contents/Resources"
else
  fetch_source "$TAG"
fi
INTEGRATE="$SRC/tools/integrate.py"
already=$(cd "$SRC/tools" && python3 -c 'import integrate; print(" ".join(integrate.installed()))')
if [ -n "$already" ]; then
  echo "已經接入：${already}（按新版重新整理一遍）"
  python3 "$INTEGRATE" refresh >/dev/null
fi
want() {  # 要不要接入 $1（問題是 $2）
  case " $already " in *" $1 "*) return 1 ;; esac
  if [ -n "${CROSSPET_CONNECT:-}" ]; then
    [ "$CROSSPET_CONNECT" = "all" ] && return 0
    case ",$CROSSPET_CONNECT," in *",$1,"*) return 0 ;; *) return 1 ;; esac
  fi
  ask "$2"
}
case " $already " in
  *" claude-mod "*|*" claude-hooks "*) ;;
  *)
    if want claude-mod "接入 Claude Code 增強版 mod（能顯示額度，需要終端裡的 Claude Code 2.1.287 以上）？"; then
      python3 "$INTEGRATE" install claude-mod
    elif want claude-hooks "  或者接入 Claude Code 標準鉤子（所有版本可用，不顯示額度）？"; then
      python3 "$INTEGRATE" install claude-hooks
    fi ;;
esac
want codex "接入 Codex（之後要在 Codex 裡用 /hooks 信任一次）？" && python3 "$INTEGRATE" install codex || true
want deepseek "接入 DeepSeek Harness 桌面版（裝外掛，能顯示餘額）？" && python3 "$INTEGRATE" install deepseek || true
want antigravity "接入 Antigravity（Gemini 角色跟著它幹活）？" && python3 "$INTEGRATE" install antigravity || true
want gemini "接入 Gemini CLI？" && python3 "$INTEGRATE" install gemini || true
want workbuddy "接入 WorkBuddy（騰訊，按當前模型換角色）？" && python3 "$INTEGRATE" install workbuddy || true
want zcode "接入 ZCode（智譜）？" && python3 "$INTEGRATE" install zcode || true
want hermes "接入 Hermes Agent（Nous Research，按當前模型換角色）？" && python3 "$INTEGRATE" install hermes || true
want qwen "接入 Qwen Code（阿里，按當前模型換角色）？" && python3 "$INTEGRATE" install qwen || true

bold "裝好了！桌寵在螢幕右下角。"
echo "· 單擊摸摸頭，拖動換位置，右鍵開啟選單（改名、換角色、開機自啟……）"
echo "· 以後更新：再執行一次同樣的命令"
echo "· 解除安裝：curl -fsSL https://raw.githubusercontent.com/$REPO_SLUG/main/get.sh | bash -s -- uninstall"
