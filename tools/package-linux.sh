#!/usr/bin/env bash
# 打包 Linux 产物：AppImage + deb
# 用法: bash tools/package-linux.sh <version>
# 产物: dist/desktop_todo_Calendar-<version>-x64.AppImage
#       dist/desktop_todo_Calendar_<version>_amd64.deb
set -euo pipefail

VER="${1:?用法: package-linux.sh <version>}"
BUNDLE="desktop_todo_Calendar"
EXE="MicaAgenda.Desktop"
ICON_SRC="MicaAgenda.App/Assets/task-icon-256.png"
ICON_NAME="${BUNDLE}.png"

[ -d pkg ] || { echo "ERROR: 缺少 pkg/ 目录（先 dotnet publish -o pkg）"; exit 1; }

mkdir -p dist

# ---------- AppDir ----------
APPDIR="dist/AppDir"
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin" \
         "$APPDIR/usr/share/applications" \
         "$APPDIR/usr/share/icons/hicolor/256x256/apps" \
         "$APPDIR/usr/share/doc/${BUNDLE}"
cp -R pkg/. "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/${EXE}"
cp "$ICON_SRC" "$APPDIR/usr/share/icons/hicolor/256x256/apps/${ICON_NAME}"
cp "$ICON_SRC" "$APPDIR/${ICON_NAME}"          # AppImage 根级图标约定
cp LICENSE "$APPDIR/usr/share/doc/${BUNDLE}/LICENSE"

cat > "$APPDIR/${BUNDLE}.desktop" <<DESK
[Desktop Entry]
Type=Application
Name=desktop_todo_Calendar
Comment=MicaAgenda 桌面日历
Exec=${EXE}
Icon=${BUNDLE}
Categories=Utility;Calendar;
Terminal=false
DESK
cp "$APPDIR/${BUNDLE}.desktop" "$APPDIR/usr/share/applications/${BUNDLE}.desktop"

# ---------- AppImage ----------
if [ ! -x appimagetool ]; then
  curl -fsSL -o appimagetool \
    https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x appimagetool
fi
ARCH=x86_64 ./appimagetool --appimage-extract-and-run "$APPDIR" "dist/${BUNDLE}-${VER}-x64.AppImage"

# ---------- deb ----------
DEBROOT="dist/debroot"
rm -rf "$DEBROOT"
mkdir -p "$DEBROOT/DEBIAN" \
         "$DEBROOT/usr/bin" \
         "$DEBROOT/usr/share/applications" \
         "$DEBROOT/usr/share/icons/hicolor/256x256/apps" \
         "$DEBROOT/usr/share/doc/${BUNDLE}"
cp -R pkg/. "$DEBROOT/usr/bin/"
chmod +x "$DEBROOT/usr/bin/${EXE}"
cp "$ICON_SRC" "$DEBROOT/usr/share/icons/hicolor/256x256/apps/${ICON_NAME}"
cp LICENSE "$DEBROOT/usr/share/doc/${BUNDLE}/LICENSE"
cp "$APPDIR/usr/share/applications/${BUNDLE}.desktop" "$DEBROOT/usr/share/applications/"
cat > "$DEBROOT/DEBIAN/control" <<CTRL
Package: desktop-todo-calendar
Version: ${VER}
Section: utils
Priority: optional
Architecture: amd64
Maintainer: MicaAgenda Team <noreply@example.com>
Description: MicaAgenda desktop calendar
 Cross-platform desk calendar / todo widget (Avalonia).
CTRL
dpkg-deb --build --root-owner-group "$DEBROOT" "dist/${BUNDLE}_${VER}_amd64.deb"

echo "Linux 产物："
ls -la dist/*.AppImage dist/*.deb
