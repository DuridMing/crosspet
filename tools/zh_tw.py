#!/usr/bin/env python3
"""maid fork：把程式裡的簡體中文轉成台灣繁體（OpenCC s2twp，含台灣慣用詞）。

用法：
    pip install opencc          # 只需要裝一次
    python3 tools/zh_tw.py      # 轉換並寫回檔案
    python3 tools/zh_tw.py --check   # 只檢查，還有沒轉的就回傳 1（給 CI 或合併上游後確認用）

設計成可以重跑：跟上游合併後，先接受上游的簡體版本，再跑一次這支腳本即可，
不必手動處理每一處翻譯衝突。

只轉「會跑的東西」：程式、畫面、出貨角色的台詞、接入腳本、測試（測試要跟著改，斷言才對得上）。
不轉 docs/、README、CHANGELOG、沒出貨的角色，降低和上游的差異。
註解裡的 docs/ 路徑保持原樣，免得指到不存在的檔名。
"""
import argparse
import re
import sys
from pathlib import Path

try:
    import opencc
except ImportError:
    sys.exit("缺少 opencc：pip install opencc")

REPO = Path(__file__).resolve().parent.parent

# 要轉的範圍（相對 repo 根目錄）：資料夾會遞迴
TARGETS = [
    "app/web",
    "app/mac",
    "app/windows/src",
    "app/windows/install.ps1",
    "app/windows/uninstall.ps1",
    "app/build.sh",
    "integrations",
    "tests",
    "tools/integrate.py",
    "tools/build-windows.sh",
    "get.sh",
    "install.sh",
    "uninstall.sh",
    "update.sh",
]
# 出貨角色的台詞（依 maid/ship-characters.txt）
SHIP_LIST = REPO / "maid/ship-characters.txt"

TEXT_EXT = {".js", ".mjs", ".cjs", ".ts", ".html", ".css", ".json", ".py", ".swift", ".cs",
            ".sh", ".ps1", ".cmd", ".yaml", ".yml", ".txt", ".md"}
# 這些片段不轉：docs/ 底下的檔名（文件沒轉，轉了會指到不存在的路徑）
KEEP = re.compile(r"docs/[^\s）)」』\"'`]+")
HAN = re.compile(r"[一-鿿]")

cc = opencc.OpenCC("s2twp")

# OpenCC 靠上下文判斷「周」要不要變「週」，單獨一個字（額度條的「周 99%」「周一」）會漏掉。
# 這裡補上：當「星期」用的周一律改週，周圍、周期這類詞不動。
WEEK = re.compile(r"周(?![圍期邊全到遭轉旋折密長])")
# 「誒」是大陸常用的語氣詞，台灣慣用「欸」
FIXUPS = [(WEEK, "週"), (re.compile("誒"), "欸")]


def convert(text: str) -> str:
    out, pos = [], 0
    for m in KEEP.finditer(text):
        out.append(cc.convert(text[pos:m.start()]))
        out.append(m.group(0))
        pos = m.end()
    out.append(cc.convert(text[pos:]))
    s = "".join(out)
    for pat, rep in FIXUPS:
        s = pat.sub(rep, s)
    return s


def files():
    targets = [REPO / t for t in TARGETS]
    if SHIP_LIST.exists():
        for line in SHIP_LIST.read_text(encoding="utf-8").splitlines():
            line = line.strip()
            if line and not line.startswith("#"):
                targets.append(REPO / "characters" / line / "character.json")
    for t in targets:
        if t.is_file():
            yield t
        elif t.is_dir():
            for p in sorted(t.rglob("*")):
                if p.is_file() and p.suffix.lower() in TEXT_EXT and "node_modules" not in p.parts:
                    yield p


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="只檢查，不寫檔")
    args = ap.parse_args()
    pending = []
    for p in files():
        raw = p.read_bytes()
        bom = raw.startswith(b"\xef\xbb\xbf")
        try:
            text = raw.decode("utf-8-sig")
        except UnicodeDecodeError:
            continue
        if not HAN.search(text):
            continue
        new = convert(text)
        if new == text:
            continue
        pending.append(p.relative_to(REPO))
        if not args.check:
            # 保留原本的 BOM 和換行（PowerShell 腳本靠 BOM 才不會亂碼）
            p.write_bytes((b"\xef\xbb\xbf" if bom else b"") + new.encode("utf-8"))
    verb = "還沒轉" if args.check else "已轉換"
    for p in pending:
        print(f"{verb}：{p}")
    print(f"共 {len(pending)} 個檔案{verb}")
    return 1 if (args.check and pending) else 0


if __name__ == "__main__":
    sys.exit(main())
