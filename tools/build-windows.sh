#!/bin/sh
# 打 Windows 包（在 macOS 上就能打，不需要 Windows）：
#   tools/build-windows.sh            → build/CrossPet-Windows.zip
# 需要 .NET 8 SDK（裝法：https://dot.net/v1/dotnet-install.sh --channel 8.0），預設找 ~/.dotnet/dotnet；也可以用 DOTNET=... 指定。
# 一個包同時支援 x64 和 ARM64：程式是 AnyCPU，WebView2 載入器三種架構都帶；鉤子用的 Python 是 x64 版，ARM64 上走系統的 x64 相容層。
set -e
cd "$(dirname "$0")/.."
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
OUT=build/windows/CrossPet
PY_VERSION=3.13.13
PY_SHA256=8766a8775746235e23cf5aee5027ab1060bb981d93110577adcf3508aa0cbd55
PY_ZIP="build/downloads/python-$PY_VERSION-embed-amd64.zip"

"$DOTNET" build app/windows/CrossPet.csproj -c Release -nologo -v quiet -p:Version="$(cut -d- -f1 VERSION)" -p:InformationalVersion="$(cat VERSION)"
BIN=app/windows/bin/Release/net48

rm -rf "$OUT"
mkdir -p "$OUT/tools"
cp "$BIN"/CrossPet.exe "$BIN"/CrossPet.exe.config "$BIN"/Microsoft.Web.WebView2.*.dll "$OUT/"
cp -R "$BIN/runtimes" "$OUT/runtimes"
cp -R app/web "$OUT/web"
# maid fork：只帶 maid/ship-characters.txt 列出的角色
mkdir -p "$OUT/characters" && cp characters/*.md "$OUT/characters/"
grep -vE '^\s*(#|$)' maid/ship-characters.txt | while read -r c; do rsync -a --exclude raw --exclude .DS_Store "characters/$c" "$OUT/characters/"; done
rsync -a --exclude .DS_Store integrations/ "$OUT/integrations/"
cp tools/integrate.py "$OUT/tools/"
cp VERSION LICENSE app/windows/apps.json app/windows/install.cmd app/windows/install.ps1 app/windows/uninstall.cmd app/windows/uninstall.ps1 "$OUT/"
cp app/icon/AppIcon.ico "$OUT/"
# Windows 自帶的 PowerShell 5.1 讀不帶 BOM 的指令碼時按系統編碼（中文系統是 GBK）解析，中文會亂碼：轉成帶 BOM 的 UTF-8
for f in "$OUT"/*.ps1; do python3 -c "import sys; p=sys.argv[1]; t=open(p,encoding='utf-8-sig').read(); open(p,'w',encoding='utf-8-sig',newline='\r\n').write(t)" "$f"; done
cp docs/windows.md "$OUT/使用說明.md"

# 隨包 Python：只給鉤子指令碼和接入工具用，官方精簡版，核對校驗值
mkdir -p build/downloads
[ -f "$PY_ZIP" ] || curl -sSLf "https://www.python.org/ftp/python/$PY_VERSION/python-$PY_VERSION-embed-amd64.zip" -o "$PY_ZIP"
echo "$PY_SHA256  $PY_ZIP" | shasum -a 256 -c - >/dev/null || { echo "Python 安裝包校驗失敗：$PY_ZIP"; exit 1; }
mkdir -p "$OUT/python"
unzip -q -o "$PY_ZIP" -d "$OUT/python"

rm -f build/CrossPet-Windows.zip
python3 - <<'EOF'
import os, zipfile
src = 'build/windows/CrossPet'
with zipfile.ZipFile('build/CrossPet-Windows.zip', 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
    for root, _, files in os.walk(src):
        for f in files:
            if f == '.DS_Store':
                continue
            p = os.path.join(root, f)
            z.write(p, os.path.join('CrossPet', os.path.relpath(p, src)))
EOF
echo "Windows 包：build/CrossPet-Windows.zip（$(du -h build/CrossPet-Windows.zip | cut -f1)）"
