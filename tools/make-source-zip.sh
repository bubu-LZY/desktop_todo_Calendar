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

echo "隐私与合规抽查："
# 只看归档内的条目名；用 ^ 锚定「顶层」目录，避免误伤源代码里的 MicaAgenda.App/Assets 等
BAD=$(unzip -Z1 "$OUT" | grep -E '(^|/)(bin|obj)/|^(publish|installer|dist|release|release-assets|assets|\.git|TestResults)/|\.log$|Source-[0-9.]+\.zip$' || true)
if [ -n "$BAD" ]; then
  echo "ERROR: 源码包仍含应排除的内容："
  echo "$BAD" | head -20
  exit 1
fi
unzip -Z1 "$OUT" | grep -q 'LICENSE' || { echo "ERROR: 源码包缺少 LICENSE"; exit 1; }
echo "OK（$(unzip -Z1 "$OUT" | wc -l) 个条目，含 LICENSE）"
