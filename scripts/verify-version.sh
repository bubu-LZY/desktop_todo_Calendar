#!/usr/bin/env bash
# 校验 desktop_todo_Calendar 的版本号是否落在全部已知位置（只读，安全）。
# 用法: bash scripts/verify-version.sh <version>   （在仓库根运行，例如 3.2.1）
set -euo pipefail

VER="${1:?用法: verify-version.sh <version>  例: 3.2.1}"
miss=0
check() { # <file> <pattern> <label>
  if grep -q -- "$2" "$1" 2>/dev/null; then
    echo "  OK   $3"
  else
    echo "  MISS $3   ($1)"; miss=1
  fi
}

echo "检查版本 $VER 是否落在全部位置："
check Directory.Build.props        "<Version>$VER</Version>"      "Directory.Build.props 的 <Version>"
check setup.iss                    "MyAppVersion \"$VER\""        "setup.iss 的 MyAppVersion 兜底"
check README.md                    "Setup-$VER.exe"               "README 的 Windows 安装包名"
check README.md                    "Source-$VER.zip"              "README 的源码包名"
check CHANGELOG.md                 "## v$VER"                     "CHANGELOG 顶部版本段"
check MicaAgenda.App/MainWindow.xaml.cs "v$VER"                   "WPF MainWindow 版本字面量"

echo "可疑的其它版本号（口径内应为空）："
grep -rnE '3\.[0-9]+\.[0-9]+' Directory.Build.props setup.iss 2>/dev/null | grep -v -- "$VER" || true

if [ "$miss" = 0 ]; then
  echo "版本号一致 OK"
else
  echo "存在缺失，请补齐后再发版"
  exit 1
fi
