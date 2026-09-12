#!/usr/bin/env bash
# 生成 GPL 合规的源码 zip，排除所有构建产物与本地隐私文件。
# 用法: bash tools/make-source-zip.sh <version>
# 产物: desktop_todo_Calendar-Source-<version>.zip（仓库根目录）
set -euo pipefail

VER="${1:?用法: make-source-zip.sh <version>}"
NAME="desktop_todo_Calendar"
OUT="${NAME}-Source-${VER}.zip"
rm -f "$OUT"

zip -r -q "$OUT" . \
  -x '*/bin/*' '*/obj/*' 'bin/*' 'obj/*' \
  -x 'publish/*' 'installer/*' 'dist/*' 'release/*' 'pkg/*' 'assets/*' 'release-assets/*' \
  -x 'MicaAgenda.Publish/*' 'DesktopCalendar.Publish/*' \
  -x '.git/*' 'TestResults/*' '*.log' \
  -x '.workbuddy/*' '.workbuddy-scratch/*' \
  -x '*-Source-*.zip' 'release-notes-*.md' \
  -x '*.DS_Store' 'appimagetool' 'tools/appimagetool' \
  -x 'MicaAgenda.UiTestAppData/*' 'MicaAgenda.UiSmokeAppData/*' 'MicaAgenda.ScreenshotAppData.*/*' \
  -x 'DesktopCalendar.UiTestAppData/*' 'DesktopCalendar.UiSmokeAppData/*' 'DesktopCalendar.ScreenshotAppData.*/*'

echo "源码包大小："
ls -la "$OUT"

echo "隐私与合规抽查（应无输出，且必须含 LICENSE）："
unzip -l "$OUT" | grep -Ei '/(bin|obj|publish|installer|dist|release|assets|\.git)/|\.log$|Source-.*\.zip$' && {
  echo "ERROR: 源码包仍含应排除的内容"; exit 1;
} || true
unzip -l "$OUT" | grep -i 'LICENSE' >/dev/null || { echo "ERROR: 源码包缺少 LICENSE"; exit 1; }
echo "OK"
