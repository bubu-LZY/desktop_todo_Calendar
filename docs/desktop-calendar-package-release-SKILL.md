---
name: desktop-calendar-package-release
description: 通用桌面日历程序（.NET / Avalonia 或 WPF）的多平台打包发布 skill。当需要为任意桌面日历应用发布新版本、出安装包、升级版本号、配置四平台 CI 或排查打包/发布失败时使用。覆盖完整链路：版本号全位置升级 → 提交推送 → changelog → GitHub Actions 四路并行构建（Windows Inno Setup / macOS .app+dmg / Linux AppImage+deb）→ 汇总产物与源码 zip → 创建或更新 GitHub Release（标 latest）→ 逐项验证。所有项目参数（仓库、版本号位置、RID 矩阵、产物名）全部动态发现，不预设任何具体项目；含代理推送、认证失败、DNS 污染、CI 时区/文化差异导致"本地绿 CI 红"等实战坑。
---

# 通用桌面日历程序 打包发布

## 适用与不适用

**适用**：.NET 桌面应用（Avalonia / Uno / Eto / 纯控制台宿主）的多平台发布；WPF 宿主的 Windows-only 发布。

**硬门槛（先判定，别猜）**：

| 工程形态 | Windows | macOS | Linux |
|---|---|---|---|
| `net9.0-windows` + `UseWPF` / `UseWindowsForms` | ✅ | ❌ | ❌ |
| `net9.0` + Avalonia / Uno / Eto / 纯控制台 | ✅ | ✅ | ✅ |

一条命令验证能否跨平台：

```bash
dotnet publish <主工程> -c Release -r osx-x64 --self-contained true -o /tmp/probe-rid
# 报 NETSDK1082: Microsoft.WindowsDesktop.App 没有运行时包 ... → 锁死 Windows
```

若锁死 Windows：**只发布 Windows**，不要把必然失败的非 Windows 行留在 CI 里制造红灯。要出三平台，先做 UI 层跨平台化（Avalonia 宿主 + 共享 `net9.0` 数据/服务层）。

---

## 执行原则

1. **一切参数动态发现，禁止假设**——仓库从 `git remote get-url origin` 读，版本号位置靠全项目搜索，RID 矩阵由 UI 框架推，产物名从打包脚本与历史 Release 读。
2. **遇错先诊断根因，再用对应解法**——避坑指南按「现象 → 根因 → 解法」组织。
3. **网络故障不阻塞本地流程**——push/上传失败时本地打包链路（清理 → publish → 打包 → 压缩）并行推进，网络恢复后统一上传。
4. **能上 CI 就上 CI，绝不交叉编译**——RID 与宿主不一致会报 `NETSDK1082`；dmg 需 `hdiutil`、AppImage/deb 需 `appimagetool`/`dpkg-deb`，只能在对应系统生成。
5. **不泄露隐私**——见下方红线。

---

## 隐私与凭据红线（贯穿全程）

| 红线 | 做法 |
|---|---|
| 仓库地址 | 运行时从 `git remote get-url origin` 提取 `owner/repo`；**不写进指南 / 脚本 / 提交信息**，一律用 `<owner>/<repo>` 占位 |
| 本机路径 | 不把 `C:\Users\<name>\...`、`/home/<name>/...` 写进任何交付文件 |
| 个人邮箱 | 用 GitHub noreply（`<id>+<login>@users.noreply.github.com`）或现场 `git config user.email` |
| Token | **CI 只用内置 `GITHUB_TOKEN`**；手动兜底临时用 `GH_TOKEN`，用完立即清理 |
| remote URL 含凭据 | 形如 `https://user:token@github.com/...` 时**先清洗 token** 再写入任何文件或汇报 |
| 运行时日志 | 应用日志（如 `app-error.log`）含本机绝对路径；`.gitignore` 必须覆盖 `*.log`，源码 zip 必须排除 |
| 交付汇报 | 只给仓库 URL 与产物名，不给本机绝对路径 |

---

## Step 0. 从项目发现全部参数（必须先做）

| 参数 | 获取方法 |
|---|---|
| 项目根目录 | 当前目录含 `*.sln` 或 `*.csproj` |
| **UI 框架** | 搜各 `*.csproj` 的 `UseWPF` / `UseWindowsForms` / `Avalonia` → **决定平台矩阵** |
| 目标框架 | 各 csproj 的 `<TargetFramework>` |
| 当前版本号 | 搜 `-Setup-<数字>.<数字>.<数字>` / `<Version>` / changelog 顶部 |
| 版本号全部硬编码位置 | 全项目搜索旧版本号（见 Step 1） |
| GitHub 仓库 | `git remote -v` 的 origin |
| 主分支名 | `git branch --show-current`（`main` / `master` 皆可能，不要假设） |
| gh 登录状态 | `gh auth status` |
| **RID 矩阵** | 由 UI 框架推导 |
| **各平台产物名** | 打包脚本（`.iss` / `.sh`）的输出名模板 |
| 已有打包脚本 | 根目录 `*.iss`、`tools/*.sh`、`*.ps1` — **复用，不要另起一套** |
| **CI 工作流** | `.github/workflows/` 是否存在 |
| 许可证 | 仓库根 `LICENSE`（copyleft 必须随产物与源码包分发） |
| `.gitignore` 覆盖 | 必须含 `bin/`、`obj/`、`publish/`、`installer/`、`release/`、`TestResults/`、`*.log`、`*-Source-*.zip` |
| 应用内更新器 | 搜 `AutoUpdater` / `updateChecker` / `api.github.com/repos`，无则 Step 8 标 N/A |

---

## Step 1. 升级版本号（全位置，缺一不可）

**原理**：用户判断"有没有新版"靠 Release tag / 安装包文件名 / 应用关于页。任何一处漏改 → 「装了新版，界面还写旧版本号」或「同名安装包互相覆盖」。

**找齐所有位置——用搜索法，不要靠列举**：

```bash
OLD=$(gh release view --json tagName -q .tagName | tr -d v)   # 或看上次 Release 文件名
grep -rn "$OLD" --include=*.cs --include=*.csproj --include=*.iss --include=*.md --include=*.html \
  --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git --exclude-dir=publish --exclude-dir=installer .
```

**常见硬编码位置**（核对清单，非全集）：

| 位置 | 形态 |
|---|---|
| `Directory.Build.props` / `*.csproj` | `<Version>X.Y.Z</Version>`（**推荐的单一来源**） |
| 主窗口代码 | `Title = "<App> vX.Y.Z";` |
| 启动日志字面量 | `$"[STARTUP] ... - vX.Y.Z - ..."`（**新项目可能已移除，搜不到别硬找**） |
| 安装脚本 | `#define MyAppVersion "X.Y.Z"` |
| `README.md` | `-Setup-X.Y.Z.exe`、`-Source-X.Y.Z.zip` |
| `CHANGELOG.md` | 新增 `## vX.Y.Z（<日期>）` 段（不删历史） |
| 关于页 / 设置页 | `vX.Y.Z` 文案 |
| 演示页 `docs/index.html` | 页脚版本号（若有） |

**建议建立单一版本号来源**（`Directory.Build.props` 的 `<Version>`），代码用 `Assembly.GetExecutingAssembly().GetName().Version` 派生，安装包由 CI `/D` 注入。

**新版本号规则**：修 bug `patch` +1；新增能力 `minor` +1；破坏性 `major` +1。tag = `v{新版本号}`。**拿不准问用户**。

**验证**：改完重跑上面的 grep，`$OLD` 应只剩 changelog 历史；`$NEW` 应命中所有目标位置。

---

## Step 2. git 提交并推送

```bash
git status --short                      # 审查改动，确认无异常大文件
git add <明确列出本次改动的文件>          # 优先按文件名加，避免 -A 误加凭据
git -c user.name="release" -c user.email="release@local" commit -m "vX.Y.Z: <摘要>"
git push
```

> 仓库未配置身份时**用内联覆盖，不要改全局 config**。

**push 失败的诊断入口**：走避坑指南坑 P / 坑 Q。**push 卡住时不要干等**——先做本地验证与打包（Step 4.3 / Step 5），最后回来重推。

---

## Step 3. 写 changelog

在 `CHANGELOG.md` **顶部**加一段（不删历史），CI 自动截取作为 Release 正文：

```markdown
## vX.Y.Z（YYYY-MM-DD）

### 新增功能
- ...

### 修复
- ...
```

⚠️ **标题必须带日期后缀**——CI 截取用 `awk` 前缀匹配。改成纯 `## vX.Y.Z` 会取空。

---

## Step 4. 配置 CI 多平台构建流水线

### 4.1 工作流骨架（`.github/workflows/release-build.yml`）

```yaml
name: Build & Release

on:
  push:
    tags: ['v*']
  workflow_dispatch:        # 手动干跑：只构建不发布

permissions:
  contents: write           # 只用内置 GITHUB_TOKEN

concurrency:
  group: release-${{ github.ref }}
  cancel-in-progress: false

jobs:
  build:
    name: Build ${{ matrix.label }}
    runs-on: ${{ matrix.os }}
    strategy:
      fail-fast: false      # 一路失败不影响其他平台产出
      matrix:
        include:
          - { label: windows-x64, os: windows-latest, rid: win-x64,   pkg: windows }
          - { label: macos-x64,   os: macos-15-intel, rid: osx-x64,   pkg: macos }
          - { label: macos-arm64, os: macos-15,       rid: osx-arm64, pkg: macos }
          - { label: linux-x64,   os: ubuntu-22.04,   rid: linux-x64, pkg: linux }
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '9.0.x' }

      - name: 还原
        run: dotnet restore <解决方案或主工程>

      # 跨平台单测：每个平台都跑，提前暴露平台差异（路径分隔符 / 编码 / 大小写敏感）
      - name: 测试（跨平台单测）
        run: dotnet test <跨平台测试工程> -c Release --no-restore

      - name: 发布（${{ matrix.rid }}）
        run: >
          dotnet publish <桌面主工程> -c Release
          -r ${{ matrix.rid }} --self-contained true
          -p:PublishSingleFile=false -o pkg

      - name: 打包（Windows / Inno Setup）
        if: matrix.pkg == 'windows'
        shell: pwsh
        run: |
          choco install innosetup --no-progress -y
          & "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" /DMyAppVersion=${{ github.ref_name }} setup.iss

      - name: 打包（macOS / .app + dmg + zip）
        if: matrix.pkg == 'macos'
        run: bash tools/package-macos.sh "${{ matrix.rid }}" "${{ github.ref_name }}"

      - name: 打包（Linux / AppImage + deb）
        if: matrix.pkg == 'linux'
        run: bash tools/package-linux.sh "${{ github.ref_name }}"

      - uses: actions/upload-artifact@v4
        with:
          name: ${{ matrix.label }}
          if-no-files-found: error
          path: |
            installer/*.exe
            dist/*.dmg
            dist/*.zip
            dist/*.AppImage
            dist/*.deb

  release:
    name: Publish Release
    needs: build
    if: startsWith(github.ref, 'refs/tags/v')   # 手动触发时不发布
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/download-artifact@v4
        with: { path: assets, merge-multiple: true }
      - name: 生成源码 zip
        run: bash tools/make-source-zip.sh
      - name: 生成发布说明
        run: bash tools/extract-changelog.sh "${GITHUB_REF_NAME}" > release-notes.md
      - name: 创建或更新 Release
        env: { GH_TOKEN: '${{ secrets.GITHUB_TOKEN }}' }
        run: |
          TAG="${GITHUB_REF_NAME}"
          # download-artifact 会保留 installer/ dist/ 子目录，必须 find 取文件
          mapfile -t ASSETS < <(find assets -type f; ls *-Source-*.zip)
          if gh release view "$TAG" >/dev/null 2>&1; then
            gh release upload "$TAG" "${ASSETS[@]}" --clobber
            gh release edit "$TAG" --notes-file release-notes.md --latest
          else
            gh release create "$TAG" "${ASSETS[@]}" --title "$TAG" --notes-file release-notes.md --latest
          fi
      - name: 核对发布结果
        env: { GH_TOKEN: '${{ secrets.GITHUB_TOKEN }}' }
        run: gh release view "${GITHUB_REF_NAME}" --json tagName,isDraft,isPrerelease,assets --jq '{tag:.tagName,draft:.isDraft,pre:.isPrerelease,assets:[.assets[]|{name,size}]}'
```

**要点**：
- `fail-fast: false` — 四路跑完，一次看清哪个平台有问题
- `--self-contained true` — 目标机免装运行时
- **绝不用 `gh release delete`** — 旧 Release 是用户回滚依赖（坑 N）。用 `upload --clobber` 幂等更新
- `macos-15-intel` 出 x64、`macos-15` 出 arm64；**不要在 arm64 runner 上出 x64 包**（坑 M）
- 干跑验证：`gh workflow run release-build.yml`

### 4.2 平台打包脚本要点

**macOS（`tools/package-macos.sh`）** — `dotnet publish` 只出可执行文件，`.app` 结构自己拼：

```
<App>.app/Contents/
├── Info.plist          # CFBundleName / CFBundleIdentifier / CFBundleExecutable / CFBundleIconFile / CFBundleShortVersionString
├── MacOS/              # dotnet publish 产物全部放这里
└── Resources/<icon>.icns
```

```bash
mkdir -p "<App>.app/Contents/MacOS" "<App>.app/Contents/Resources"
cp -R pkg/* "<App>.app/Contents/MacOS/"
chmod +x "<App>.app/Contents/MacOS/<App>"
cp build/<icon>.icns "<App>.app/Contents/Resources/"
# Info.plist 版本号必须与 tag 一致，否则"关于"页显示旧版本
hdiutil create -volname "<App>" -srcfolder "<App>.app" -ov -format UDZO "dist/<App>-<ver>-<arch>.dmg"
ditto -c -k --sequesterRsrc --keepParent "<App>.app" "dist/<App>-<ver>-<arch>.zip"
```

**Linux（`tools/package-linux.sh`）** — AppDir 骨架 + `appimagetool`；`.deb` 用 `dpkg-deb`：

```bash
mkdir -p "AppDir/usr/bin" "AppDir/usr/share/applications" "AppDir/usr/share/icons/hicolor/256x256/apps"
cp -R pkg/* "AppDir/usr/bin/"
curl -fsSL -o appimagetool \
  https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
chmod +x appimagetool
./appimagetool --appimage-extract-and-run AppDir "dist/<App>-<ver>-x64.AppImage"
dpkg-deb --build debroot "dist/<App>_<ver>_amd64.deb"
```

**Windows（Inno Setup）** — 沿用项目 `setup.iss`，只需版本号可被 CI 注入：

```ini
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
```
```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DMyAppVersion=5.2.2 setup.iss
```

### 4.3 本地 Windows 快速验证（发版前推荐）

```bash
dotnet test <跨平台测试工程> -c Release       # 先过测试，别把红灯推上去
rm -rf pkg installer
dotnet publish <桌面主工程> -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o installer/publish
```

**完成标准是产物出现且大小合理**：`.exe` ≥50MB（自包含典型 60~200MB），**不要只信退出码**。

---

## Step 5. 压缩源码 zip

**命名沿用历史**：`<项目名>-Source-<version>.zip`（注意无 `v` 前缀）。

**排除项**：

| 排除 | 原因 |
|---|---|
| `bin/` `obj/` | 编译中间产物 |
| `publish/` `installer/` `dist/` `release/` `assets/` | 已打包产物 / CI 落盘，会膨胀到 GB |
| `.git/` | 版本库 |
| `TestResults/` | 测试输出 |
| `*.log`（含 `app-error.log`） | **含本机绝对路径，打进公开包即隐私泄露** |
| `release-notes-*.md` | 发布说明草稿 |
| 历史 `*-Source-*.zip` | 自嵌套 |

**copyleft 硬要求**：GPL / AGPL / LGPL 项目的源码包**必须包含 `LICENSE`**，且二进制产物目录也各带一份（Inno `[Files]` 加一条、mac `.app/Contents/Resources/`、Linux `usr/share/doc/<app>/`）。

**必做校验**（不能只看"命令跑完了"）：

```bash
ls -l <项目名>-Source-*.zip        # 应在 <10MB 量级
unzip -l <项目名>-Source-<ver>.zip | grep -Ei '/(bin|obj|publish|installer|dist|release|\.git)/|\.log$|\.zip$' | head
# 期望无输出
unzip -l <项目名>-Source-<ver>.zip | grep -i 'LICENSE'    # GPL 项目必须有
```

---

## Step 6. 打 tag 上传（触发 CI）

```bash
git -c user.name="release" -c user.email="release@local" tag -a vX.Y.Z -m "vX.Y.Z: <摘要>"
git push origin vX.Y.Z
gh run list --limit 2
gh run watch <run-id> --exit-status
gh run view <run-id> --log-failed     # 失败时看这个
```

⚠️ **带注解的 tag（`-a`）也需要身份**，否则 `Committer identity unknown` 直接失败。

**CI 已失败、修完要重发时**（tag 尚无 Release 时可重指，`gh release create` 前都安全）：

```bash
git push origin main
git tag -d vX.Y.Z
git push origin :refs/tags/vX.Y.Z     # 删远端 tag
git tag -a vX.Y.Z -m "..."            # 在新 commit 上重建
git push origin vX.Y.Z                # 重新触发
```

**若该 tag 已生成过 Release，不要重指**——直接发新版本号。

### 本地兜底（CI 不可用 / 只发单平台）

```bash
gh release create "v$V" <产物...> --repo "$REPO" --title "v$V" \
  --notes-file "release-notes-v$V.md" --latest --target <主分支>
```
- **`--latest` 必须带**，否则 `releases/latest` 仍指旧版
- **全部资产在同一条命令上传**，不分批
- 含 100MB 级安装包，超时给足（≥10 分钟）

---

## Step 7. 验证发布结果

```bash
gh release view "v$V" --repo "$REPO" \
  --json tagName,isDraft,isPrerelease,targetCommitish,assets \
  --jq '{tag:.tagName,draft:.isDraft,pre:.isPrerelease,commit:.targetCommitish,assets:[.assets[]|{name,sizeMB:(.size/1048576*100|round/100)}]}'
gh api repos/$REPO/releases/latest --jq .tag_name     # 确认 latest 指向新版本
```

确认：tag 正确 / 非 draft / 非 prerelease / target 指向主分支；**平台矩阵每份产物都在**；大小合理（安装包 60~300MB、源码 zip <10MB）。

⚠️ **以 API 为准，不要信本地 git 引用**——用"URL 内嵌 token"这类非 origin 地址推送时，本地 `origin/main` 不会更新，`git status` 会误报 `ahead N`（坑 R）。

---

## Step 8. 产物命名与安装说明

> 无应用内更新器时，本节只做命名约定 + 交付说明（"更新器契约"标 N/A）。

### 8.1 命名契约

| 平台 | 产物名 | 原因 |
|---|---|---|
| Windows（只 x64） | `<项目名>-Setup-<version>.exe` | 单架构可省架构词，**沿用历史命名** |
| macOS（x64 + arm64） | `<项目名>-<version>-<arch>.dmg` / `.zip` | **必须含架构词**，否则两架构无法区分 |
| Linux（只 x64） | `<项目名>-<version>-x64.AppImage`、`<项目名>_<version>_amd64.deb` | 含架构词更稳 |
| 源码 | `<项目名>-Source-<version>.zip` | 沿用历史（无 `v` 前缀） |

> ⚠️ 若将来恢复 Windows x86，必须改为含架构词（`-x86`），否则人工选包会选错。
> ⚠️ GitHub 会把资产名空格换成 `.`（坑 Q）。

### 8.2 各平台安装方式

| 平台 | 安装 | 未签名提示与绕过 |
|---|---|---|
| Windows | 运行 `-Setup-<ver>.exe` | SmartScreen「Windows 已保护你的电脑」→ 更多信息 → 仍要运行 |
| macOS | 打开 `.dmg`，拖进「应用程序」 | Gatekeeper → `xattr -dr com.apple.quarantine /Applications/<App>.app`（坑 H） |
| Linux | AppImage 加执行权限运行；或 `sudo dpkg -i *.deb` | AppImage 需 `libfuse2`（坑 I） |

---

## Step 9. 交付汇报

1. Release 地址：`https://github.com/<owner>/<repo>/releases/tag/v<version>`
2. 产物清单（按平台列出文件名与大小）
3. 各平台安装方式 + **未签名带来的系统提示与绕过方法**
4. 提醒：安装前**完全退出旧版（含托盘图标右键退出）**，否则会"装了新版仍复现旧 bug"（坑 S）
5. GPL 项目补充：源码包与产物均已随附 `LICENSE`
6. 本次**未交付**的能力明确说清原因与后续计划

---

# 避坑指南

### 坑 A：WPF / WinForms 工程无法产出 macOS / Linux 包
**现象**：`dotnet publish -r osx-x64` 报 `NETSDK1082: Microsoft.WindowsDesktop.App 没有运行时包可用于指定的 RuntimeIdentifier`。
**根因**：`UseWPF` / `UseWindowsForms` 绑定 `Microsoft.WindowsDesktop.App`，只有 Windows RID 运行时包。
**解法**：短期只发 Windows；长期做跨平台宿主（Avalonia），数据/服务层抽到 `net9.0` 共享。

### 坑 B：`dotnet test <解决方案>` 静默跑 0 个测试还返回成功
**现象**：报「已通过!」但用例数是 0。
**根因**：测试工程**没登记进 `.sln`**，假绿灯。
**解法**：`dotnet sln <sln> list` 确认 → `dotnet sln <sln> add <测试工程>.csproj`。**永远核对用例总数**，不只看退出码。

### 坑 C：发布路径与打包脚本读取路径不一致
**现象**：产物缺文件或报找不到 `installer\publish\*`。
**根因**：README 的 `-o publish` 与 `setup.iss` 的 `Source: installer\publish\*` 不一致。
**解法**：以打包脚本为准统一目录，并同步修正文档。自检：`grep -n "Source:" setup.iss && grep -rn "\-o publish" README.md docs/`。

### 坑 D：单文件 / 自包含发布仍需要原生 DLL 伴随
**现象**：干净机器上「安装后打不开」，本机正常。
**根因**：`PublishSingleFile` 只合并托管程序集；`wpfgfx_cor3.dll`、`D3DCompiler_47_cor3.dll`、`libSkiaSharp`、`libHarfBuzzSharp` 等原生依赖仍需在 exe 旁。
**解法**：安装包**整目录打包**，`Source: "installer\publish\*"; Flags: ignoreversion recursesubdirs`。

### 坑 E：开了 `PublishTrimmed` 后 XAML 反射找不到类型
**现象**：发布版启动即崩 / 界面空白，异常含 `XamlParseException` / `TypeLoadException`。
**根因**：裁剪器看不到 XAML/反射里的类型引用，剪掉了。
**解法**：**不要对 WPF / Avalonia 桌面工程启用 `PublishTrimmed`**。

### 坑 F：Inno Setup 只能在 Windows 上编译
**解法**：Windows 包只在 `windows-latest` 产出；macOS 用 `hdiutil`+`ditto`，Linux 用 `appimagetool`+`dpkg-deb`——三者互不替代。

### 坑 G：安装脚本的清理逻辑误伤用户数据目录
**现象**：升级安装后任务/配置全丢。
**根因**：清理"非白名单文件"的路径判定不严，触及 `%APPDATA%` / `%LOCALAPPDATA%`。
**解法**：清理**只作用于安装目录**并加护栏：
```pascal
AppDir := ExpandConstant('{app}');
IsSafePath := (Pos('AppData', AppDir) = 0) and (Pos('Roaming', AppDir) = 0) and (Pos('Local\', AppDir) = 0);
if not IsSafePath then begin Log('ABORT: refusing to clean ' + AppDir); Exit; end;
```

### 坑 H：未签名 macOS 包被 Gatekeeper 拦截
**解法**：告知绕过 `xattr -dr com.apple.quarantine /Applications/<App>.app`，写进 Release 说明。**不要**在 CI 里设猜测性签名配置碰运气。

### 坑 I：AppImage 在缺 libfuse2 的发行版上无法启动
**现象**：`dlopen(): error loading libfuse.so.2`。
**解法**：README 写明 `sudo apt install libfuse2`；同时提供 `.deb` 兜底。

### 坑 J：macOS runner 标签会退役
**现象**：`The macOS runner version 'macos-XX' is not supported`。
**解法**：Intel 用 `macos-15-intel`、Apple Silicon 用 `macos-15`；退役后查文档换在役标签，**不要**改成在 arm64 上跑 x64。

### 坑 K：`read release-assets/installer: is a directory`
**现象**：收集资产时把目录当文件传给 `gh release`。
**根因**：`download-artifact` 保留 `installer/`、`dist/` 子目录。
**解法**：用 `find release-assets -type f`，**不能** `release-assets/*`。

### 坑 L：源码包隐私抽查误伤
**现象**：检查脚本把 `Assets/` 源码当成构建产物。
**根因**：`unzip -Z1 | grep` 加了 `-i`，大小写不敏感。
**解法**：**不加 `-i`**，且只锚定**顶层**构建目录。

### 坑 M：macOS 交叉出包拿到错误原生依赖
**根因**：在 arm64 runner 上用 `-r osx-x64`，RID 与宿主不一致。
**解法**：`macos-15-intel` 出 x64、`macos-15` 出 arm64，各出各的。

### 坑 N：绝不要删除已发布的 Release 资产
**现象**：老用户回滚下载 404；README 链接失效。
**解法**：`gh release upload --clobber` 幂等更新；旧 tag / 旧 Release / 旧资产**一律保留**。

### 坑 O：安装包版本号与 tag 不一致
**解法**：按 Step 1 搜索法逐处核对；优先单一版本号来源 + CI `/D` 注入。

### 坑 P：`git push` 报 401 / 静默 exit 128（**最高频，必读**）
**现象**：极具误导性——`git ls-remote` 能通、`gh auth status` 正常、`gh api` 返回 200，但 `git push` **静默 exit 128、零输出**。

**根因**：仓库未配置 `credential.helper` 时，git 先发一个**未认证**请求 → GitHub 回 `401 Unauthorized` → git **直接放弃，连凭据助手都不调用**。所以"gh 明明登录着却推不上去"。

**诊断（必须开 trace，否则看不到真相）**：
```bash
GIT_TRACE=1 GIT_CURL_VERBOSE=1 git push origin main 2> trace.txt
# 看 trace 最后几行：停在 401 之后 → 就是本坑
```

**首选解法——URL 内嵌 token（最可靠）**：
```bash
TOK=$(gh auth token)
git -c http.proxy=<proxy> -c https.proxy=<proxy> -c credential.helper= \
    push "https://x-access-token:$TOK@github.com/<owner>/<repo>.git" main
```
**备选**：`gh auth setup-git`（写 `~/.gitconfig` 的 helper；单独用常常不够）。

**辅助确认**：
```bash
gh auth status                                    # 登录状态 + scopes
printf 'protocol=https\nhost=github.com\n\n' | gh auth git-credential get   # helper 是否返回
gh api repos/<owner>/<repo>                       # permissions.push / admin
```

### 坑 Q：github.com 连不上（DNS 污染 / TLS 重置），但 gh api 正常
**现象**：`git push` 报 `Failed to connect to github.com port 443`；`gh api` 一切正常。

**诊断**：
```bash
[System.Net.Dns]::GetHostAddresses("github.com")   # 可能被污染到 20.205.243.166
Test-NetConnection 140.82.112.3 -Port 443          # 试备用 IP
```

**关键陷阱：GET 通 ≠ POST 通**。写 hosts 映射只能救 **GET / `ls-remote`**：
```
140.82.112.3 github.com     # 先备份 hosts
```
**但 `git push` 的 `git-receive-pack` POST 仍会挂**（HTTP 000，约 21s 超时）——**直连绕不过 POST 封锁，推送必须走代理。**
⚠️ **用完务必删除该 hosts 行并还原。**

**代理端口要实测，别照抄**——同机常有多个端口，只有一个能用：
```powershell
foreach ($p in 10808,7892,7890,10809) {
  foreach ($s in "http","socks5h") {
    curl.exe -s -o NUL -w "$p/$s -> %{http_code}`n" --max-time 12 -x "$s://127.0.0.1:$p" "https://api.github.com"
  }
}
```
> 实战：某端口能建 CONNECT 隧道（返回 200）但 **TLS 握手稳定失败**（`schannel: failed to receive handshake`，重试全 `000`）；真正可用的是另一个。**"端口能连" ≠ "端口能用"，必须用 https 请求验证。**

⚠️ **环境变量 `HTTP_PROXY`/`HTTPS_PROXY` 会覆盖 git 配置**，且可能指向早失效的端口。推送前清空或显式 `-c` 覆盖：
```powershell
$env:HTTP_PROXY=""; $env:HTTPS_PROXY=""      # 或
git -c http.proxy=http://127.0.0.1:<port> -c https.proxy=http://127.0.0.1:<port> push origin main
```
**不要**用空值强制直连（`-c http.proxy=`）——那通常会失败。

### 坑 R：本地 `git status` 误报 `ahead N`
**根因**：用"URL 内嵌 token"等非 origin 地址推送时，本地 remote-tracking ref 不会更新。
**解法**：**以 API 为准**：`gh api repos/<owner>/<repo>/commits/main --jq .sha` 对比 `git rev-parse HEAD`。

### 坑 S：CI 测试"本地绿、CI 红"（**时区 / 文化差异，必读**）
**现象**：本地 `dotnet test` 全过，推上去 CI 四平台全挂。CI runner 是 **UTC + 英文/invariant culture**，本机常是 **UTC+8 + 中文 culture**。

**两类必自查的脆弱断言**：

1. **「固定日期 + 挂钟时刻」组合**。❌ 反例：给任务设 `2026-09-20` 的提醒，却断言"提前一天档位已记为已推"——隐含要求当前时刻已过 `09-19 17:00`（触发点 + 60 分钟长档位宽限）。本机 19:34 成立，CI 跑在 UTC 11:34 不成立 → 挂。
   ✅ 正解：用**相对当下的过去/未来日期**，如 `DateOnly.FromDateTime(DateTime.Now).AddDays(-3)`，任何时区任何时刻都成立。
2. **对中文/本地化字符串 `OrderBy` 后断言整体序列**。❌ 反例：`titles.OrderBy(t => t)` 再 `Assert.Equal(["周日","周一"], ...)`——ICU 排序 CI 得 `周一/周日`、本机得 `周日/周一`。
   ✅ 正解：顺序无关断言——`Assert.Equal(2, len)` + `Assert.Contains("周一", titles)`。

**复现手段**：`$env:TZ="UTC"` 后再跑测试（Windows 上 `TZ` 不一定生效，最可靠的是**把断言的时刻/顺序依赖彻底消除**）。

**推论**：**推送前必须本地跑一遍测试**——CI 四平台都跑测试，测试挂了 = 整个 Release 不产出。

### 坑 T：tag 身份 / 提交身份缺失
**现象**：`git tag -a` 报 `Committer identity unknown`。
**解法**：`git -c user.name="X" -c user.email="X@local" tag -a vX.Y.Z -m "..."`。

### 坑 U：窗口位置 / 尺寸"记不住"（重启后回默认位置）

**现象**：用户反馈「每次电脑重启，窗口都回到默认位置」；但正常从托盘退出再启动，位置是对的。

**根因（两条，缺一不可 —— 只修一条不管用）**：
1. **存了但从没读过**。启动路径常写成 `ApplyWindowBounds(GetBoundsForView(ViewMode))`，而
   `GetBoundsForView` 是"切视图用"的：它返回 `new WindowBounds(Position.X, Position.Y, DefaultW, H)`
   —— 位置取**当前默认值**，持久化的 `Settings.WindowBounds` 根本没参与。写进去的数据成了死数据。
2. **重启 / 关机这条路径上压根没存**。窗口位置通常只在「托盘正常退出」和「切视图」两处落盘；
   而重启走的是系统会话结束消息。若程序还带 `Closing += (_, e) => e.Cancel = !_allowClose`
   （桌面挂件禁止点 × 关闭），系统发来的关闭也会被一并挡掉 → 关机时零落盘动作。

**自检方法**：全局搜 `ApplyWindowBounds(` 的每个调用点，逐个确认传进去的 `WindowBounds`
是否来自持久化设置（`Settings.WindowBounds` / `*WindowBounds`），而不是现算的默认值。

**修法（四件事一起做）**：
1. 拆两个方法：`GetBoundsForView`（切视图，位置不动）与 `GetStartupBounds`（启动，**连位置一起恢复**）。
   优先级：按视图记忆 → 通用 `WindowBounds` → 默认值；且只在 `Width > 0 && Height > 0` 时采用。
2. **选屏要按目标坐标**，不能用 `ScreenFromWindow(this)` —— 启动时窗口还在默认位置，必然选错屏。
3. **越界拉回**：各屏工作区并集覆盖不到窗口面积一半（典型：外接显示器被拔）就拉回主屏居中偏上。
   注意 **DPI 坑**：`Screen.WorkingArea` 是**物理像素**，窗口 `Width/Height` 是**逻辑像素**，
   比较前必须除以 `screen.Scaling`，否则 125% 缩放下夹取失效。
4. **落盘时机挂到"几何稳定"而不是"退出路径"**：
   - 拖动 / 缩放停下 ~1.2s 防抖落盘（用 `_revision` / `_savedRevision` 版本对判断有无待存改动）。
   - 关机 / 重启同步落盘（见下）。

**⚠️ 框架级陷阱（Avalonia）**：**`Window.PositionChanged` 在自定义标题栏走 `BeginMoveDrag`
（原生拖拽）时，整个拖动过程一次都不触发** —— 靠它记位置等于没记。`SizeChanged` 是可靠的。
位置必须走 Win32：`SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, ..., WINEVENT_OUTOFCONTEXT)`
+ `GetWindowRect` 取真实坐标，回调里再过滤"坐标其实没变"的重复消息。

**⚠️ 关机落盘必须同步**：会话结束路径上**不能** `await SaveAsync()` ——
系统只留几十到几百毫秒，异步 I/O 的续体可能还没被调度到进程就没了，表现就是"存了但没生效"。
需要给数据层补一个**同步** `Save()`：文件流用 `FileOptions.WriteThrough` 绕过系统写缓存，
信号量用 `Wait(0)`（拿不到锁直接放弃返回 false，绝不能在关机路径上等），退避重试上限压到 ~300ms。

**⚠️ 关机钩子怎么挂**：若程序已有窗口过程子类化（例如为了吃掉最小化消息），顺路把
`WM_QUERYENDSESSION (0x0011)` / `WM_ENDSESSION (0x0016)` 接出来即可（`WM_ENDSESSION` 带
`wParam == 0` 表示"又不结束了"，别触发）。**别用 .NET 的 `SystemEvents.SessionEnding`** ——
它需要额外的 `Microsoft.Win32.SystemEvents` 包，为关一个挂件不值得多引依赖。
再叠一层 `AppDomain.ProcessExit` 兜底，两层共享一个"只跑一次"闸门。

**⚠️ WPF 宿主注意**：`Screen` / `Screen.WorkingArea` 是 WinForms 类型。若 csproj 里有
`<Using Remove="System.Windows.Forms" />`（为避免与 WPF 命名空间冲突，很常见），
必须显式 `using Screen = System.Windows.Forms.Screen;`，并给 `Point` 起别名
（`Screen.FromPoint` 收 `System.Drawing.Point`，与 WPF `Point` 冲突）；
`WorkingArea` 是 `System.Drawing.Rectangle`，与 WPF `Rect` 不能直接运算，需转换。

---

## 关键约束清单

✅ **必须**：
- 版本号按搜索法逐处更新，tag 与工程内版本号一致
- CI 只用内置 `GITHUB_TOKEN`
- macOS / Linux 包在对应 runner 产出，绝不交叉编译
- 发布用 `--latest`；已存在 Release 用 `upload --clobber` 更新
- 源码 zip 排除 `bin/obj/publish/installer/dist/release/assets/.git/TestResults/*.log`，**含 `LICENSE`**
- 安装包整目录打包（含原生 DLL / `libSkiaSharp`）
- 推送前本地跑测试；核对 `dotnet test` 的**用例总数**
- 汇报给出各平台安装方式 + 未签名提示

❌ **绝对不能**：
- WPF / WinForms 工程直接往 macOS / Linux 发
- 交叉编译跨平台包
- `dotnet sln` 里漏登记测试工程
- 启用 `PublishTrimmed`
- 删除旧 Release 资产 / 旧 tag
- 把本机绝对路径、Token、个人邮箱写进任何交付文件
- 让安装脚本清理逻辑可能触及 `%APPDATA%` / `%LOCALAPPDATA%`
- 在测试里用「固定日期 + 挂钟时刻」或对中文串 `OrderBy` 后断言整体序列

---

## 交付汇报模板

```markdown
## 发布完成：v<version>

**Release**：https://github.com/<owner>/<repo>/releases/tag/v<version>

**产物**
| 平台 | 文件 | 大小 |
|---|---|---|
| Windows | <项目名>-Setup-<ver>.exe | xx MB |
| macOS x64 | <项目名>-<ver>-x64.dmg | xx MB |
| macOS arm64 | <项目名>-<ver>-arm64.dmg | xx MB |
| Linux | <项目名>-<ver>-x64.AppImage | xx MB |
| Linux | <项目名>_<ver>_amd64.deb | xx MB |
| 源码 | <项目名>-Source-<ver>.zip | x.x MB |

**安装方式**
- Windows：运行 exe。未签名 → SmartScreen，「更多信息 → 仍要运行」
- macOS：拖入「应用程序」后 `xattr -dr com.apple.quarantine /Applications/<App>.app`
- Linux：AppImage 加执行权限运行（需 libfuse2），或 `sudo dpkg -i *.deb`

**注意**：安装前请完全退出旧版（含托盘图标右键退出），否则可能复现旧问题。

**本次未覆盖**：<平台或能力 + 原因 + 后续计划；无则写"无">
```

---

## 相关资源

- 项目专属发布 skill（含具体仓库、版本号落点、产物命名）：`desktop-todo-calendar-release`
- push 网络/认证疑难专项诊断：`github-push-network-auth-diagnosis`
