#!/bin/sh
# 文字大小：視窗放在哪（Mac 版 App.placeWindow 的純計算），先 app/build.sh 編譯，再 tests/window_place_probe.sh
# 引數：錨點 舊x 舊y 舊寬 舊高 新寬 新高 舊底欄 新底欄 可見區底邊（螢幕點，y 向上）
set -eu
cd "$(dirname "$0")/.."
BIN=build/CrossPet.app/Contents/MacOS/CrossPet
fail=0
check() {   # 名字 期望輸出 引數…
  name=$1; want=$2; shift 2
  got=$(perl -e 'alarm 5; exec @ARGV' "$BIN" --window-place-probe "$@" 2>/dev/null || echo "（沒有輸出）")
  if [ "$got" = "$want" ]; then echo "PASS $name"; else echo "FAIL $name：期望 $want，得到 $got"; fail=1; fi
}
# 啟動 / 網頁過載：存下的位置本來就是這個畫布的視窗，左下角不動，只換大小（審查第 1 條：每次啟動往左下漂）
check corner-startup "100 200 252 265.8" corner 100 200 156 217.2 252 265.8 37.2 61.8 0
# 改人物大小：底邊中點不動，和以前一樣；底欄的網頁高度變了也不挪（審查第 2 條：螢幕上的底欄沒變）
check bottom-size "152 200 156 217.2" bottom 100 200 260 362 156 217.2 62 37.2 0
check bottom-unlocked "148 200 252 265.8" bottom 100 200 348 341.4 252 265.8 61.8 61.8 0
# 改文字大小：底欄在螢幕上變高多少，視窗底邊就往下挪多少，人物不動；水平按中心
check feet-text "52 175.4 252 265.8" feet 100 200 156 217.2 252 265.8 37.2 61.8 0
# 往下擴出了可見區：挪回可見區底邊
check feet-clamp "52 20 252 265.8" feet 100 30 156 217.2 252 265.8 37.2 61.8 20
# 本來就放在可見區下面：不往上挪到比原來還高
check feet-already-below "52 10 252 265.8" feet 100 10 156 217.2 252 265.8 37.2 61.8 20
exit $fail
