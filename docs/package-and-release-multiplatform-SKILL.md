---
name: "package-and-release-dotnet-multiplatform"
description: ".NET 桌面应用的多平台打包发布指南（Windows / macOS / Linux 三平台，含 Inno Setup 安装包、.app+dmg、AppImage/deb 与源码 zip）。一条链路完成：全位置版本号升级 → 提交推送 → changelog → GitHub Actions 四路并行构建 → 汇总产物与 GPL 源码包 → 创建或更新 GitHub Release（标 latest）。所有项目参数（仓库、版本号位置、RID 矩阵、产物名）均从项目动态发现，不预设任何具体项目；全文不含任何私密信息，可直接照做。同时给出「当前工程是 WPF，尚不具备跨平台出包能力」这一硬门槛的判定与处理方式。"
---

# .NET 桌面应用 多平台打包发布 指南

## 本指南的用途

对**任何一个** .NET 桌面应用项目，完成一条完整发布链路：

```
升级版本号（全部位置） → git 提交推送 → 写 changelog
   → CI 四路并行构建（各平台在各自 runner 上 publish + 打包）
   → 汇总全部产物 + GPL 源码 zip → 上传 GitHub Release（标 latest）
   → 用户按平台下载安装
```

每一步独立可验证、可重跑。本指南**不预设任何项目的具体值**——仓库地址、版本号位置、产物文件名、支持哪些平台，全部按 Step 0 从项目里动态发现。

> 与 Electron 版的差别（本指南专为 .NET 而写）：
> - 打包工具链是 `dotnet publish -r <RID>` + 平台原生打包器（Inno Setup / hdiutil / appimagetool / dpkg-deb），不是 electron-builder。
> - **UI 框架决定了能否跨平台出包**：`UseWPF` / `UseWindowsForms` 的工程**只能出 Windows 包**，这是编译器层面的硬约束（见 Step 0 前置门槛、坑 A）。
> - 没有应用内自动更新器时，Step 8 的「更新器产物契约」不适用（标注 N/A 即可）。

## 执行原则

1. **一切参数动态发现，禁止假设**：仓库地址从 `git remote get-url origin` 读，版本号位置靠全项目搜索，RID 矩阵从「UI 框架能否跨平台」推，产物名从打包脚本与历史 Release 读。不要凭记忆或相似项目的经验填写具体值。
2. **遇错先诊断根因，再用对应解法**：避坑指南按「现象 → 根因 → 解法」组织，先比对现象再动手。
3. **网络故障不阻塞本地流程**：git push / 上传失败时，本地打包链路（清理 → publish → 打包 → 压缩）应并行推进，网络恢复后统一上传。
4. **能上 CI 就上 CI，绝不本机跨平台交叉编译**：RID 与宿主平台不一致时，`dotnet publish` 会在还原阶段直接报 `NETSDK1082`。macOS 的 dmg（需要 `hdiutil`）与 Linux 的 AppImage/deb（需要 `dpkg-deb`、文件权限位）也**只能在对应系统上生成**。Windows 上不可能产出可用的 mac / Linux 包。
5. **不泄露隐私**：本指南及据此产出的任何文件、日志、交付物，都不得包含本机绝对路径、个人邮箱、访问令牌或仓库私密信息（见「隐私与凭据红线」）。

---

## 隐私与凭据红线（贯穿全程）

| 红线 | 做法 |
|---|---|
| 仓库地址 | 运行时 `git remote get-url origin` 提取 `owner/repo`；**不写进指南 / 脚本 / 提交信息**，指南内一律用 `<owner>/<repo>` 占位 |
| 本机路径 | 不把 `C:\Users\<name>\...`、`/home/<name>/...` 写进任何交付文件；一律用相对路径或 `$HOME` |
| 个人邮箱 | 不写入指南、不写入 csproj 的 `Authors` / `Company` 字段。需要填时用 GitHub noreply 地址（`<id>+<login>@users.noreply.github.com`）或 `git config user.email` 现场取 |
| Token | **CI 只用内置 `GITHUB_TOKEN`**，不新增任何个人 PAT secret。手动兜底时才临时用 `GH_TOKEN`，用完立即 `unset` |
| remote URL 含凭据 | 若 `git remote -v` 输出形如 `https://user:token@github.com/...`，**先把 token 清洗掉**再写入任何文件或汇报文本 |
| 运行时日志 | 应用自身的错误日志（如 `app-error.log`）会写本机绝对路径与进程路径；`.gitignore` 必须覆盖 `*.log`，源码 zip 必须排除 |
| 交付汇报 | 只给仓库 URL 与产物名，不给本机绝对路径（用户在自己机器上时除外） |

---

## Step 0. 从项目发现全部参数（必须先做）

| 参数 | 获取方法 | 说明 |
|---|---|---|
| 项目根目录 | 当前目录含 `*.sln` 或 `*.csproj` | 确认是 .NET 项目 |
| **UI 框架** | 搜 `UseWPF` / `UseWindowsForms` / `Avalonia` 于各 `*.csproj` | **决定平台矩阵**，见下方前置门槛 |
| 目标框架 | 各 csproj 的 `<TargetFramework>` | `net9.0-windows` 说明锁死 Windows；`net9.0` 才可跨平台 |
| 当前版本号 | 项目内搜 `-Setup-<数字>.<数字>.<数字>` / `<Version>` / changelog 顶部 | 见 Step 1 |
| 版本号全部硬编码位置 | 全项目搜索旧版本号（见 Step 1） | 不同项目位置不同，必须搜索确认 |
| GitHub 仓库 | `git remote -v` 的 origin URL | 提取 `owner/repo`；无 remote 则问用户是否要建仓库 |
| 主分支名 | `git branch --show-current` | 可能是 `main` / `master`，不要假设 |
| gh CLI 登录状态 | `gh auth status` | 应显示 `Logged in to github.com account ...` |
| **RID 矩阵** | 由 UI 框架与项目声明支持的平台推导 | 例：`win-x64` / `osx-x64` / `osx-arm64` / `linux-x64` |
| **各平台产物名** | 打包脚本（`.iss` / `.sh`）的输出名模板 | 见 Step 8 的命名契约 |
| 已有打包脚本 | 搜根目录 `*.iss`、`tools/*.sh`、`*.ps1` | 复用，不要另起一套 |
| **CI 工作流** | `.github/workflows/` 是否存在 | 不存在则按 Step 4 创建 |
| 许可证 | 仓库根 `LICENSE` | GPL/AGPL 等 copyleft 许可证**必须**随产物与源码包分发 |
| `.gitignore` 覆盖情况 | 读 `.gitignore` | 必须覆盖 `bin/`、`obj/`、`publish/`、`installer/`、`release/`、`TestResults/`、`*.log`、`*-Source-*.zip` |
| 应用内更新器 | 搜 `AutoUpdater` / `updateChecker` / `api.github.com/repos` | 没有则 Step 8 标 N/A |

### 前置门槛：UI 框架决定你能出几个平台的包

| 工程形态 | Windows | macOS | Linux |
|---|---|---|---|
| `net9.0-windows` + `UseWPF` / `UseWindowsForms` | ✅ | ❌ | ❌ |
| `net9.0` + Avalonia / Uno / Eto / 纯控制台 | ✅ | ✅ | ✅ |

**判定方法**（一条命令即可验证，不要靠猜）：

```bash
# 用一个非 Windows RID 试还原；能过就说明可跨平台，报 NETSDK1082 就是锁死 Windows
dotnet publish <主工程> -c Release -r osx-x64 --self-contained true -o /tmp/probe-rid
```

WPF 工程的报错形如：

```
error NETSDK1082: Microsoft.WindowsDesktop.App 没有运行时包可用于指定的 RuntimeIdentifier"osx-x64"。
```

**这意味着三平台发布必须先完成 UI 层跨平台化**（例如引入 Avalonia 桌面宿主工程 `MicaAgenda.Desktop`，与现有 WPF 宿主并存或替换）。在跨平台宿主落地前，CI 的 macOS / Linux 行必然失败——此时应**只发布 Windows**，不要把未验证的平台行留在流水线里制造红灯。

---

## Step 1. 升级版本号（全部位置，缺一不可）

**原理**：用户判断"有没有新版"靠的是 Release tag / 安装包文件名 / 应用关于页显示的版本。任何一处漏改，都会表现为「装了新版，界面还写着旧版本号」或「同名安装包互相覆盖」。

**找齐所有位置的可靠方法（搜索法，不要靠列举）**：

```bash
OLD=$(gh release view --json tagName -q .tagName | tr -d v)   # 或直接看上一次 Release 的文件名
# 全项目搜索旧版本号字面量
grep -rn "$OLD" --include=*.cs --include=*.csproj --include=*.iss --include=*.md --include=*.html \
  --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git --exclude-dir=publish --exclude-dir=installer .
```

**常见硬编码位置**（作为搜索结果的核对清单，不是全部）：

| 位置 | 形态 |
|---|---|
| `Directory.Build.props` / `*.csproj` | `<Version>X.Y.Z</Version>`（**推荐的单一来源**） |
| 主窗口代码 | `Title = "<App> vX.Y.Z";` |
| 启动日志字面量 | `$"[STARTUP] ... - vX.Y.Z - ..."` |
| 安装脚本 | `#define MyAppVersion "X.Y.Z"` |
| `README.md` | 安装包 / 源码包文件名 `-Setup-X.Y.Z.exe`、`-Source-X.Y.Z.zip` |
| `CHANGELOG.md` | 新增 `## vX.Y.Z（<日期>）` 段（不删历史段） |
| 关于页 / 设置页 | `vX.Y.Z` 文案 |
| 演示页 `docs/index.html` | 页脚版本号（若有） |

**建议：建立单一版本号来源，消除漂移**。做法是加一个 `Directory.Build.props`：

```xml
<Project>
  <PropertyGroup>
    <Version>X.Y.Z</Version>
  </PropertyGroup>
</Project>
```

然后让代码与安装脚本从它派生，而不是各写一遍：

- 应用标题：`Assembly.GetExecutingAssembly().GetName().Version`（去掉 `vX.Y.Z` 字面量）
- 安装包版本：Inno Setup 脚本顶部加兜底，由 CI 用 `/D` 覆盖（见 Step 4.3）

**新版本号怎么定**：常规修复 patch +1；有较大功能新增 minor +1；破坏性改动 major +1。git tag 必须等于 `v{新版本号}`。**拿不准时问用户**。

**验证**：

```bash
grep -rn "$OLD" --include=*.cs --include=*.csproj --include=*.iss --include=*.md --include=*.html \
  --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git --exclude-dir=publish --exclude-dir=installer .
# 应只剩 changelog 历史记录
NEW=<新版本号>
grep -rn "$NEW" --include=*.cs --include=*.csproj --include=*.iss --include=*.md --include=*.html \
  --exclude-dir=bin --exclude-dir=obj --exclude-dir=.git --exclude-dir=publish --exclude-dir=installer .
# 确认所有目标位置已更新
```

---

## Step 2. git 提交并推送

**先确认 .gitignore 覆盖了不该上传的内容**（见 Step 0 清单），用 `git status` 检查有无异常大的待提交文件。若无关文件已被跟踪，用 `git rm --cached <path>` 从 git 移除（保留本地文件）后提交。

**仓库未配置 user.name / user.email 时**，不要改全局配置，用单次内联覆盖：

```bash
git -c user.name="release" -c user.email="release@local" commit -m "vX.Y.Z: <本次改动摘要>"
```

```bash
git status --short                    # 审查改动清单
git add <明确列出本次改动的文件>       # 优先按文件名加，避免 -A 误加（尤其别误加 .env / 凭据）
git commit -m "vX.Y.Z: <本次改动摘要>"
git push
```

**首次推送被拒（fetch first）**：本地是完整正确版本时用 `git push -u origin <主分支> --force`（force push 属破坏性操作，**先征得用户同意**）；远程有需保留的提交时先 `git pull --allow-unrelated-histories` 合并。

**push 失败的处理**：按避坑指南坑 L / 坑 M 诊断（认证失败 / 网络阻断各有解法）。**push 卡住时不要干等**，先做本地验证与打包（Step 4.4 ~ Step 5），最后回来重推。

---

## Step 3. 写 changelog

在 `CHANGELOG.md` **顶部**加一段（不删历史段），CI 会自动截取作为 Release 正文：

```markdown
## vX.Y.Z（YYYY-MM-DD）

### 新增功能
- ...

### 安全加固
- ...

### 修复
- ...
```

**若项目有提取脚本**（搜 `extract-changelog`），CI 中调用它生成正文。**若无**，手写 `release-notes-v<version>.md`（项目根目录，属临时文件，应在 `.gitignore` 里）。

**理由**：`gh release create` 用 `--notes-file` 引用文件，比 `--notes "..."` 内联安全（免处理换行与引号转义）。

---

## Step 4. 配置 CI 多平台构建流水线

### 4.1 工作流骨架（`.github/workflows/release-build.yml`）

```yaml
name: Build & Release

on:
  push:
    tags: ['v*']
  workflow_dispatch:        # 手动触发做「只构建不发布」的干跑验证

permissions:
  contents: write           # 只用内置 GITHUB_TOKEN，不配置任何个人 secret

concurrency:
  group: release-${{ github.ref }}
  cancel-in-progress: false

jobs:
  build:
    name: Build ${{ matrix.label }}
    runs-on: ${{ matrix.os }}
    strategy:
      fail-fast: false      # 一路失败不影响其他平台产出，便于排查
      matrix:
        include:
          - label: windows-x64
            os: windows-latest
            rid: win-x64
            pkg: windows
          - label: macos-x64
            os: macos-15-intel  # Intel macOS；标签会随镜像退役变更，见坑 J
            rid: osx-x64
            pkg: macos
          - label: macos-arm64
            os: macos-15        # Apple Silicon runner
            rid: osx-arm64
            pkg: macos
          - label: linux-x64
            os: ubuntu-22.04
            rid: linux-x64
            pkg: linux
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'

      - name: 还原
        run: dotnet restore <解决方案或主工程>

      # 跨平台核心测试：每个平台都跑，能提前暴露平台差异（路径分隔符 / 编码 / 大小写敏感）
      - name: 测试（跨平台单测）
        run: dotnet test <跨平台测试工程> -c Release --no-restore

      - name: 发布（${{ matrix.rid }}）
        run: >
          dotnet publish <桌面主工程> -c Release
          -r ${{ matrix.rid }} --self-contained true
          -p:PublishSingleFile=false
          -o pkg

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
    if: startsWith(github.ref, 'refs/tags/v')   # 手动触发时不发布，只构建
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '9.0.x'
      - uses: actions/download-artifact@v4
        with:
          path: assets
          merge-multiple: true
      - name: 生成源码 zip
        run: bash tools/make-source-zip.sh   # 或 pwsh / node 版，见 Step 5
      - name: 生成发布说明
        run: bash tools/extract-changelog.sh "${GITHUB_REF_NAME}" > release-notes.md
      - name: 创建或更新 Release
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          TAG="${GITHUB_REF_NAME}"
          ASSETS=(assets/* *-Source-*.zip)
          if gh release view "$TAG" >/dev/null 2>&1; then
            gh release upload "$TAG" "${ASSETS[@]}" --clobber      # 幂等：已存在则覆盖本次产物
            gh release edit "$TAG" --notes-file release-notes.md --latest
          else
            gh release create "$TAG" "${ASSETS[@]}" --title "$TAG" --notes-file release-notes.md --latest
          fi
      - name: 核对发布结果
        env:
          GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: gh release view "${GITHUB_REF_NAME}" --json tagName,isDraft,isPrerelease,assets --jq '{tag:.tagName,draft:.isDraft,pre:.isPrerelease,assets:[.assets[]|{name,size}]}'
```

**要点**：

- `fail-fast: false`：让四路都跑完，一次看清哪个平台有问题
- `--self-contained true`：目标机无需预装 .NET 运行时（体积大但装机最省心）
- **绝不用 `gh release delete`**：旧版本 Release 是用户回滚的依赖，删掉即断链（见坑 K）。用 `upload --clobber` 做幂等更新
- `macos-15-intel` 出 x64、`macos-15` 出 arm64；**不要在 arm64 runner 上出 x64 包**（RID 与宿主不一致会拿到错误的原生依赖）。**runner 标签会随镜像退役变更，见坑 J**
- 若 `.gitignore` 排除了 `installer/`，`upload-artifact` 仍能读到本地文件（它按路径读磁盘，不走 git），无需改 .gitignore
- 干跑验证：`gh workflow run release-build.yml`，此时只有 build job 执行，不产生 Release

### 4.2 平台打包脚本要点

**macOS（`tools/package-macos.sh`）**——`dotnet publish` 只产出可执行文件，`.app` 目录结构要自己拼：

```
<App>.app/Contents/
├── Info.plist          # CFBundleName / CFBundleIdentifier / CFBundleExecutable / CFBundleIconFile / CFBundleShortVersionString
├── MacOS/              # dotnet publish 的产物全部放这里
└── Resources/<icon>.icns
```

```bash
# 关键步骤
mkdir -p "<App>.app/Contents/MacOS" "<App>.app/Contents/Resources"
cp -R pkg/* "<App>.app/Contents/MacOS/"
chmod +x "<App>.app/Contents/MacOS/<App>"
cp build/<icon>.icns "<App>.app/Contents/Resources/"
# Info.plist 里的版本号必须与 tag 一致，否则"关于"页显示旧版本
hdiutil create -volname "<App>" -srcfolder "<App>.app" -ov -format UDZO "dist/<App>-<ver>-<arch>.dmg"
ditto -c -k --sequesterRsrc --keepParent "<App>.app" "dist/<App>-<ver>-<arch>.zip"
```

**Linux（`tools/package-linux.sh`）**——AppDir 骨架 + `appimagetool`；`.deb` 用 `dpkg-deb`：

```bash
# AppDir
mkdir -p "AppDir/usr/bin" "AppDir/usr/share/applications" "AppDir/usr/share/icons/hicolor/256x256/apps"
cp -R pkg/* "AppDir/usr/bin/"
# AppDir/<App>.desktop + AppDir/<App>.png（根级软链/图标是 AppImage 的约定）
curl -fsSL -o appimagetool \
  https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
chmod +x appimagetool
./appimagetool --appimage-extract-and-run AppDir "dist/<App>-<ver>-x64.AppImage"

# deb：手工拼 DEBIAN/control + 文件树，再 dpkg-deb --build
dpkg-deb --build debroot "dist/<App>_<ver>_amd64.deb"
```

**Windows（Inno Setup）**——沿用项目已有 `setup.iss`，只需让版本号可被 CI 注入：

```ini
; setup.iss 顶部：允许 CI 用 /DMyAppVersion=... 覆盖，本地直接编译时用兜底值
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
```

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DMyAppVersion=3.2.0 setup.iss
```

### 4.3 本地 Windows 快速验证（每次发版前可选但推荐）

CI 一轮 10~20 分钟，本地先验证构建配置改动能省一轮：

```bash
dotnet test <跨平台测试工程> -c Release                       # 先过测试，别把红灯推上去
rm -rf pkg installer
dotnet publish <桌面主工程> -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o installer/publish
# 然后编译安装包（需本机已装 Inno Setup 6）
```

**判断完成的标准是产物出现且大小合理**：`installer/` 出现 `.exe` 且 ≥50MB（自包含 WPF 安装包典型 60~200MB），**不要只信退出码**。

---

## Step 5. 压缩源码 zip

**命名沿用历史约定**（用户与文档可能引用），不要改：`<项目名>-Source-<version>.zip`。

必须排除的目录 / 文件：

| 排除项 | 原因 |
|---|---|
| `bin/` `obj/` | 编译中间产物 |
| `publish/` `installer/` `dist/` `release/` | 已打包产物，几百 MB ~ GB |
| `.git/` | 版本库 |
| `TestResults/` | 测试输出 |
| `*.log`（含应用自身 `app-error.log`） | **含本机绝对路径与进程路径**，打进公开包即隐私泄露 |
| `release-notes-*.md` | 发布说明草稿，不是源码 |
| `assets/`（CI 里 `download-artifact` 的落盘目录） | 会把安装包吞进源码包（见坑 N） |
| 历史 `*-Source-*.zip` | 自嵌套 |

**copyleft 许可证硬要求**：若仓库是 GPL / AGPL / LGPL，源码包**必须包含 `LICENSE`**，且已发布的二进制产物目录里也应带一份（Inno 的 `[Files]` 里加一条、mac `.app/Contents/Resources/` 与 Linux `usr/share/doc/<app>/` 各放一份）。这是许可证合规，不是可选项。

**必做校验**（不能只看"命令跑完了"）：

```bash
ls -l <项目名>-Source-*.zip                    # 大小应在 <10MB 量级
unzip -l <项目名>-Source-<version>.zip | grep -Ei '/(bin|obj|publish|installer|dist|release|\.git)/|\.log$|\.zip$' | head
# 期望无输出；有输出说明漏排
unzip -l <项目名>-Source-<version>.zip | grep -i 'LICENSE'    # GPL 项目必须有
```

若异常膨胀（几百 MB / GB），一定是漏排了大目录，按坑 N 排查。

---

## Step 6. 上传 GitHub Release

### 6.1 CI 自动（推荐，正式发版路径）

推 tag 即自动触发：

```bash
git tag vX.Y.Z
git push origin vX.Y.Z
gh run watch                        # 实时查看 CI 进度
```

### 6.2 本地兜底（CI 不可用 / 只发 Windows 单平台时）

**先确认 gh 已登录**（`gh auth status`）。未登录的处理见坑 L。

```bash
REPO="<owner>/<repo>"               # Step 0 从 git remote 读到
V="<新版本号>"

gh release create "v$V" \
  "installer/<项目名>-Setup-$V.exe" \
  "<项目名>-Source-$V.zip" \
  --repo "$REPO" \
  --title "v$V" \
  --notes-file "release-notes-v$V.md" \
  --latest \
  --target <主分支名>
```

- **`--latest` 必须带**：否则 `releases/latest` 仍指向旧版
- **全部资产在同一条命令里上传**，不要分多次
- 上传含 100MB 级安装包，耐心等待，超时给足（≥10 分钟）
- 同名 release 已存在时，**不要 delete**，改用 `gh release upload "v$V" <files> --clobber`

---

## Step 7. 验证发布结果

```bash
gh release view "v$V" --repo "$REPO" \
  --json tagName,isDraft,isPrerelease,targetCommitish,assets \
  --jq '{tag:.tagName,draft:.isDraft,pre:.isPrerelease,commit:.targetCommitish,assets:[.assets[]|{name,sizeMB:(.size/1048576*100|round/100)}]}'
```

确认：

- tag 正确、非 draft、非 prerelease、target 指向主分支
- **平台矩阵的每一份产物都在**：Windows `.exe`；macOS `.dmg` + `.zip` 各 x64/arm64 两版；Linux `.AppImage` + `.deb`；外加源码 zip（含 `LICENSE`）
- 各资产大小合理（安装包 60~300MB、源码 zip <10MB）
- `releases/latest` 指向新版本：`gh api repos/$REPO/releases/latest --jq .tag_name`

asset 名里的空格被 GitHub 自动替换成 `.` 是平台硬规则（见坑 Q），不影响使用。

---

## Step 8. 产物命名与安装说明

> **若项目没有应用内自动更新器，本节只做命名约定 + 交付说明，无"更新器契约"可核对（标 N/A）。**

### 8.1 命名契约

| 平台 | 产物名 | 原因 |
|---|---|---|
| Windows（只出 x64） | `<项目名>-Setup-<version>.exe` | 单架构平台可省架构词，**沿用历史命名**（README 已引用） |
| macOS（x64 + arm64） | `<项目名>-<version>-<arch>.dmg` / `.zip` | **必须含架构词**，否则两个架构互相无法区分 |
| Linux（只出 x64） | `<项目名>-<version>-x64.AppImage`、`<项目名>_<version>_amd64.deb` | 含架构词更稳（未来加 arm64 无需改名） |
| 源码 | `<项目名>-Source-<version>.zip` | 沿用历史约定（注意此处无 `v` 前缀） |

> ⚠️ **若将来恢复 Windows x86**：必须把 Windows 产物名改为含架构词（如 `<项目名>-Setup-<version>-x86.exe`）。否则 x86 机器上人工选择会选错安装包。
>
> ⚠️ **GitHub 会把资产名里的空格换成 `.`**（见坑 Q）。人工核对时要知道本地 `My App Setup 1.0.0.exe` 在 Release 上显示为 `My.App.Setup.1.0.0.exe`。

### 8.2 各平台安装方式（写进 Release 说明与 README）

| 平台 | 安装 | 未签名的系统提示与绕过 |
|---|---|---|
| Windows | 运行 `-Setup-<ver>.exe`，按向导安装 | SmartScreen「Windows 已保护你的电脑」→ 更多信息 → 仍要运行 |
| macOS | 打开 `.dmg`，把 `.app` 拖进「应用程序」 | Gatekeeper「无法验证开发者」→ `xattr -dr com.apple.quarantine /Applications/<App>.app`（见坑 H） |
| Linux | 给 AppImage 加执行权限后直接运行；或 `sudo dpkg -i *.deb` | AppImage 需要 `libfuse2`（见坑 I） |

---

## Step 9. 交付汇报

完成后向用户汇报：

1. Release 地址：`https://github.com/<owner>/<repo>/releases/tag/v<version>`
2. 本次发布包含的产物清单（按平台列出文件名与大小）
3. 各平台安装方式一句话说明，以及**未签名带来的系统提示**与绕过方法
4. 提醒：安装前**完全退出旧版（含托盘图标右键退出）**，否则会出现"装了新版仍复现旧 bug"（见坑 R）
5. GPL 项目补充说明：源码包与产物均已随附 `LICENSE`
6. 若某项能力（如自动更新、某平台包）本次**未交付**，明确说清原因与后续计划，不要让用户以为已覆盖

---

## 避坑指南

### 坑 A：WPF / WinForms 工程无法产出 macOS / Linux 包

**现象**：`dotnet publish -r osx-x64`（或 `-r linux-x64`）报错：
`error NETSDK1082: Microsoft.WindowsDesktop.App 没有运行时包可用于指定的 RuntimeIdentifier"osx-x64"。`

**根因**：`UseWPF` / `UseWindowsForms` 把工程绑定到 `Microsoft.WindowsDesktop.App` 框架引用，该框架引用只有 Windows RID 的运行时包。

**解法**：
1. **短期**：只发布 Windows，CI 里不要留必然失败的非 Windows 行。
2. **长期**：把 UI 层做成跨平台宿主（如 Avalonia），并把数据层 / 服务层抽到 `net9.0` 类库供两个宿主共用，然后对**新宿主**跑三平台矩阵。

### 坑 B：`dotnet test <解决方案>` 静默跑 0 个测试还返回成功

**现象**：`dotnet test MicaAgenda.sln` 输出极少，报「已通过!」但用例数是 0 或远小于预期。

**根因**：测试工程**没有被登记进 `.sln`**。解决方案里只有应用工程时，`dotnet test <sln>` 实际没有任何测试工程可跑，于是"成功"了——典型的假绿灯。

**解法**：

```bash
dotnet sln <解决方案>.sln list              # 确认测试工程在列表里
dotnet sln <解决方案>.sln add <测试工程>.csproj
```

并且**永远核对输出的用例总数**，不要只看退出码：

```
已通过! - 失败: 0，通过: 45，已跳过: 0，总计: 45
```

若有平台绑定测试被拆到 `net9.0-windows` 的独立工程，注意它在非 Windows runner 上会被跳过——CI 需在 Windows job 里单独跑它，或接受它是 Windows-only 保护网。

### 坑 C：发布路径与打包脚本读取路径不一致

**现象**：本地手动跑 `dotnet publish -o publish`，随后编译安装包，产物里缺文件或报找不到 `installer\publish\*`。

**根因**：README / 文档写的输出目录与 `setup.iss` 的 `[Files] Source:` 不一致（例如文档写 `-o publish`，脚本读 `installer\publish\*`）。

**解法**：以打包脚本为准，统一到一个目录（推荐 `installer/publish`），并**同步修正文档**。发布前用一条命令自检：

```bash
grep -n "Source:" setup.iss && grep -rn "\-o publish\|-o installer" README.md docs/
```

### 坑 D：单文件 / 自包含发布仍需要原生 DLL 伴随

**现象**：干净机器上「安装后打不开」，本机却正常。

**根因**：`PublishSingleFile` 合并的是托管程序集，WPF / Avalonia 的原生依赖（如 `wpfgfx_cor3.dll`、`D3DCompiler_47_cor3.dll`、`PenImc_cor3.dll`、`PresentationNative_cor3.dll`、`vcruntime140_cor3.dll`、Avalonia 的 `libSkiaSharp` / `libHarfBuzzSharp`）仍需置于 exe 旁。

**解法**：安装包**整目录打包**，不要只挑 exe。`setup.iss` 用 `Source: "installer\publish\*"; Flags: ignoreversion recursesubdirs`。

### 坑 E：开了 `PublishTrimmed` 后 XAML 反射找不到类型

**现象**：发布版启动即崩或界面空白，异常含 `XamlParseException` / `TypeLoadException` / `MissingMethodException`。

**根因**：裁剪器无法静态看到 XAML / 反射里的类型引用，把它们剪掉了。

**解法**：**不要对 WPF / Avalonia 桌面工程启用 `PublishTrimmed`**。需要减体积就改 `PublishReadyToRun` 或换自包含策略，不要动裁剪。

### 坑 F：Inno Setup 只能在 Windows 上编译

**现象**：在 macOS / Linux runner 上找不到 `ISCC.exe`。

**根因**：Inno Setup 是 Windows 原生工具。

**解法**：Windows 安装包**只在 `windows-latest` 上产出**。macOS 用 `hdiutil` + `ditto`，Linux 用 `appimagetool` + `dpkg-deb`——三者互不替代。

### 坑 G：安装脚本的清理逻辑误伤用户数据目录

**现象**：升级安装后任务 / 配置全部丢失。

**根因**：为清理旧版残留文件（多文件时代的 `*.dll`、`.deps.json`）写了"删除非白名单文件"的逻辑，但路径判定不严，可能触及 `%APPDATA%` / `%LOCALAPPDATA%` 下的用户数据。

**解法**：清理逻辑必须**只作用于安装目录**，并加多重护栏：

```pascal
AppDir := ExpandConstant('{app}');
IsSafePath := (Pos('AppData', AppDir) = 0) and (Pos('Roaming', AppDir) = 0) and (Pos('Local\', AppDir) = 0);
if not IsSafePath then begin Log('ABORT: refusing to clean ' + AppDir); Exit; end;
```

同时在脚本注释里写明：用户数据目录（`%APPDATA%\<App>` / `%LOCALAPPDATA%\<App>`）**绝对不能碰**。

### 坑 H：未签名 macOS 包被 Gatekeeper 拦截

**现象**：用户拖入「应用程序」后双击，提示「无法打开，因为 Apple 无法验证」。

**根因**：未做 Apple Developer 签名与公证（notarization）。

**解法**：短期明确告知绕过方式：

```bash
xattr -dr com.apple.quarantine /Applications/<App>.app
```

并在 Release 说明里写清楚。**不要**在 CI 里设置 `CSC_IDENTITY_AUTO_DISCOVERY` 之类的猜测性签名配置去碰运气。

### 坑 I：AppImage 在缺少 libfuse2 的发行版上无法启动

**现象**：`dlopen(): error loading libfuse.so.2`。

**根因**：AppImage 的运行期依赖 FUSE 2；Ubuntu 22.04+ 默认不带。

**解法**：README 里写明 `sudo apt install libfuse2`；或同时提供 `.deb` 作为兜底（本指南矩阵已含）。

### 坑 J：macOS runner 标签会退役

**现象**：CI 报 `The macOS runner version 'macos-XX' is not supported`。

**根因**：GitHub 会按批次下线旧 macOS 镜像。

**解法**：Intel 用 `macos-15-intel`、Apple Silicon 用 `macos-15`；一旦收到退役通知，查 runner 镜像文档换成在役标签，**不要**改成"在 arm64 上跑 x64"。

### 坑 K：绝不要删除已发布的 Release 资产

**现象**：老用户回滚时下载 404；README / 文档里的下载链接失效。

**根因**：为了"干净"而 `gh release delete` 或删除资产。

**解法**：`gh release upload --clobber` 做幂等更新即可；旧 tag、旧 Release、旧资产**一律保留**。README 引用了具体文件名时尤其如此。

### 坑 L：gh 已登录但 git push 认证失败（或 gh 报未登录）

**现象**：`git push` 报 `Authentication failed`，但 `gh auth status` 显示已登录、`gh api` 能用。

**根因**：Windows 凭据管理器存了**已失效的旧 token**，其优先级高于 gh CLI 提供的凭据。

**诊断**：

```bash
git config --show-origin --get-all credential.helper    # 看到 manager（系统级）+ gh（全局级）并存
cmdkey /list:git:https://github.com                     # 有存量凭据 → 就是它
```

**解法**：删除失效凭据（已无法认证，删除安全）：

```bash
cmdkey /delete:git:https://github.com
git push
```

**gh 确实未登录时**：请用户提供 PAT（`repo` 权限），当次命令前缀 `GH_TOKEN="<token>" gh ...`，**用完立即清理**，且不要把 token 写进任何文件或提交。不要在 token 没试过时就断言"未登录"。

### 坑 M：github.com 连不上（DNS 污染 / TLS 间歇重置），但 gh api 正常

**现象**：`git push` 报 `Failed to connect to github.com port 443`；同时 `gh api` 一切正常。

**诊断**：

```bash
curl -sS -o /dev/null -w "%{http_code}\n" --max-time 12 https://github.com/        # 连不上
curl -sS -o /dev/null -w "%{http_code}\n" --max-time 12 https://api.github.com/    # 200 → 主站被单独阻断
```

**解法**：走代理时用**单次内联配置**，不要改动用户全局 gitconfig：

```bash
git -c http.proxy=<proxy> -c https.proxy=<proxy> push origin <分支>
# 若全局 gitconfig 里配了坏代理，用空值单次覆盖：
git -c http.proxy= -c https.proxy= push origin <分支>
```

推送不成时**不要干等**，先做本地验证与打包（Step 4.3 / Step 5），网络恢复后统一上传。

### 坑 N：源码 zip 把构建产物一起吞了，体积膨胀到 GB 级

**现象**：`<项目名>-Source-<ver>.zip` 几百 MB ~ GB。

**根因**：漏排 `publish/` / `installer/` / `assets/`（CI 下载 artifact 的落盘目录）。`installer/` 与 `.gitignore` 里是否被忽略无关——压缩脚本是按磁盘目录扫的。

**解法**：压缩前显式列出排除项，压完必做体积与内容抽查（见 Step 5）。

### 坑 O：测试工程 target framework 跟错了宿主

**现象**：跨平台测试工程引用 WPF 主工程，导致它在 macOS / Linux runner 上还原失败。

**根因**：`net9.0-windows` 的工程被 `net9.0` 的测试工程引用。

**解法**：把**跨平台**测试放在 `net9.0` 工程里、只引用跨平台类库；把**平台绑定**测试（注册表、Win32 像素换算、WPF 几何）单独放一个 `net9.0-windows` 工程，只在 Windows runner 跑。

> 若平台测试需要访问应用的 `internal` 成员，记得在应用工程里补 `[assembly: InternalsVisibleTo("<测试程序集名>")]`。**改测试程序集名而不改这行**是最常见的漏项——表现为 `CS0122` / `CS0117` 一串找不到成员。

### 坑 P：tag 与工程内版本号不一致

**现象**：Release 打的是 `vX.Y.Z`，安装包里"关于"页显示旧版本。

**根因**：只在 `setup.iss` / README 改了版本，忘了代码里的标题字面量；或反之。

**解法**：按 Step 1 的搜索法逐处核对，并优先建立单一版本号来源（`Directory.Build.props` + CI `/D` 注入）。

### 坑 Q：资产名里的空格被 GitHub 换成 `.`

**现象**：本地叫 `My App Setup 1.0.0.exe`，Release 上显示 `My.App.Setup.1.0.0.exe`。

**根因**：GitHub 的资产名规范化规则。

**解法**：无解，也不影响使用；核对时按替换后的名字找。产物名尽量少用空格。

---

## 关键约束清单

✅ 必须做到：

- 版本号按 Step 1 搜索法逐处更新，tag 与工程内版本号一致
- CI 只用内置 `GITHUB_TOKEN`
- macOS / Linux 包在对应 runner 上产出，绝不交叉编译
- 发布用 `--latest`；已存在的 Release 用 `upload --clobber` 更新
- 源码 zip 排除 `bin/obj/publish/installer/dist/release/.git/TestResults/*.log`，**含 `LICENSE`**
- 安装包整目录打包（含原生 DLL / `libSkiaSharp` 等）
- 交付前核对 `dotnet test` 的**用例总数**，不只看退出码
- 汇报给出各平台安装方式 + 未签名提示

❌ 绝对不能：

- WPF / WinForms 工程直接往 macOS / Linux 发（`NETSDK1082`）
- 交叉编译跨平台包
- `dotnet sln` 里漏登记测试工程（假绿灯）
- 启用 `PublishTrimmed`
- 对桌面工程删除旧 Release 资产 / 旧 tag
- 把本机绝对路径、Token、个人邮箱写进任何交付文件
- 让安装脚本的清理逻辑可能触及 `%APPDATA%` / `%LOCALAPPDATA%`

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
- Windows：运行 exe。未签名 → SmartScreen 提示，点「更多信息 → 仍要运行」
- macOS：拖入「应用程序」后执行 `xattr -dr com.apple.quarantine /Applications/<App>.app`
- Linux：AppImage 加执行权限运行（需 `libfuse2`），或 `sudo dpkg -i *.deb`

**注意**：安装前请完全退出旧版（含托盘图标右键退出），否则可能复现旧问题。

**本次未覆盖**：<平台或能力 + 原因 + 后续计划；无则写"无">
```
