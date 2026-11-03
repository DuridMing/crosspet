#!/bin/sh
# 編譯 CrossPet.app（Apple 晶片 + Intel 通用版）到 build/CrossPet.app
# 用法：app/build.sh           只編譯
#       app/build.sh --zip     編譯後再打一個釋出用的 build/CrossPet-macOS.zip
set -e
cd "$(dirname "$0")/.."
ROOT=$(pwd)
APP="$ROOT/build/CrossPet.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

VERSION=$(cat VERSION 2>/dev/null || echo 1.0.0)
CORE_VERSION=${VERSION%%-*}   # 蘋果要求的版本號只能是數字：1.2.5-beta.1 → 1.2.5，完整的另存在 CrossPetVersion
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>CrossPet</string>
  <key>CFBundleDisplayName</key><string>CrossPet</string>
  <key>CFBundleIdentifier</key><string>io.github.crosspet</string>
  <key>CFBundleExecutable</key><string>CrossPet</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$CORE_VERSION</string>
  <key>CFBundleVersion</key><string>$CORE_VERSION</string>
  <key>CrossPetVersion</key><string>$VERSION</string>
  <key>LSUIElement</key><true/>
  <key>LSMinimumSystemVersion</key><string>13.0</string>
</dict></plist>
PLIST

# 兩種晶片各編一份，再合成一個通用二進位制
for arch in arm64 x86_64; do
  swiftc -O -target "$arch-apple-macos13.0" app/mac/*.swift -o "build/CrossPet-$arch" \
    -framework Cocoa -framework WebKit -framework ServiceManagement -framework Carbon
done
lipo -create build/CrossPet-arm64 build/CrossPet-x86_64 -output "$APP/Contents/MacOS/CrossPet"
rm build/CrossPet-arm64 build/CrossPet-x86_64

# 圖示：從 app/icon/AppIcon.png（1024，已帶圓角）生成全套尺寸
ICONSET=build/AppIcon.iconset
rm -rf "$ICONSET" && mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s app/icon/AppIcon.png --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) app/icon/AppIcon.png --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
rm -rf "$ICONSET"

cp -R app/web "$APP/Contents/Resources/web"
# maid fork：只帶 maid/ship-characters.txt 列出的角色
mkdir -p "$APP/Contents/Resources/characters"
cp characters/*.md "$APP/Contents/Resources/characters/"
grep -vE '^\s*(#|$)' maid/ship-characters.txt | while read -r c; do cp -R "characters/$c" "$APP/Contents/Resources/characters/$c"; done
cp integrations/crosspet-hook.py "$APP/Contents/Resources/crosspet-hook.py"
# 接入部分也打進包裡：App 啟動時會把已經裝過的 Claude mod / DeepSeek 外掛更新到新版；
# 接入工具也帶上，安裝 / 更新命令直接用它，不用再下載整份原始碼
cp -R integrations "$APP/Contents/Resources/integrations"
find "$APP/Contents/Resources/integrations" -name .DS_Store -delete
mkdir -p "$APP/Contents/Resources/tools"
cp tools/integrate.py "$APP/Contents/Resources/tools/integrate.py"
find "$APP/Contents/Resources/characters" -name raw -type d -prune -exec rm -rf {} +

xattr -cr "$APP" 2>/dev/null || true   # 去掉檔案來源標記之類的擴充套件屬性，免得打進發布包（zip 裡會多出一堆 ._ 檔案）
codesign --force --deep -s - "$APP"
echo "已編譯：$APP"

if [ "$1" = "--zip" ]; then
  (cd build && rm -f CrossPet-macOS.zip && ditto -c -k --norsrc --noextattr --keepParent CrossPet.app CrossPet-macOS.zip)
  echo "釋出包：$ROOT/build/CrossPet-macOS.zip"
fi
