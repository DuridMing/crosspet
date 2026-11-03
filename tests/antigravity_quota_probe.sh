#!/bin/sh
# Gemini（Antigravity）額度排版：先 app/build.sh 編譯，再 tests/antigravity_quota_probe.sh
# 樣本在 tests/fixtures/antigravity/（只留模型名和額度欄位）
set -eu
cd "$(dirname "$0")/.."
BIN=build/CrossPet.app/Contents/MacOS/CrossPet
F=tests/fixtures/antigravity
fail=0
check() {   # 名字 樣本 期望（Python 表示式，q 是結果）
  out=$(perl -e 'alarm 5; exec @ARGV' "$BIN" --agy-quota-probe "$F/$2.json" 2>/dev/null || echo '"（沒有輸出）"')
  python3 - "$1" "$3" "$out" <<'PY' || fail=1
import json, sys
name, expect, out = sys.argv[1:4]
try: q = json.loads(out)
except Exception: q = None
ok = False
try: ok = bool(eval(expect, {"q": q}))
except Exception: pass
print(("PASS" if ok else "FAIL"), name, "" if ok else out)
sys.exit(0 if ok else 1)
PY
}
# 2026-10-14 實測：非 Ultra 賬號裡「Gemini 4 Argon」標著不可用、額度資訊是空的，以前被當成「剩 0%」排在第一行
# 固定兩行，和 Antigravity 自己「View Usage」的分組一樣：Gemini 一行、Claude/GPT 一行，數字相同也不合並
check "不可用的模型不算進額度" ultra-only-model 'q["text"].split(" ｜ ")[0].startswith("Gemini 剩餘 100%") and q["text"].split(" ｜ ")[1] == "Claude/GPT 剩餘 100%" and q["low"] is False'
check "真用完了還是 0%" gemini-used-up 'q["text"].startswith("Gemini 剩餘 0%") and "重置" in q["text"].split(" ｜ ")[0] and q["text"].split(" ｜ ")[1] == "Claude/GPT 剩餘 100%" and q["low"] is True'
check "兩個池 + 不可用模型" two-pools-with-unavailable 'q["text"].startswith("Gemini 剩餘 52%") and q["text"].split(" ｜ ")[1] == "Claude/GPT 剩餘 99%" and " 0%" not in q["text"] and q["low"] is False'
# 主要來源：額度彙總（RetrieveUserQuotaSummary，和 Antigravity 的「View Usage」同一份資料，剛對話完就更新）
check "彙總：兩組各一行，98% 是新資料" summary-live 'q["text"].split(" ｜ ")[0].startswith("Gemini 剩餘 100%") and "重置" in q["text"].split(" ｜ ")[0] and q["text"].split(" ｜ ")[1].startswith("Claude/GPT 剩餘 98% · ") and q["text"].split(" ｜ ")[1].endswith(" 重置") and q["low"] is False'
check "彙總：用完時比例被省略，算 0%" summary-gemini-used-up 'q["text"].startswith("Gemini 剩餘 0%") and q["low"] is True'
check "彙總：一組有兩個視窗，只留剩餘最少的那個" summary-two-windows 'q["text"].split(" ｜ ")[0].startswith("Gemini 今日 剩餘 40%") and q["text"].split(" ｜ ")[1].startswith("Claude/GPT 剩餘 98% · ") and "本週" not in q["text"]'
check "彙總：獨立額度池（Gemini 4 Argon）保留名字，每組一行" summary-multi-pool 'q["text"].split(" ｜ ")[0].startswith("Gemini 本週 剩餘 99%") and q["text"].split(" ｜ ")[1].startswith("Gemini 4 Argon 剩餘 46% · ") and q["text"].split(" ｜ ")[2].startswith("Claude/GPT") and len(q["text"].split(" ｜ ")) == 3 and "Five Hour" not in q["text"]'
check "彙總：5h 最緊時顯示成「5小時」" summary-5h-tightest 'q["text"].split(" ｜ ")[0].startswith("Gemini 5小時 剩餘 30%") and q["text"].split(" ｜ ")[1].startswith("Claude/GPT 剩餘 100% · ") and "本週" not in q["text"]'
check "彙總：兩個視窗一樣緊（50%），不管介面順序都顯示 5小時 和它的重置時間" summary-tie-5h-first 'q["text"].split(" ｜ ")[0].startswith("Gemini 5小時 剩餘 50% · ") and q["text"].split(" ｜ ")[0].endswith(" 重置")'
check "彙總：同上，週額度排在前面結果一樣" summary-tie-weekly-first 'q["text"].split(" ｜ ")[0].startswith("Gemini 5小時 剩餘 50% · ")'
check "彙總：5h 滿了顯示週額度和重置時間" summary-multi-pool 'q["text"].split(" ｜ ")[0].startswith("Gemini 本週 剩餘 99% · ") and q["text"].split(" ｜ ")[2].startswith("Claude/GPT 本週 剩餘 100% · ")'
check "彙總：週額度 ≤10% 時不被 5h 蓋住" summary-weekly-low 'q["text"].split(" ｜ ")[0].startswith("Gemini 本週 剩餘 5% · ") and q["low"] is True'
check "彙總：每一行都帶重置時間" summary-all-50 'all(seg.endswith(" 重置") for seg in q["text"].split(" ｜ ")) and len(q["text"].split(" ｜ ")) == 3'
# 合併顯示用的結構化額度條 bars：每個視窗一項 {label, left, reset?, group}，同一個池的視窗挨著排（短視窗在前）；排版（Gemini + Arg 一行、Agy C/G 另起一行）在網頁層
check "bars：每個視窗一項，label = 池名 + 視窗名，同一個池挨著排" summary-multi-pool '[b["label"] for b in q["bars"]] == ["Gemini 5小時", "Gemini 本週", "Argon 本週", "Claude/GPT 5小時", "Claude/GPT 本週"] and [b["group"] for b in q["bars"]] == ["Gemini", "Gemini", "Argon", "Claude/GPT", "Claude/GPT"] and [b["left"] for b in q["bars"]] == [1, 0.99, 0.46, 1, 1] and all(isinstance(b["reset"], int) and b["reset"] > 1.7e9 for b in q["bars"])'
check "bars：視窗名按介面的視窗 id（今日 / 本週），沒有 Argon 時只有兩個池" summary-two-windows '[b["label"] for b in q["bars"]] == ["Gemini 今日", "Gemini 本週", "Claude/GPT 本週"]'
check "bars：全部用完（比例被省略）= left 0，每項都帶 reset" summary-all-used-up 'len(q["bars"]) == 5 and all(b["left"] == 0 and "reset" in b for b in q["bars"])'
check "bars：沒有重置時間就不寫 reset 欄位" summary-no-reset 'q["bars"] == [{"label": "Gemini 本週", "left": 0.7, "group": "Gemini"}]'
check "彙總是空的：不顯示（程式會退回查使用者狀態）" summary-empty 'q == {}'
exit $fail
