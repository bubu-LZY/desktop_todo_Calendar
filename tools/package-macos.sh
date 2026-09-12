#!/usr/bin/env bash
# 打包 macOS 产物：.app 包 + .dmg + .zip
# 用法: bash tools/package-macos.sh <rid> <version>
#   rid     : osx-x64 | osx-arm64
#   version : 3.2.0（不含 v 前缀）
# 产物: dist/desktop_todo_Calendar-<version>-<arch>.dmg / .zip
set -euo pipefail

RID="${1:?用法: package-macos.sh <rid> <version>}"
VER="${2:?用法: package-macos.sh <rid> <version>}"
ARCH="${RID#osx-}"                 # x64 / arm64
BUNDLE="desktop_todo_Calendar"
EXE="MicaAgenda.Desktop"           # Avalonia 宿主的程序集名（apphost）
ICON_SRC="MicaAgenda.App/Assets/task-icon-256.png"

[ -d pkg ] || { echo "ERROR: 缺少 pkg/ 目录（先 dotnet publish -o pkg）"; exit 1; }

mkdir -p dist
APP="dist/${BUNDLE}.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# 1) 发布产物 → .app/Contents/MacOS
cp -R pkg/. "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/${EXE}"

# 2) 图标：从 256 PNG 生成多尺寸 .icns
ICONSET="dist/${BUNDLE}.iconset"
rm -rf "$ICONSET"
mkdir -p "$ICONSET"
gen() { sips -z "$1" "$1" "$ICON_SRC" --out "$2" >/dev/null; }
gen 16   "$ICONSET/icon_16x16.png"
gen 32   "$ICONSET/icon_16x16@2x.png"
gen 32   "$ICONSET/icon_32x32.png"
gen 64   "$ICONSET/icon_32x32@2x.png"
gen 128  "$ICONSET/icon_128x128.png"
gen 256  "$ICONSET/icon_128x128@2x.png"
gen 256  "$ICONSET/icon_256x256.png"
gen 512  "$ICONSET/icon_256x256@2x.png"
gen 512  "$ICONSET/icon_512x512.png"
gen 1024 "$ICONSET/icon_512x512@2x.png"
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/${BUNDLE}.icns"

# 3) Info.plist（CFBundleShortVersionString 必须与 tag 一致）
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>${BUNDLE}</string>
  <key>CFBundleDisplayName</key><string>desktop_todo_Calendar</string>
  <key>CFBundleIdentifier</key><string>com.micaagenda.desktop</string>
  <key>CFBundleVersion</key><string>${VER}</string>
  <key>CFBundleShortVersionString</key><string>${VER}</string>
  <key>CFBundleExecutable</key><string>${EXE}</string>
  <key>CFBundleIconFile</key><string>${BUNDLE}</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

# 4) GPL-3.0 合规：产物随附许可证
cp LICENSE "$APP/Contents/Resources/LICENSE"

# 5) dmg + zip
hdiutil create -volname "$BUNDLE" -srcfolder "$APP" -ov -format UDZO "dist/${BUNDLE}-${VER}-${ARCH}.dmg"
ditto -c -k --sequesterRsrc --keepParent "$APP" "dist/${BUNDLE}-${VER}-${ARCH}.zip"

echo "macOS 产物："
ls -la dist/*.dmg dist/*.zip
