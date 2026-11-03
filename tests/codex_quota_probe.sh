#!/bin/sh
# 用假 codex 測 Mac 版問服務的各條路徑：先 app/build.sh 編譯，再 CROSSPET_CODEX_TIMEOUT=3 tests/codex_quota_probe.sh
set -eu
cd "$(dirname "$0")/.."
BIN=build/CrossPet.app/Contents/MacOS/CrossPet
FAKE="$(pwd)/tests/fake_codex.py"
EMPTY=$(mktemp -d)   # 空的 CODEX_HOME：沒有會話記錄，失敗時應該是 none
fail=0
check() {   # 模式 期望的 source 期望 error 裡包含的字（可空）
  out=$(CODEX_HOME="$EMPTY" CROSSPET_CODEX_BIN="$FAKE" FAKE_CODEX_MODE="$1" "$BIN" --codex-quota-probe)
  python3 - "$1" "$2" "$3" "$out" <<'PY' || fail=1
import json, sys
mode, src, err, out = sys.argv[1:5]
p = json.loads(out)
ok = p["source"] == src and (err in (p.get("error") or "")) and "acct-secret" not in out
print(("PASS" if ok else "FAIL"), mode, out[:160])
sys.exit(0 if ok else 1)
PY
}
check ok live ""
check single live ""
check auth none "authentication"
check nomethod none "method not found"
check garbage none "超時"
start=$(date +%s); check hang none "超時"; [ $(( $(date +%s) - start )) -le 8 ] || { echo "FAIL hang 超過 8 秒（超時沒生效）"; fail=1; }
# 對面一啟動就退出：往它的 stdin 寫東西不能把整個程式帶崩（SIGPIPE），連跑 20 次
crash=0; for i in $(seq 20); do CODEX_HOME="$EMPTY" CROSSPET_CODEX_BIN="$FAKE" FAKE_CODEX_MODE=exit "$BIN" --codex-quota-probe >/dev/null 2>&1 || crash=$((crash+1)); done
[ $crash -eq 0 ] && echo "PASS exit-no-crash" || { echo "FAIL exit 崩了 $crash 次"; fail=1; }
out=$(CODEX_HOME="$EMPTY" CROSSPET_CODEX_BIN=/nonexistent/codex "$BIN" --codex-quota-probe)
echo "$out" | grep -q '"source":"none"' && echo "PASS missing-binary" || { echo "FAIL missing-binary $out"; fail=1; }
# 會話記錄不可靠（Codex 回覆了也不一定寫額度），1.3.3 起不再讀：有記錄也應該是 none
REC=$(mktemp -d); D="$REC/sessions/$(date +%Y/%m/%d)"; mkdir -p "$D"
now=$(date +%s)
printf '{"timestamp":"2026-01-01T00:00:09Z","payload":{"rate_limits":{"primary":{"used_percent":70,"window_minutes":300,"resets_at":%s}}}}\n' $((now+600)) > "$D/rollout-a.jsonl"
printf '{"timestamp":"2026-01-01T00:00:01Z","payload":{"rate_limits":{"primary":{"used_percent":10,"window_minutes":300,"resets_at":%s}}}}\n' $((now+600)) > "$D/rollout-b.jsonl"
touch "$D/rollout-a.jsonl"; sleep 1; touch "$D/rollout-b.jsonl"   # b 檔案更新，但記錄更舊
out=$(CODEX_HOME="$REC" CROSSPET_CODEX_BIN="$FAKE" FAKE_CODEX_MODE=auth "$BIN" --codex-quota-probe)
echo "$out" | grep -q '"source":"none"' && ! echo "$out" | grep -q 'used_percent' && echo "PASS records-ignored" || { echo "FAIL 不該再讀會話記錄 $out"; fail=1; }
rm -rf "$REC"
# 定時（每分鐘一次）到點時要不要問：Codex 在幹活（10 分鐘內有不是 Stop 的事件）1 分鐘問一次，閒著 2 分鐘一次
# 引數：離上次問過了幾秒 離 Codex 最近一次幹活事件幾秒（-1 = 沒有 / 已經 Stop）
# 程式不認識這個引數時會當成正常啟動、一直開著：最多等 5 秒
tick() { got=$(perl -e 'alarm 5; exec @ARGV' "$BIN" --codex-tick-probe "$1" "$2" 2>/dev/null || echo "（沒有輸出）"); [ "$got" = "$3" ] && echo "PASS tick $4" || { echo "FAIL tick $4：期望 $3，得到 $got"; fail=1; }; }
tick 58 30 yes "幹活中、定時器早到 2 秒也算到點"
tick 40 30 no "幹活中、剛問過"
tick 58 -1 no "閒著、才 1 分鐘"
tick 118 -1 yes "閒著、2 分鐘（定時器早到 2 秒）"
tick 70 700 no "上次幹活是 10 分鐘前：算閒著"
rm -rf "$EMPTY"
exit $fail
