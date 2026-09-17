#!/usr/bin/env bash
# 校验 desktop_todo_Calendar 某版本的 GitHub Release（只读，安全）。
# 用法: bash scripts/check-release.sh vX.Y.Z
set -euo pipefail

TAG="${1:?用法: check-release.sh vX.Y.Z}"
REPO="bubu-LZY/desktop_todo_Calendar"

echo "=== $TAG ==="
gh release view "$TAG" --repo "$REPO" \
  --json tagName,isDraft,isPrerelease,publishedAt,assets \
  --jq '{tag:.tagName,draft:.isDraft,pre:.isPrerelease,published:.publishedAt,assets:[.assets[]|{name,mb:((.size/1048576)*100|round/100)}]}'

echo "=== releases/latest ==="
gh api "repos/$REPO/releases/latest" --jq .tag_name

echo
echo "期望：draft=false、pre=false；8 个资产（Setup.exe / 两架构 dmg+zip / AppImage / deb / Source.zip）；latest 指向 $TAG"
