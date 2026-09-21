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

⚠️ **环境变量 `HTTP_PROXY`/`HTTPS_PROXY` 会覆盖 git 配置**，且可能指向早失效的端口。

**🔥 关键发现（先做这一步，能省掉后面 90% 的折腾）**：
这个环境的 shell **每次新起会话都会重新注入一对代理环境变量**，而端口往往是**早已失效**的
（实测被注入 `http://127.0.0.1:59776`，而真正能用的代理在 `10808`）。
于是 git 一直在往一个死端口发 CONNECT → 报的却是 `CONNECT tunnel failed, response 502` 或
`schannel: failed to receive handshake` —— **看起来像"墙/网络问题"，其实只是环境变量错了**。

更要紧的是：**把这两个变量清空后，直连是通的**。
```powershell
# 先看清楚当前被注入的是什么
"HTTP_PROXY=$env:HTTP_PROXY  HTTPS_PROXY=$env:HTTPS_PROXY"
# 清空（注意：要显式赋空串，"Remove-Item Env:XXX"在该环境里不一定生效）
$env:HTTP_PROXY=""; $env:HTTPS_PROXY=""; $env:ALL_PROXY=""
git -c http.version=HTTP/1.1 ls-remote origin main    # 实测 exit=0，直连可用
```
> 本次 `git push` / `fetch` / `ls-remote` **在清空环境变量后直连全部成功**，
> 一次都不用代理。之前"必须走代理"的结论，其实是被那对陈旧环境变量误导的。
> **所以顺序是：① 看清并清空代理环境变量 → ② 试直连 → ③ 直连不行才去找可用代理。**

⚠️ **清空操作必须在与 git 命令同一条命令里执行** —— 环境变量由 shell 每次重新注入，
上一条命令清了，下一条命令又会回来。**别指望跨命令生效。**

**直连确实不通时**，再去逐个端口实测（能连 ≠ 能用）：
```powershell
$env:HTTP_PROXY=""; $env:HTTPS_PROXY=""
foreach ($p in 10808,7892,7890,10809) {
  foreach ($s in "http","socks5h") {
    curl.exe -s -o NUL -w "$p/$s -> %{http_code}`n" --max-time 12 -x "$s://127.0.0.1:$p" "https://api.github.com"
  }
}
```
用代理推送时加 HTTP/1.1 + 短重试（代理偶发 502，重试即可）：
```powershell
$tok = gh auth token
$url = "https://x-access-token:$tok@github.com/<owner>/<repo>.git"
for ($i=1; $i -le 6; $i++) {
  git -c credential.helper= -c http.version=HTTP/1.1 push $url main:main
  if ($LASTEXITCODE -eq 0) { break }
  Start-Sleep -Seconds 6
}
```

**✅ 推荐做法：把「清变量 + 直连 + 代理回退」写进同一条命令**，一次跑完别再手试。
直连时通时不通（实测同一台机器上，上午直连成功、下午直连稳定失败而代理成功），
所以两者都留着、按顺序自动回退最省事：
```powershell
$out = "$env:TEMP\wb-push.txt"
$tok = (gh auth token 2>&1 | Out-String).Trim()
$url = "https://x-access-token:$tok@github.com/<owner>/<repo>.git"

# ① 直连：清空被注入的陈旧代理变量（必须与 git 同一条命令）
$env:HTTP_PROXY=""; $env:HTTPS_PROXY=""; $env:ALL_PROXY=""
for ($i=1; $i -le 3; $i++) {
  git -c credential.helper= -c http.version=HTTP/1.1 push $url main:main 2>&1 | Out-File $out -Append -Encoding utf8
  if ($LASTEXITCODE -eq 0) { "DIRECT_OK attempt=$i" | Out-File $out -Append -Encoding utf8; break }
  Start-Sleep -Seconds 5
}

# ② 直连没成功 → 换可用代理（本机实测 10808）
if ($LASTEXITCODE -ne 0) {
  $env:HTTP_PROXY="http://127.0.0.1:10808"
  $env:HTTPS_PROXY="http://127.0.0.1:10808"
  for ($i=1; $i -le 5; $i++) {
    git -c credential.helper= -c http.version=HTTP/1.1 push $url main:main 2>&1 | Out-File $out -Append -Encoding utf8
    if ($LASTEXITCODE -eq 0) { "PROXY_OK attempt=$i" | Out-File $out -Append -Encoding utf8; break }
    Start-Sleep -Seconds 6
  }
}

# ③ 用 API 的真实 sha 判定结果（不要相信 git status / git rev-parse 的 remote ref）
$env:HTTP_PROXY=""; $env:HTTPS_PROXY=""; $env:ALL_PROXY=""
$api = (gh api repos/<owner>/<repo>/commits/main --jq .sha 2>&1 | Out-String).Trim()
"HEAD = $(git rev-parse HEAD)" | Out-File $out -Append -Encoding utf8
"api  = $api"                    | Out-File $out -Append -Encoding utf8
"match= $($api -eq (git rev-parse HEAD))" | Out-File $out -Append -Encoding utf8
```
> 实测：清空变量后直连**失败**，紧接着切 10808 代理**第 1 次即成功**（`57ca5f5..cc2d64c main -> main`）。
> 判定一定要落在 ③ 的 `match=True` 上 —— 这个仓库的 `origin/main` 引用天生不更新（见坑 R）。

**先判定"到底是谁推不动"再选路线**：`gh` 通但 `git` 不通 ⇒ 是 git 传输层 / 代理变量问题（走上面）；
`gh` 也不通 ⇒ 才是真的网络断了，此时别硬推，先报告用户。

---

#### 🔥🔥 最终定位：`-c http.sslBackend=openssl`（**先试这一条，能省掉上面全部折腾**）

清空 `HTTP_PROXY`/`HTTPS_PROXY` 之后，`git push` **依然**报
`CONNECT tunnel failed, response 502` —— 关键线索：**它仍在使用某个代理**，
只是这个代理不来自环境变量。那是 **Windows 系统代理（WinINET 设置）**：
git-for-windows 默认的 `schannel` 后端会把 libcurl 接到系统代理上，
所以你在 shell 里怎么清环境变量都没用。

同一条命令里加一个开关，**立刻就好**：

```powershell
git -c credential.helper= -c http.version=HTTP/1.1 -c http.sslBackend=openssl push $url main:main
```

实测（同一台机器、同一条命令、前后只差这一个参数）：

| 配置 | 结果 |
|---|---|
| 清空环境变量 + HTTP/1.1 | `CONNECT tunnel failed, response 502` × 4（**仍在走系统代理**） |
| 清空环境变量 + HTTP/1.1 + `http.sslBackend=openssl` | **第 1 次即成功** `d8cee9d..bd0587c main -> main` |

一次切换同时解决两件事：**① 绕开 schannel 的 TLS 握手失败**（`failed to receive handshake`）；
**② 让 libcurl 不再自动套用系统代理**。

**判定网络本身是否通**（决定是否值得重试）：
```powershell
$env:HTTP_PROXY=""; $env:HTTPS_PROXY=""; $env:ALL_PROXY=""
curl.exe -s -o NUL -w "direct -> %{http_code}`n" --max-time 10 "https://api.github.com"
curl.exe -s -o NUL -w "proxy  -> %{http_code}`n" --max-time 10 -x "http://127.0.0.1:10808" "https://api.github.com"
```
> 实测遇到过：`curl` 直连与走代理**双双 200**，而 `git push` 连续 8 次 502 ——
> **所以别用 curl 的结果推断 git 能不能推**，两者走的 TLS/代理路径不同。
> curl 只用来确认"机器有网、代理端口活着"。

**推荐顺序（每次推送都照这个来）**：
1. `-c http.sslBackend=openssl` 直接推（多半一次成）；
2. 不成再叠 HTTP/1.1 + 短重试；
3. 再不成才去实测端口、切代理；
4. 最后用 `gh api .../commits/main --jq .sha` 核对。

**每一类 push 都要各自做一遍双路回退，不要把结果存进变量跨命令复用** ——
实测踩过：在同一条命令里先推 `main`（直连第 2 次成功）再推 `tag`，结果 `main` 成功、
**`tag` 五连败**，因为 `$LASTEXITCODE` 的语义在两种失败形态（`Empty reply from server` /
`CONNECT tunnel failed, response 502`）之间反复，用来判断"是否还需要走代理"并不可靠。
**稳妥写法**：把 `main` 和 `tag` 各自包一层完整的「清变量直连 N 次 → 不通则切代理 N 次」，
或者干脆分两条命令跑。推完**必须各查一次**：
```powershell
gh api repos/<owner>/<repo>/commits/main --jq .sha                              # main 是否到位
gh api repos/<owner>/<repo>/git/refs/tags/v5.2.8 --jq .object.sha               # tag 是否到位
```
> 注意：`git tag -a` 推上去的 tag 是**附注标签**，`git/refs/tags/...` 返回的是**标签对象**的 SHA，
> 与提交 SHA 不同 —— 别拿它和 `git rev-parse HEAD` 直接比对而误判成"没推上去"。
> 要比就比 `git rev-list -n1 <tag>`（解引用后的提交）。

**标签没推上去 = 不触发任何 CI**，Release 会静静地不出现。所以**推完 tag 一定要确认 CI 跑起来了**
（`gh run list --limit 3` 里能看到 `headBranch = vX.Y.Z`），别只看 `main` 推成功就以为完事。

**兜底方案：`gh api` 可以直接改远端文件**（当 git 完全推不动、但 `gh` 通时）。
往 `contents` 端点 PUT 即可，无需 git 传输：
```powershell
$b64  = [Convert]::ToBase64String([IO.File]::ReadAllBytes($localPath))
$sha  = (gh api "repos/$repo/contents/$apiPath?ref=main" --jq .sha).Trim()
$body = @{ message="..."; content=$b64; sha=$sha; branch="main" } | ConvertTo-Json -Compress
[IO.File]::WriteAllText($bf, $body, (New-Object System.Text.UTF8Encoding $false))  # 别写 BOM
gh api -X PUT "repos/$repo/contents/$apiPath" --input $bf
```
⚠️ 用这条路改完文件后，**本地会和远端分叉**（同一内容两个不同 commit），
下次操作前务必按坑 R 的流程用 API 的 SHA 同步本地，**不要**直接 `reset --hard origin/main`。

⚠️ **别再尝试** `-c http.proxy=`（空值强制直连）——那不会让 git 忽略环境变量，通常还是失败。
要绕开环境变量就**显式清空变量**。

### 坑 R：本地 `git status` 误报 `ahead N` —— **且 `reset --hard origin/main` 会毁掉工作树**

**根因**：用"URL 内嵌 token"等非 origin 地址推送时，本地 remote-tracking ref（`origin/main`）不会更新，
会**长期停留在很久以前的提交**（实测停在 `v5.2.0`，而远端已经是 `v5.2.5`）。

**浅层症状**：`git status` 误报 `ahead N`。
**解法**：**以 API 为准**：`gh api repos/<owner>/<repo>/commits/main --jq .sha` 对比 `git rev-parse HEAD`。

**⚠️⚠️ 真正的危险（本条是本 skill 里最危险的一个坑）**：
用 URL-token 推过几次之后，如果习惯性地执行
```
git reset --hard origin/main      # ☠️ 会回退到几周前的旧提交
```
就会把 HEAD 拉回那个**陈旧**的 remote-tracking ref，**工作树里新版本的文件被成批删除**
（实测一次删掉 53 个文件，刚发布的源码全没了）。而且该命令**不会**报错，看起来很正常。

**铁律：任何 `git reset --hard origin/...` 之前，先核对 ref 与 API 是否一致。**
```powershell
$api = (gh api repos/<owner>/<repo>/commits/main --jq .sha).Trim()
$ref = (git rev-parse refs/remotes/origin/main).Trim()
"api=$api ref=$ref"          # 不一致 → 绝对不要 reset
```
不一致时，正确做法是**按 API 给出的 SHA 操作**，而不是按 ref：
```powershell
git fetch origin $api            # 需要时按 SHA 取
git reset --hard $api            # 目标明确，不会踩到陈旧 ref
git update-ref refs/remotes/origin/main $api   # 顺手修好陈旧的 remote-tracking ref
```

**为什么 `git fetch` 之后 ref 还是旧的**：实测 `git fetch origin main` 打印了
`f41e3c9..6aa996e  main -> origin/main`，但随后 `git rev-parse refs/remotes/origin/main`
**依然是 `f41e3c9`**（packed-refs 与 loose ref 不一致时会出现这种"消息说更新了、实际没更新"）。
**所以：不要相信 fetch 的那行输出，只相信 `git rev-parse`。**

**误踩之后的恢复**（本次 100% 无损恢复，因为提交对象都还在本地 object DB）：
```powershell
git reflog -8                     # 确认是被 reset 拉到哪一步，找到丢失的提交
git reset --hard <正确的提交SHA>   # 例：6aa996e
git update-ref refs/remotes/origin/main <SHA>
git status --short                # 必须干净
```
**恢复后一定要重新 build + 跑全量测试**，确认文件真的齐了（本次 268/268 通过即证明无损）。
另外：**已推送的提交/标签/Release 不受本地误操作影响**，先在远端确认发布完好再决定怎么修本地。

**预防**：不要在同一个仓库里混用两种推送方式。要么全程用 `origin`（先 `gh auth setup-git`），
要么全程 URL-token 并在**每次**操作后用 API 核对，绝不依赖 `origin/*` 来判断"本地和远端谁新"。

### 坑 S：CI 测试"本地绿、CI 红"（**时区 / 文化差异，必读**）
**现象**：本地 `dotnet test` 全过，推上去 CI 四平台全挂。CI runner 是 **UTC + 英文/invariant culture**，本机常是 **UTC+8 + 中文 culture**。

**两类必自查的脆弱断言**：

1. **「固定日期 + 挂钟时刻」组合**。❌ 反例：给任务设 `2026-09-20` 的提醒，却断言"提前一天档位已记为已推"——隐含要求当前时刻已过 `09-19 17:00`（触发点 + 60 分钟长档位宽限）。本机 19:34 成立，CI 跑在 UTC 11:34 不成立 → 挂。
   ✅ 正解：用**相对当下的过去/未来日期**，如 `DateOnly.FromDateTime(DateTime.Now).AddDays(-3)`，任何时区任何时刻都成立。
2. **对中文/本地化字符串 `OrderBy` 后断言整体序列**。❌ 反例：`titles.OrderBy(t => t)` 再 `Assert.Equal(["周日","周一"], ...)`——ICU 排序 CI 得 `周一/周日`、本机得 `周日/周一`。
   ✅ 正解：顺序无关断言——`Assert.Equal(2, len)` + `Assert.Contains("周一", titles)`。

**复现手段**：`$env:TZ="UTC"` 后再跑测试（Windows 上 `TZ` 不一定生效，最可靠的是**把断言的时刻/顺序依赖彻底消除**）。

**推论**：**推送前必须本地跑一遍测试**——CI 四平台都跑测试，测试挂了 = 整个 Release 不产出。

#### 坑 S-2：同一颗「定时炸弹」的第二种形态 —— 它在当天某个时刻之后必然自爆

坑 S 讲的是「CI 时区不同导致本地绿、CI 红」。但还有一类更阴的：**本地也会在某个时刻之后突然变红**，
而且**跟时区无关**。同一个仓库里它出现过两次（第二次真的在真实运行中爆了）：

| 形态 | 反例 | 什么时候爆 |
|---|---|---|
| ① 固定日期 + 挂钟时刻组合 | 断言"提前一天档位已记为已推" | 当天 17:00 之前成立、之后不成立 |
| ② 固定日期 + 该日已过的时刻 | `date:"2026-09-20", time:"14:30"` 后断言 `isOverdue == false` | **当天 14:30 之后**必然失败 |

形态 ② 的第二次翻车记录：`McpServerTests` 里写死 `2026-09-20 14:30` 并断言"未逾期" ——
本来是绿的，跑到当天 15:58 就红了（任务过了时刻自然成了逾期）。

**识别口诀**：断言里只要同时出现「**写死的日期**」和「**与"现在"有关的概念**」
（逾期 / 已推 / 待办 / 今天 / 剩余），就是一颗定时炸弹 —— 哪怕它现在绿灯。

**解法**：一律用**相对今天的日期**，并让"与现在有关"的那一维**天然落在安全侧**：
```csharp
var date = DateOnly.FromDateTime(DateTime.Now).AddDays(45);   // 永远在未来 → 永远不会"逾期"
var date = DateOnly.FromDateTime(DateTime.Now).AddDays(-3);   // 永远在过去 → 永远算"已过期"
```
**别用 `AddDays(1)` 这种"擦着边界"的偏移** —— 跨天、跨时区、夏令时都可能把它推到错误一侧。
往"远"了取（±30~45 天），比精确贴近边界更稳。

**自查命令**（找写死日期的断言）：
```powershell
Select-String -Path MicaAgenda.Tests\*.cs -Pattern '20\d\d-\d\d-\d\d'
```
逐条问自己：这条断言在**那一年的那一天的某个时刻之后**还会成立吗？

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

### 坑 V：窗口缩小时工具栏按钮之间裂开大空白

**现象**：把窗口拖窄、工具栏按钮被迫换到第二行后，按钮**同时缩小**、**中间却出现几块很大的空白**。
用户描述通常是"看起来非常奇怪"。

**根因**：工具栏容器用了 `ColumnDefinitions="Auto,*,Auto,*,Auto,..."` 这种**交替弹性列**，
本意是"换行后让按钮均匀铺满整行、右边缘与下方内容区对齐"。
但弹性列吸收的是**全部剩余宽度** —— 窗口越窄、按钮越小，剩余宽度反而被均摊成越夸张的空洞。
这类"均摊"布局只在窗口足够宽时好看。

**自检**：搜工具栏容器的 `Grid.ColumnDefinitions`，只要出现 `Auto` 与 `*` 交替，且按钮数量 ≥ 4，
就基本会中招。

**修法**：改成**水平 `StackPanel` + 固定 `Spacing`**，去掉按钮的 `Grid.Column` 归属，整组明确对齐
（左 / 右 / 居中，按设计意图定）。间距可随"紧凑档"一起收紧（如 6 → 4 → 3），
既铺不满也不会裂开。**不要**再用"按像素反算间隙"那套 —— 手算在 `SizeChanged` 那一轮里量不准，会偶发对不齐。

**⚠️ 别把两件事混为一谈**。"按钮随窗口缩小"（防遮挡）和"按钮等距铺开"（对齐美观）是两个独立需求。
用户当初要求缩小，是为了窄窗下按钮**不被遮挡**；改布局时必须把防遮挡这条保住：
仍按「正常 → 紧凑 → 超紧凑」逐级收紧优先挤进一行，实在放不下才换行，且窗口要有硬性 `MinWidth`。
改动后**不要**动测量逻辑 —— 若适配判定基于 `MeasurePanelWidth`（真量 `DesiredSize`），
它换成 `StackPanel` 后依然成立（`StackPanel` 的自然宽度已含 `Spacing`），无需修改。

### 坑 W：`macos-15` 上 `hdiutil: create failed - Resource busy`（平台瞬时故障，优先重跑）

**现象**：CI 四平台里**只有 `macos-15`（arm64）** 挂在"打包（macOS / .app + dmg + zip）"步骤，
日志仅两行：`hdiutil: create failed - Resource busy` + `##[error]Process completed with exit code 1`。
同一脚本、同一 `-format UDZO` 参数在 `macos-15-intel` 上**完全正常**。

**极易误判的点**：
- 报错是 `Resource busy`，看着像"磁盘/镜像被占用"，于是去查 `mount` / `diskutil` / 上一次 `hdiutil attach` 没卸载。
- 实际 `package-macos.sh` 里这一步之前只有 `cp` / `sips` / `iconutil` / `cat > Info.plist`，**没有任何 mount 操作**，
  且 `-verbose` 日志显示 `hdiutil create` 是本步骤**第一条**命令 —— 不存在"前一条命令污染设备"。
- 真因：**runner 可用磁盘空间不足**。`hdiutil create` 会先在**同一个数据卷**（`/System/Volumes/Data`）展开一份
  **未压缩的临时镜像**（约等于 `.app` 体积）。空间不够时 hdiutil 抛出的就是 `Resource busy` 这种**字面无关**的错误。
  `macos-15` 与 `macos-15-intel` 是不同镜像/不同租户，磁盘余量各自漂移，所以"同一提交一个平台挂一个平台过"。

**定位套路（真要用探针时）**：临时加一个 `workflow_dispatch` 探针 workflow，逐层排除：
1. 探针开头必打环境：`sw_vers`、`df -h`、`hdiutil info | head -60`、`diskutil list`、`mount | grep disk`。
   **`df -h` 里 `/System/Volumes/Data` 的 Avail 就是判据**（健康时 ~40Gi+，紧张时显著变小）。
2. 分级试：空目录 → 小文件（4MB）→ `-format UDIF` 不压缩 → 伪 `.app`（~100MB 二进制 + 数百个 dll 共 ~174MB）。
   任一失败就能确认是内容/体积相关，全过则基本锁定平台侧。
3. 探针跑完**必须删掉**，别把诊断 workflow 留在仓库。

**处置（按优先级）**：
1. **直接 `gh run rerun --failed <run-id>`** —— 复用同一 commit / tag，不重新打 tag、不改代码。
   平台瞬时故障重跑一次通常就过（本次 job 重跑 53s 绿）。**这是首选，别急着改脚本。**
2. 若重跑仍挂：在打包步骤前加一步清空间（`sudo rm -rf ~/Library/Developer/Xcode/DerivedData`、
   清 `~/Library/Caches`、`docker system prune -af` 等），或把 `-srcfolder` 换成先 `hdiutil create -size` 定量 + `attach` + `cp` + `detach` 的两段式（可指定更小的镜像尺寸，但更复杂）。
3. 不要为了绕过它把 macOS 产物从 `.dmg` 降级成只有 `.zip` —— dmg 是用户预期的安装形态。

### 坑 X：修了「写入端 + 读取端」，却漏了中间被抹掉的数据（**状态类 bug 必读**）

**现象**：用户报告一个"状态没被记住"的问题（窗口位置、视图、上次选择……），
你补上了**保存**和**读取**两处逻辑、也发了版，用户升级后说**依然复现**。

**真实案例**：桌面日历「重启后窗口位置记不住」修过两轮。
第一轮补了「启动时读回位置」+「关机前落盘」（两端），看起来完整；
第二轮用户反馈"依旧没有记住，现在默认打开就在左上角"。
根因在中间：`SaveCurrentViewBounds` 存**分视图**记忆时写了
`s.WeekWindowBounds = b with { Left = 0, Top = 0 }` —— 用「把坐标抹成 0」来暗示
"分视图只记尺寸、不记位置"；而读取端 `GetStartupBounds` 把整条记录**当完整边界**用。
于是凡"当前视图有分视图记忆"，启动坐标必然是 `(0,0)`，原地 Clamp 后就是屏幕左上角。

**两个可复用教训**：

1. **诊断顺序：先读磁盘上的状态文件，再读代码。** 这次 10 分钟定位，
   靠的是直接看 `%APPDATA%\<App>\calendar-data.json`：
   ```
   windowBounds = {Left:1070, Top:21, ...}   ← 正确
   weekWindowBounds = {Left:0, Top:0, ...}   ← 当前视图，坐标被抹了
   viewMode = Week
   ```
   一眼就锁定了"当前视图的那条记录坐标是 0"。**状态类 bug 先看落盘数据，
   比通读调用链快一个数量级**；而且能顺带看出"宽高一致、只有坐标不同"这种
   "同一次写入里被针对性抹掉"的铁证。

2. **不要用「抹掉字段」来表达语义。** 想让某个字段"不参与"，应该在**读取端**忽略它，
   或者在类型上就把它去掉；在写入端把它写成 0 / 空 / 缺省值，是给下一个人埋雷 ——
   两份数据各说各话，迟早打架。**语义越隐晦，回归越晚被发现。**

**修法模板（本次用的）**：
- 把规则收敛成一个**纯函数**并加单测（`WindowBoundsResolver.ForStartup(settings, mode, fallback)`），
  明确写下不变式（"位置唯一来源 = 通用记忆；分视图只贡献尺寸"）。
- 多宿主项目（如 Avalonia + WPF 两套 UI）**必须共用这一份**：本例两个宿主各写了一份，
  于是**同一个坑两边一起踩**。收敛成一处 + 单测，是防复发的关键。
- **让老数据自愈**：读取端不再读那个被抹的字段 → 用户机器上已存坏的配置被自动绕过，
  **不需要让用户清配置**。修状态类 bug 时优先选"兼容旧脏数据"的方案。
- 顺带把写入端也改正确（存真实值），但**不要依赖它来修老数据**。

**回归测试要复刻用户的真实数据**：直接拿磁盘上那份 `1070,21` + 分视图 `0,0` 当用例输入，
断言结果仍是 `1070,21`。比构造"理想输入"有意义得多。

### 坑 Y：半透明叠层「颜色跟主题不统一」——多半是相对亮度的方向错了

**现象**：用户说某块面板"颜色和整体主题不统一"。具体描述通常带方向，很有信息量：
"用暗色磨砂主题时这块**特别黑**，换成亮色主题它就是**白色**的"。

**根因不是色相，而是亮度方向**。半透明面板叠在窗口那层磨砂底色之上，
而**磨砂会把壁纸变亮** —— 所以窗口实际观感比它的设定色亮。
如果面板选了"比窗口设定色更暗、且不透明度很高（如 225）"的颜色，
叠上去等于在已经很亮的磨砂层上再压一层黑 → 观感是一块与主题无关的黑洞。

真实案例：深色分支把面板写死 `Argb(225, 30, 36, 46)`；
而浅色分支用的是"外壳 100 号色 → 面板升到 50 号色"（**更亮一档**）—— 浅色因此没问题。
**两个分支违反了同一条规则，只有深色露馅。**

**修法**：确立跨模式不变式并写进注释 ——
**「面板永远比窗口底色更亮一档」**（浅色主题往亮处升色阶，深色主题同理，不是往暗处走）。
深色面板改用主题里既有的那档深色面色（与日期格子 / 分组 / 悬停同色），
并把不透明度降到"刚好够文字清晰、还能透出底层主题"的程度（本例 225 → 150）。

**自检清单**（改配色时逐条过）：
1. 这个值是**写死**的，还是从该模式的色系派生的？写死的高不透明度值最容易在换主题时露馅。
2. 它比窗口底色**更亮还是更暗**？方向符合"容器浮起"的直觉吗？
3. 浅色 / 深色**两个分支的规则一致**吗？（本例就是一边对一边错）
4. 面板里的子元素（任务胶囊等）还**能不能从中区分出来**？别把两者调成同一个色。

**多宿主同样要同步**：WPF 宿主里有一份一模一样的硬编码，一并改掉。

> 如果这类问题已经反复出现（改一个又冒一个），别再逐个 hand-tune 颜色 —— 见下面的 **坑 Z**。

### 坑 Z：主题/配色系统 —— 把「观感」变成可测的不变式

**何时适用**：用户连续反馈多条配色问题（"不是白的就是黑的""这两个主题没区别""这个颜色一点也不X"
"文字看不清""透明度强度不高"），或者主题数量要扩张。**逐条手改颜色是治不住的** ——
改完 A 就碰坏 B，加个新主题又会退化回去。

**根因模式（几乎总是同一个）**：
1. **主题的身份没有被表达出来。** 每个主题只是"一堆手调数值"，没有"我是谁"这个声明。
   于是所有深色主题共用同一套格子色，只在窗口底色上差几个 RGB（本例 `(28,31,36)` vs `(17,24,39)`）。
2. **两个宿主各写一份按模式 switch 的调色板**，加主题要改两处，而且**没法写测试**。
3. **文字色是固定值**，不随主题的底色亮度走 → 压在彩色/中灰底上对比不足。
4. **数值写死而不是相对派生** → 半透明叠层里"容器该比窗口更亮"这类关系会不成立。

**修法（本例的做法，可直接照搬）**：
- **配色收敛成 Core 里唯一一份定义**。每个主题只声明两件事：**基色** + **上色浓度**，
  其余全部派生。于是"每个主题有自己的色调"成为结构性保证，而不是靠逐处 tuning。
  （本次：`MicaAgenda.Core/Models/ThemeCatalog.cs`，14 个主题，两个宿主只做
  「角色 → XAML 资源键」的搬运，**不再有任何手调数值**。）
- 枚举新值**一律追加在末尾**（避免已有值位移）；历史值让 catalog 内部映射到现役主题。
- **层次用相对量**：深色里 `Surface(lift) => Mix(base, deep, ShellMix - lift)`。
  写死固定值的话，浓度低的主题会出现"面板比窗口还暗"（就是坑 Y 那个黑洞）。
- **下拉列表由 catalog 生成**，把 XAML 里手写的选项清单删掉 —— 否则"清单"和"实现"必然漂移。
- **alpha 不要逐模式截断**。本例写着 `Math.Min(alpha, 210)` / `Math.Min(alpha, 150)`，
  滑杆拉到头只有 82% / 58%，用户反馈"强度不高"—— 是代码限死的。

**⚠️ 核心：把观感需求写成测试（这次真正的价值）**
本例的 `ThemeCatalogTests` 16 条用例，**在编写过程中真的拦下了 3 处问题**：

| 不变式 | 怎么测 | 实现方式 |
|---|---|---|
| 主题之间要能分辨 | 同明暗组内任意两主题**底色 RGB 欧氏距离** ≥ 阈值（本例 ≥ 28，深色间 ≥ 30） | 第一版把两个暖色做成 Δ=12.7、Δ=19.4，**测试直接报红**，逼我重挑基色 |
| 文字要看得清 | **WCAG 对比度**：正文 ≥ 7:1、次要文字 ≥ 4.5:1（**含直接压在窗口底色上的顶栏那一层**） | 抓出「4.30:1」和「3.65:1」两处；前者加深次要文字、后者提高该主题浓度 |
| 透明的主题要真透明 | Shell / Cell 的 `A == 0` | 老版本 Win10 上仍请求亚克力，界面是白雾 |
| 滑杆要有强度 | `opacity=1.0` 时 `Shell.A == 255`、0 时≈0、且随值单调 | 直接锁死"不许再有截断" |
| 容器的浮起方向 | 面板亮度 > 窗口底色亮度（把坑 Y 也变成测试） | 防止黑洞回归 |
| 暖色要真的暖 | 暖色主题 `R > B` | 防止"加了暖色但看不出来" |

另有：主题数下限、不含历史值、名字唯一、每个枚举值都能解析、工具函数本身的标准值校验。

**设置阈值的两条经验**：
- **留余量**。本例把阈值定为 Δ≥28 而实际最小是 31.3 —— 一改就红的测试会被无视。
- **测试要能给出可读的失败信息**（打印两个颜色与差值），否则只会看到"assert false"，没法调。

**多宿主项目的额外好处**：收敛到 Core 后，Avalonia / WPF 不可能再各呈现一套观感，
而且**配色第一次变得可以单测**。

### 坑 AA：推送给用户的消息里混进了「源码样式」

**现象**：用户收到 IM 推送后说"内容看不懂 / 像源码"。实际看到的是 `**粗体**`、`# 标题`、
`- 列表`、反引号，或者一条任务被换行撑成两行、把 `1. 2. 3.` 的编号结构搞坏。

**根因**：推送内容里嵌了**用户可控文本**（任务标题 / 备注 / 自定义字段），
而这些东西**支持 Markdown**。把标题原样拼进消息里，记号就跟着出去了。

**关键判断依据 —— 看发送时的消息类型**：

| 发送用的类型 | 该怎么处理 |
|---|---|
| **纯文本**（飞书 `msg_type=text` / 企微 `msgtype=text`） | **必须**把内嵌文本剥成纯文本（去强调记号、标题号、列表号、链接），并压成单行 |
| **Markdown**（飞书 `lark_md` / 企微 `markdown`） | 可以不剥，但要 **escape** 内嵌文本里的结构字符（`\` `#` `-` 等），否则用户内容会改写你的排版 |

⚠️ **同一个仓库里两种类型可能并存**（本项目就是：报告走 markdown 且已 escape，提醒走纯文本却直接拼接）
—— 所以**不能因为"报告那边没问题"就以为全线没问题**，要**逐条推送路径**查。

**修法模板（本轮做法）**：
1. 先把文案从服务类里**抽成 Core 中的纯函数 builder**（如 `ReminderTextBuilder`），
   服务只负责取数、发送、标记状态。
2. 内嵌文本统一走「Markdown → 纯文本」的转换；需要放进列表行的再压成**单行**
   （否则多行标题会破坏编号结构）。
3. 空文本给占位（如 `(无标题)`），避免出现 `1. ⬜` 这种没内容的行。
4. 写测试**断言"不许出现"**：
   ```csharp
   Assert.DoesNotContain("**", text);
   Assert.DoesNotContain("#", text);
   Assert.Contains("第一行 第二行", text);   // 换行已被压成空格
   ```
   这一条比"看起来对"可靠得多 —— 首轮就是这么发现有两处标题没剥干净的。

**顺带复查两点**（都是本轮真实踩到的）：
- **早退条件会把新分支一起吞掉**。原实现「当天没任务 → 直接 return 并记已推送」，
  于是新加的"逾期欠账"分支永远走不到。**加分支时回头检查早退条件**。
- **多个"已推送"标记共用一个状态文件时，必须整文件一次写回**。分别写的话后一次会覆盖前一次，
  结果就是"早上推过汇总、晚上又推一遍"。改成一次 `Serialize` 全部字段。

**哪些路径要查（清单）**：定时提醒 / 逾期预警 / 定时报告 / 自动备份 / 任何自带 webhook 的通知。

---

### 坑 AB：一份 Markdown 喂给两个**方言不同**的渠道（**推送类 bug 必读**）

**现象**：修完坑 AA 之后，用户又说"推送的卡片里**还是** Markdown 语法"。截图里长这样：

```
# 桌面日历 · 周报          ← # 原样露出
**统计区间**：…            ← 但这个是【渲染成粗体】的
## 📊 总览                 ← 原样露出
> 期间到期 **0** 项         ← > 原样露出，**0** 却是粗体
- 考前练车（09/21） — …     ← - 原样露出
```

**这条截图信息量极大 —— 先看"哪些记号生效了"**：
`**` 生效、`#` `>` `-` 不生效 ⇒ **不是"忘了剥 Markdown"，而是"用了这个渠道不认的语法"**。
如果全部原样露出，那才是坑 AA（压根没转义）。

**根因**：**飞书卡片的 `lark_md` 不是通用 Markdown，是一个很小的子集。**

| | 企微 `markdown` | 飞书 `lark_md` |
|---|---|---|
| `**加粗**` / `~~删除~~` / `[字](url)` | ✅ | ✅ |
| `#` 标题 / `>` 引用 / `-` 列表 / `---` 分隔线 | ✅ | ❌ **原样显示** |

而工程里通常只产出**一份** markdown 字符串，然后**同一份喂给两个渠道**。加内容的人
根本无从察觉飞书不认什么 —— 这就是结构性缺陷。

**修法（不要只补一次转义）**：把正文收敛成**「方言中立的块序列」**，
块只声明"这是标题 / 引用 / 列表项"，由一层 `Emit(blocks, dialect)` 翻译成各渠道的记号：

```csharp
private abstract record Block;
private sealed record Heading(int Level, string Text) : Block;
private sealed record Quote(string Text)   : Block;
private sealed record Bullet(string Text)  : Block;
private sealed record Gap                  : Block;

// 标题：企微 "# …" / 飞书 "**…**"
// 引用：企微 "> …" / 飞书 直接一行
// 列表：企微 "- …" / 飞书 "• …"（项目符号是普通字符，不依赖渲染）
```

**收益**：两条渠道的**正文内容只可能来自同一个 `BuildBody()`**，差异被压缩到语法翻译一处。
以后加内容不会再踩这个坑。**"内容与语法分离"比"每个渠道各写一份渲染"可靠得多** ——
后者早晚会漂移（本项目两个宿主的主题配色就是这么漂移的，见坑 Z）。

**内嵌标题怎么办**：企微侧沿用 escape；**飞书侧直接剥成纯文本**
（`MarkdownText.ToSingleLine`），**不要**赌 `lark_md` 认不认反斜杠转义 ——
赌错的代价是把用户标题原样加一串 `\`。

**回归测试怎么写**（比肉眼看卡片可靠）：
```csharp
foreach (var raw in feishuMarkdown.Split('\n'))
{
    var line = raw.TrimStart();
    Assert.False(line.StartsWith('#'));
    Assert.False(line.StartsWith('>'));
    Assert.False(line.StartsWith("- "));
}
Assert.Contains("**桌面日历", feishuMarkdown);   // 它认的那部分要留下
```
再加一条**防漂移断言**：两种方言的输出里，每个存在的分组标题都必须出现。

---

### 坑 AC：「拖了几天」这类**相对时间**，口径必须挂在**业务日期**上，不是创建时间

**现象**：报告里写「至今未完成 **4** 项，其中逾期 **0** 项」，同一段却又把四个任务
全标成「已拖 2 天 / 2 天 / 1 天 / 当天新建」。四个任务的到期日是 09/21、09/22、09/22、10/24
（**全在未来**）—— 两句话自相矛盾。

**根因**：`GetPendingDays()` 是**从创建时间**算起的天数（"这个任务挂了多久没动"），
而文案写的是"拖了几天"（相对**应该做完的日子**）。两个指标被当成一个用了。
更隐蔽的是：这个值还**参与了排序**，于是"两个月后到期"的任务被排到"昨天就该做完"的前面。

**修法**：相对时间的措辞挂到**业务日期**上，且**措辞只留一份定义**：
```csharp
public int DueOffsetDays { get; set; }   // today - Task.Date：正=已过期，负=还有几天，0=今天到期

public string DelayText => DueOffsetDays switch
{
    > 0 => $"已拖 {DueOffsetDays} 天",
    < 0 => $"还有 {-DueOffsetDays} 天到期",
    _   => "今天到期"
};
```
然后 **所有**渲染层（纯文本 / 各渠道 markdown / 各宿主的统计窗口）都读这一处，
**不许任何一个地方再拼一遍**。排序改成按 `Date` 升序（最紧急在前）。

⚠️ **口径要与同类功能对齐**：本项目"逾期"的定义是**跨过任务当日的 24:00**
（用户明确指定），也就是 `Date < today`。检查新口径是否与提醒里的 `GetOverdueDays` 同源，
否则两个功能会各说各话。

**识别口诀**：文案里出现「已拖 / 还剩 / 逾期 / 超时 / 多久没…」这类**相对时间**，
先问一句"**相对哪个时间点**"，再去核对它读的字段是不是那个时间点。

---

### 坑 AD：物化数据的封顶挂在「起点日期」上 → 用户看到"只能用 N 条"

**现象**：用户报"周期任务最多只能加 731 个"。

**根因**：周期任务是把未来的实例**提前物化**成普通任务（好处是日历/编辑/提醒/MCP 全复用现有逻辑）。
物化必然要有封顶，当时的写法是 `模板日期 + 730 天` —— 于是**封顶是"一次性"的**：
系列铺满 2 年就永久用完，再也不会产生新实例。

**改法：把封顶从「起点日期」挪到「今天」，并做成滚动的**：
```csharp
var horizon = today.AddDays(MaxMaterializedDays);   // 不是 master.Date.AddDays(...)
```
在**每次启动 + 每次跨天**调一次补齐，只从"该系列已有的最后一天"往后续（天然幂等），
并尊重用户显式设的结束日期（别被地平线顶穿）。

**要点**：
- **幂等是硬要求** —— 启动和跨天都会调，不幂等就会天天翻倍。补完后必须能断言"第二次返回空"。
- **从最后一个实例续推，不要从源任务重算**，否则会产出重复日期的实例。
- 补齐后要 `MarkDirty()`；而且注意**宿主订阅自动保存是在构造之后**，
  构造期的脏标记会被静默丢掉 —— 需要在订阅完成后补一次显式落盘（本项目踩到过）。
- 别为了"真正无限"去改成虚拟实例：那会把日历/编辑/提醒/MCP 全部打上补丁，代价远大于收益。
  **滚动窗口已经给出用户要的效果（系列看起来是无限的）**，只是数据文件有界。

---

### 坑 AE：给标题加"彩色前缀"时，用 `Run` 内联，别并排第二个 `TextBlock`

**需求形态**：任务标题前要有蓝色【周期】/ 红色【已逾期】，而标题本身是正常色 ——
**两种颜色，所以拼不成一个字符串**（必须两个视觉元素）。

**错误做法**：横排 `StackPanel` 里放两个 `TextBlock`（前缀 + 标题）。
后果：`TextTrimming`/`TextWrapping` **只作用于标题那个块**，前缀照样占满宽度，
标题被挤到没有宽度、直接消失或完全不省略。

**正确做法**：一个 `TextBlock`，里面用内联 `Run` 分段着色：
```xml
<TextBlock Classes="taskTitle" TextTrimming="CharacterEllipsis">
  <TextBlock.Inlines>
    <Run Text="{Binding OverduePrefix}" Foreground="{DynamicResource ImportantTaskTextBrush}" />
    <Run Text="{Binding RecurringPrefix}" Foreground="{DynamicResource RecurringBadgeBrush}" />
    <Run Text="{Binding Title}" />
  </TextBlock.Inlines>
</TextBlock>
```
内联之后**省略号与换行按"前缀+标题"整行计算**，行为才对。
Avalonia 与 WPF 都支持（`Run` 上的 `Foreground` 也都能绑 `DynamicResource`）。

**判定"是不是周期任务"要同时看两个字段**：源任务 `Recurrence != None`，
而实例的 `Recurrence` 是 `None`（规则只存在源任务上）、靠 `SeriesId` 指回去。
只判其一，UI 上就会出现"有的周期任务有标识、有的没有"。把判定收敛成一个属性（如 `IsRecurring`），别在调用点各写一遍。

---

### 坑 AF：可滚动长列表只做了**单向**扩展 → 用户翻不回去

**现象**：周视图左栏是"竖排日期"的长列表，用户说"看不到今天之前的日期"。

**根因**：起始点固定（本周周日），而扩展逻辑只有 `Add`（往尾部追加）。
**头部从来不扩展**，所以起点之前的内容永远不存在。

**改法**：加一个对称的"向头部插入"，并**同时补偿滚动偏移**：
```csharp
// 插到头部：其后每行索引 +count，偏移必须 +count×行高，否则画面会跳一整屏
for (var i = 0; i < inserted.Count; i++) VisibleDays.Insert(i, inserted[i]);
WeekScrollHeadPrepended?.Invoke(count);
```

**两个方向都要补偿**，只补一个就会出现"往下滚很顺、往上滚会跳"：
- 头部**裁掉** N 行 → 偏移 **减** N×行高；
- 头部**插入** N 行 → 偏移 **加** N×行高。

⚠️ 偏移赋值会被**当前** `Extent` 夹取，而插入/删除后 `Extent` 还没重算。
"往上补偿"发生在贴近顶部时，加完仍在旧上限之内，所以同步赋值安全；
如果反过来（贴近底部还要往大调），就必须等一帧布局后再赋值。

---

### 坑 AG：观感类需求"照着感觉调"必然来回返工 —— **先把参考量出来**

**现象**：用户发来参考图说"我想达成这种效果"，凭感觉调了一版，用户回"对不上"。

**先做三件取证，再动代码**（本项目实测，每一条都改变了结论）：

1. **读用户当前配置**（`%APPDATA%\<App>\<settings>.json`）。
   实测发现用户 `backgroundMode = None`、`opacity = 0.22` —— **根本没选中那个主题**，
   截图里的"纸感"是**桌面壁纸**透出来的。不查这一步，后面全是白调。
2. **读壁纸文件本身**：`HKCU:\Control Panel\Desktop` 的 `WallPaper` 值 → 直接打开看。
3. **量化参考图**（Python + Pillow，本机已有）：

```python
from PIL import Image, ImageFilter
import statistics
img = Image.open(src).convert("RGB"); W, H = img.size
gray = img.convert("L"); blur = gray.filter(ImageFilter.BoxBlur(2))
gp, bp = list(gray.getdata()), list(blur.getdata())
m = ...  # 均匀网格筛"纯纸"块：局部均亮高 且 块内 sd 小（排除墨迹/文字）
print("底色", 各通道均值)
print("颗粒振幅 sd", statistics.pstdev([gp[i]-bp[i] for i in 采样点]))
print("暗于邻域 >2 级的占比", ...)      # ← 这个比 sd 更直观，直接对应"颗粒有多明显"
```

实测输出（两张参考图）：底色 **(242,237,234)**、颗粒 **sd ≈ 0.7**、暗 2 级以上仅 **1.3%**。
→ 结论：**近白暖米底 + 极细低对比颗粒（±2~3 级）**。这跟"凭感觉调一版"的结果差了好几倍。

**参数怎么换算**：合成后的亮度落差 ≈ `(底色 − 颗粒色) × alpha / 255`。
按实测的 ±2~3 级反推，颗粒 alpha 均值应落在 **20 附近**；调到 80 那种量级就成了"砂纸"。

⚠️ **`Add-Type` 会被沙箱拦截**（"compiles and loads .NET code"），
`Add-Type -AssemblyName System.Drawing` 用不了 —— 图像处理直接走 **Python + Pillow**。

**改观感之前先出预览图给用户确认**：临时 console 工程引用 Core，
用**真实的**主题/图案代码导出用色与素材（纹理图块导成 raw RGBA 即可），
再用 Python 平铺合成 PNG。观感类需求在装包前无法验证，出图能省掉一整轮往返。跑完即删。

---

### 坑 AH：当一条不变式逼着你做"用户明确说不对"的事时 —— **先怀疑不变式测错了对象**

**经过**：给浅色主题加了"任意两者底色色距 ≥ 28"的不变式（它确实抓到过真 bug）。
后来新增"米色纹理"主题，真实纸感的底色 (242,237,234) 与「白雾玻璃」(245,247,249)
**只差 17.6** —— 过不了。为了迁就它，我把纸底改成了偏灰的 (233,230,225)。
用户看到后说"对不上"。

**根因**：**不变式测错了对象**。带纹理主题的辨识度来自**纹理层**，
而"比底色"的测试根本看不见那一层。真实纸感本来就是"极浅的暖白"，
硬拉底色距离只能靠加灰 —— 那正是让它不像纸的原因。

**改法（保留这把刀，但要换个握法）**：
- 先加一个可判定的事实：`ThemeCatalog.IsTextured(mode)`；
- 涉及纹理主题的比较改用**另一条判据与阈值**（如 8，只用来排除"两个主题同一个颜色"）；
- **补"不许滥用豁免"的断言**：纹理主题必须真的 `TextureOpacity > 0`；
  且相对同组其它主题仍须拉开那个最小距离；
- 对比度（文字可读性）的不变式**一条都不放松** —— 那才是真正影响可用性的部分。

**通用规则**：一条断言如果开始逼着你做用户已经明确说过"不对"的事，
不要继续迁就它 —— **去问"它测的是不是我们真正关心的东西"**，
然后把它改准，并补上防止豁免被滥用的断言。

---

### 坑 AI：「材质型」主题的浓度不要直接等于滑块值（否则纸感会被拉没）

**经过**：用户配了「米色纹理」+ 透明度 `0.22`，反馈"它四周都是透明的，
我想要那种浅一点的米色纹理填充进去"。

**根因**：底色浓度**直接等于滑块值**，他那档只剩 22% 的米色；
纹理浓度又被乘了同一个 0.22（`0.72 × 0.22 ≈ 0.16`）—— 两边一起淡掉，
看上去既没有颜色、也没有纹理。这不是 bug，是"滑块语义"与"主题身份"打架。

**判断标准**：**这个主题的身份是不是"一整块材质"？**
纯色/玻璃类主题，淡下去仍然是那个主题（只是更透）；但"纸""布""金属"这类
材质型主题，淡到一定程度就什么都不是了 —— 用户要的是"这张纸"，
把滑块拉到 0 就等于把主题删掉。

**改法**：给主题加**底色下限**（`ThemeDefinition.MinShellOpacity`，默认 0 不影响其它主题）：
```csharp
// 有效浓度 = 滑块与主题下限取大值；底色与纹理必须用**同一个**有效浓度
var effective = Math.Max(Math.Clamp(opacity, 0, 1), Math.Clamp(def.MinShellOpacity, 0, 1));
var alpha = (byte)Math.Clamp(Math.Round(effective * 255), 0, 255);
...
return colors with { TextureOpacity = def.TextureOpacity * effective };
```

⚠️ **别用这两种替代做法**：
- "切到这个主题就自动把滑块拉高"——**悄悄改掉用户的设置**，他下次切回去会莫名其妙；
- "把下限写进滑块控件本身"——等于这个主题永远不能更透。
下限只约束**这一个主题**，且是显式的、可被单测锁住的。

⚠️ **底色与纹理必须共用同一个有效浓度**。两边各算各的会出现
"底色很实、纹理却淡得看不见"这种自相矛盾的效果，而单看任意一边都查不出来。

⚠️ 加了平台（下限）之后，**原有不变式会撞上**，要一起改准而不是删掉：
- "0 不透明度必须几乎全透" → 对有下限的主题**显式跳过**（并写明理由）；
- "单调递增" → 改成"**不递减 + 整段有变化**"（下限以下是一段合法平台，但绝不允许反向）。

---

### 坑 AJ：细噪点纹理在**缩放屏幕**上会被糊掉 —— 必须有粗颗粒层

**经过**：纹理按参考图 1:1 标定，参数没错（底色、振幅都对上了），
但用户装上后还是说"感觉纹理还不够"。

**两个根因，都不是参数问题**：

1. **1×1 的细噪点，在 125%/150% 缩放下会被插值糊成一片均匀的灰。**
   显示器缩放时位图会被重采样，单像素的孤立点恰好是重采样最容易抹掉的东西。
   **必须有 2×2 的粗颗粒层** —— 缩放到 2~3 物理像素后仍然有边界，这才是"看得见"的那一层。
   实现上按 **2 像素网格**铺（奇偶行错开半格，否则会看到规则方阵），
   并让它取色域偏深的一段（`t ≥ 0.4`）、不透明度整体更实。

2. **照片实测值 ≠ 屏幕上该用的值。** 参考照片经过拍摄与压缩，颗粒振幅天然偏小；
   而且照片有自己的物理尺度与观看距离。按同一振幅铺到屏幕上就是看不见。
   → 观感参数**以照片为起点**，但最终要按"在屏幕上是否看得见"来定。

**顺带一个 C# 陷阱**：生成器里的随机数是**结构体**（自定义 LCG，为了跨宿主/跨版本可复现）。
把它**按值**传给辅助方法，内部推进不会回写 → 每颗颗粒都拿到同一个随机值，
整张纸退化成同一种颜色的重复图案。**必须 `ref` 传**：
```csharp
private static RgbaColor Grain(ref Lcg rng, ...)   // 不能省 ref
```

**读法上的教训**：用户说"不够"时，先分清是
①**参数不够重**、还是 ②**根本没渲染出来/被糊掉了**。
这次两者都有：浓度被滑块乘掉了（坑 AI），细颗粒又被缩放糊掉了。

---

### 坑 AK：同一个事实被两套措辞表达 —— 改一处就会漏掉另一处

**经过**：报告里的"已拖 N 天"在 v5.2.8 已改成按**任务日期**算（坑 AC）。当时我看到
任务行的小徽标还在用**创建时间**算的"N 天未完"，判断"语义上不算错"就没动它，并在回复里提了一句。
**两轮之后用户还是回来了**：
"这个提醒好奇怪呀。正常来说没到期就该是'还有几天'，到了就显示今天，超期了就显示已逾期几天。"

**根因**：不是"漏改一处"，是**同一个事实存在两套措辞**（"拖" / "未完"），
它们各自演化、各自被误用。只要两套并存，就必然有一处用错参照物。

**改法（收敛，不是逐处打补丁）**：
1. 把措辞收敛成**唯一一份**纯函数（本仓库是 `TimeText.FormatDueOffset` / `DescribeDueOffset`），
   徽标 / 悬浮提示 / 报告三处渲染 / 两个宿主的统计窗口**全部读它**。
2. 统一用词：口语的"拖"换成与设置界面一致的"逾期"，避免用户以为是两个指标。
3. **删掉**那个语义会误导的旧方法（`GetPendingDays()`）与其在模型上的字段 ——
   留着一个"能算但会被误用"的公开方法，就是下次踩坑的诱因。
   判据：**它已经没有任何生产调用点了**，就删，不要"留着以后可能有用"。

**接口设计上的推论**：同一个事实需要"短 / 长"两种语域时（徽标要短、提示与报告要成句），
让它们**共用同一个 switch**（一个 `offset` 参数），而不是各写一份 —— 并补一条断言
"两种形态对同一个 offset 的判定必须一致（都判逾期 / 都判未到）"。

⚠️ 改措辞会**打红一批旧断言**（`已拖 1 天` vs `已逾期 1 天`）—— 那些不是回归，
是应该同步更新的期望值。别因为"测试红了"就把措辞改回去。

---

### 坑 AL：`HttpClient.Timeout` 不是"多久没响应"，是"整个请求（含读完响应体）"

**现象**：用户反馈"更新包下载慢 / 不稳"。代码看着没问题：`ResponseHeadersRead`、
80KB 缓冲区、`.part` 后改名，都写了。

**根因**：`new HttpClient { Timeout = TimeSpan.FromSeconds(20) }`。
这个 Timeout 覆盖**把响应体读完**的全过程 —— 一个 50MB 的安装包意味着
**平均速度必须跑到 2.5MB/s 才能活下来**，慢一点的链路会在下到一半时被掐断。

**改法**：
```csharp
_http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };   // 大文件不能用整体超时
```
再用**停滞看门狗**接管：连续 N 秒（这里 30s）一个字节都没收到才判失败。
```csharp
// 每收到一段数据就 Kick()；定时器发现超时就 Cancel 链接出去的令牌
if (_cts.IsCancellationRequested) throw new TimeoutException($"连续 {idle} 秒没有收到数据");
```
⚠️ 必须把"卡死"和"用户取消"**分开报** —— 否则两者都走同一个 `OperationCanceledException`
分支，卡死会被当成"用户取消"静默收场，用户完全不知道为什么没更新。

⚠️ 取消整体超时后，**其它短请求要自己补超时**：检查更新这类小请求用
`CancellationTokenSource.CreateLinkedTokenSource(ct)` + `CancelAfter(20s)`，
并区分"用户取消"与"自己超时"两种回执文案。

---

### 坑 AM：大文件走**单连接**最吃亏 —— 分段并发（Range）是最实在的提速

**背景**：用户明确指出"慢主要是因为 GitHub 是国外的服务器"。
这是地理问题，程序改不了 —— 但**单条 TCP 连接**在跨国高丢包链路上很难把带宽跑满
（丢包 + 往返延迟压住窗口），而 GitHub 的 release 文件本来就在 CDN 上、**支持 Range**。

**做法**：
1. 先探一次 `Range: bytes=0-0` —— **同时**拿到真实总长（`Content-Range` 里的 `/total`）
   与"服务器确实支持分段"（必须回 **206**）。
2. 按固定大小切段（12MB / 段，最多 6 段），并发拉。
3. `File.OpenHandle(partial, FileMode.Create, FileAccess.Write, FileShare.ReadWrite|Delete,
   preallocationSize: total)` 预分配，各段用
   `RandomAccess.WriteAsync(handle, buf, offset)` 直接写自己的偏移 —— **不需要拼接**。
4. **任一条不满足就自动退回单流**（太小 / 不支持 Range / 某段回的不是 206），
   绝不为了提速牺牲稳定。

⚠️ **若某段回 200 而不是 206**，说明服务器把 Range 当普通请求、准备把**整个文件**
返给每一个分段 —— 拼出来必然是坏文件，必须当场失败并退回单流，不能"先写进去再说"。

⚠️ **预分配会让"按文件大小校验"恒真**：分段路径的完整性只能靠
"每段必须读满自己那段、读不满就抛"。别以为末尾那句 `actual != expected` 还在兜底。

⚠️ 另外给一个**可选的加速前缀**（`前缀 + 原始地址`）作为兜底 —— 地理问题终究要靠镜像 / 代理。
前缀必须校验 `http(s)://` 开头，**填错就忽略、退回直连**：
宁可不加速，也不能拼出一个必然失败的地址（那会把"下载更新"变成死路，比慢严重得多）。

---

### 坑 AN：Inno Setup `/SILENT` **仍然会显示安装进度窗**

用户要求"安装的时候也不要显示进度"，而参数写的是 `/SILENT`。但 Inno Setup 里：

| 参数 | 行为 |
|---|---|
| `/SILENT` | **不提问**，但**仍显示安装进度窗** |
| `/VERYSILENT` | 完全不显示（配合 `/SUPPRESSMSGBOXES` 压掉提示框） |

要"全程无 UI"必须 `/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /NORESTART`。

**同类推论**：凡是"静默 / 无提示"这类要求，**不要凭参数名字判断行为** ——
去查该工具对这几个档位的准确定义。`SILENT` 与 `VERYSILENT` 只差一个词，
表现却是一个有进度窗、一个什么都没有。

---

### 坑 AO：删掉"进度浮窗"这类组件时，连带清理引用面

用户要求下载全静默后，`UpdateProgressWindow` 整个文件失去引用。
做法：**先 grep 确认零引用**（只剩它自己那两行），再删文件，再编译验证 ——
而不是留一个注释掉的调用、或留一个没人用的窗口类。
判据同上（坑 AK）：**没有生产调用点的东西就删**，留着只会在下次重构时误导人。

---

### 坑 AP：构建期从 GitHub 拉外部二进制 → 会 504，且**不要**当成自己的 bug

**经过**：v5.2.12 首轮 CI 只有 `linux-x64` 挂了：

```
Downloading runtime file from https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-x86_64
Failed to download runtime: server returned status code 504
##[error]Process completed with exit code 1.
```

`appimagetool` 每次打包都会去 GitHub 拉一份 AppImage runtime。**这是外部服务的瞬时故障**，
与本次改动无关（同一轮另外三个平台全绿）。

**处置**：`gh run rerun --failed <run-id>`，一次即过。**先重跑、别改代码**——
这也是坑 W 那条规则的另一个实例，只是失败点从 `hdiutil` 换成了"构建期拉外部二进制"。

⚠️ **识别它**：报错里出现 `status code 5xx` / `Failed to download` 且指向
`github.com/<第三方项目>/releases/...`，就是这一类。此时**不要**去改 `package-linux.sh`，
也不要把 AppImage 降级成只有 deb —— 那是在为别人的瞬时故障做永久性妥协。

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
- CI 单平台失败先 `gh run rerun --failed`（平台瞬时故障优先重跑，别急着改代码）
- 「状态记不住」类问题：**先读磁盘上的状态文件**，再读代码
- 修状态类 bug 时优先让**旧脏数据自愈**（读取端忽略脏字段），别要求用户清配置
- 多处实现的同一规则（多宿主 / 多入口）**收敛成一个带单测的纯函数**
- 视觉/配色类需求也写成**可测的不变式**（色距、WCAG 对比度、alpha 单调性）—— 见坑 Z
- 「材质型」主题的**底色与纹理必须用同一个有效浓度**（各算各的会出现自相矛盾的效果）—— 见坑 AI
- 用户说"效果不够"时，先分清是**参数不够重**、还是**根本没渲染出来 / 被缩放糊掉** —— 见坑 AJ
- 生成器里的随机数是**结构体**时必须 `ref` 传（按值传会让所有颗粒拿到同一个随机值）—— 见坑 AJ
- 同一个事实（"还有几天到期"）只留**一份措辞实现**，所有呈现面读同一处 —— 见坑 AK
- 改措辞时同步更新断言（旧期望值变红不是回归）；有"短 / 长"两种语域就共用同一个判断 —— 见坑 AK
- 大文件下载用**停滞看门狗**而不是整体超时，且"卡死"与"用户取消"要分开报 —— 见坑 AL
- 大文件优先**分段并发**（先探 206，任一条不满足就退回单流），不拿稳定换速度 —— 见坑 AM
- 「静默 / 无提示」类要求要**查工具的档位定义**，不凭参数名猜 —— 见坑 AN
- 组件失去全部生产调用点就**连引用面一起删掉**（grep 确认零引用 → 删 → 编译验证）—— 见坑 AO
- 推送类文案抽成 **Core 里的纯函数 builder**，并断言「不许出现 `**` / `#` / 换行」—— 见坑 AA
- **同一份正文要发往多个渠道时，先把内容收敛成「方言中立的块序列」**，再按渠道翻译语法 —— 见坑 AB
- 加新分支时回头检查**早退条件**（`if (没数据) return;` 很容易吞掉新逻辑）
- **「已拖 / 还剩 / 逾期」这类相对时间必须挂在业务日期上**，且措辞只留一份定义 —— 见坑 AC
- 推送前先看清并**清空代理环境变量**（`$env:HTTP_PROXY=""`），**优先试直连**
- **推送优先带 `-c http.sslBackend=openssl`**（同时绕开 schannel 握手失败与系统代理）—— 见坑 Q
- **每一类 push（`main` / `tag`）各自做一遍双路回退，推完各自查询确认**；tag 推不上 = 不触发 CI
- 物化/缓存类数据的封顶要挂在**今天**上并定期补齐（滚动窗口），补齐必须**幂等** —— 见坑 AD
- 给标题加**彩色前缀**用 `Run` 内联，别并排两个 `TextBlock` —— 见坑 AE
- 可滚动长列表要做**双向**扩展，并**对称补偿**滚动偏移（裁头减、插头加）—— 见坑 AF
- 观感类需求**先量化参考图**再改参数；改完先出**预览图**给用户确认 —— 见坑 AG
- 不变式开始逼你做"用户明确说不对"的事时，**改判据而不是迁就它** —— 见坑 AH

❌ **绝对不能**：
- WPF / WinForms 工程直接往 macOS / Linux 发
- 交叉编译跨平台包
- `dotnet sln` 里漏登记测试工程
- 启用 `PublishTrimmed`
- 删除旧 Release 资产 / 旧 tag
- 把本机绝对路径、Token、个人邮箱写进任何交付文件
- 让安装脚本清理逻辑可能触及 `%APPDATA%` / `%LOCALAPPDATA%`
- 在测试里用「固定日期 + 挂钟时刻」或对中文串 `OrderBy` 后断言整体序列
- 把临时诊断 / 探针 workflow 留在仓库里
- 用「抹掉字段值」表达语义（如把坐标写成 0 表示"不记位置"）—— 请在读取端忽略
- 深色主题里让容器面板比窗口底色更暗（会变成黑洞）
- 逐处手调主题颜色（应声明"基色 + 浓度"并派生；观感不变式要写进单测 —— 见坑 Z）
- 对 alpha / 不透明度写逐模式上限（会把用户能拉到的强度限死）
- 在 XAML 里手写主题下拉清单（应由主题清单生成，否则必然与实现漂移）
- 测试里写死日期后断言任何与"现在"有关的概念（逾期 / 已推 / 待办）—— 见坑 S-2
- 把用户可控文本（标题等）原样拼进**纯文本**推送（会露出 Markdown 源码）—— 见坑 AA
- 把企微方言的 Markdown（`#` / `>` / `-`）直接喂给飞书 `lark_md`（会原样显示）—— 见坑 AB
- 用创建时间冒充业务日期说"拖了几天"（两个指标，用户一眼能看出矛盾）—— 见坑 AC
- **`git reset --hard origin/main`**（remote-tracking ref 可能是陈旧的 → 回退并成批删文件）；
  先用 API 的 SHA 核对，再 `git reset --hard <SHA>`
- 把物化封顶写成"**起点日期** + N 天"（一次性用完，用户看到"只能加 N 条"）—— 见坑 AD
- 用并排两个 `TextBlock` 拼"前缀 + 标题"（会挤掉标题的省略号/换行）—— 见坑 AE
- 只给可滚动长列表做单向扩展（用户翻不回起点之前）—— 见坑 AF
- 观感参数凭感觉定（"我觉得这个深度差不多"）—— 先量参考图 —— 见坑 AG
- 为了让某条不变式变绿而去改用户明确说"不对"的观感 —— 见坑 AH
- 让"材质型"主题（纸 / 布）的底色浓度直接等于滑块值（拉到低档主题就没了）—— 见坑 AI
- 只铺 1×1 细噪点当纹理（缩放屏幕上会被插值糊成均匀灰）—— 必须加 2×2 粗颗粒层 —— 见坑 AJ
- 相信 `git fetch` 打印的 "main -> origin/main" 就当 ref 已更新（只信 `git rev-parse`）
- 同一个事实留下两套措辞（"已拖" / "未完"），或留着一个"能算但语义会误导"的公开方法 —— 见坑 AK
- 给 `HttpClient` 设一个覆盖大文件下载的 `Timeout`（会掐断正常下载）—— 见坑 AL
- 大文件只用单连接下载（跨国链路上最吃亏）—— 见坑 AM
- 把 Inno Setup 的 `/SILENT` 当成"完全不显示界面"（它仍会弹进度窗）—— 见坑 AN
- 留下失去引用的组件 / 注释掉的调用（死代码会在下次重构时误导人）—— 见坑 AO

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
