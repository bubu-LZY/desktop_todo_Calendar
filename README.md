# desktop_todo_Calendar

一个基于 .NET 9 WPF 的 Windows 桌面日历 / 待办任务工具，支持 Mica 风格玻璃主题、中国节假日、桌面嵌入，以及面向 AI 客户端的 MCP Server 和 HTTP API。

> 仓库与产品展示名：`desktop_todo_Calendar`。  
> 底层工程名仍保留 `MicaAgenda.App`，便于兼容旧数据和既有构建链路。

## 致谢原作者

本项目基于 [zhangyiye18601-spec/mica-agenda](https://github.com/zhangyiye18601-spec/mica-agenda) 二次创作，感谢原作者 [zhangyiye18601-spec](https://github.com/zhangyiye18601-spec) 提供的优秀基础项目。

在此基础上新增/改进的内容包括：

- MCP Server（Model Context Protocol）集成
- HTTP REST API
- 12 种背景主题
- 中国节假日联网刷新与本地缓存
- 桌面嵌入 / 置底
- 定时提醒、自动备份
- 系统托盘常驻
- DWM Mica / Acrylic 窗口效果
- 窄窗口响应式布局
- 年视图今日高亮与“今天”定位

## 在线演示

交互式网页已部署到 GitHub Pages：

https://bubu-lzy.github.io/desktop_todo_Calendar/

该页面是纯前端交互演示，不连接真实数据。

## 功能特性

### 日历视图

| 视图 | 说明 |
|---|---|
| 月视图 | 连续滚动时间轴，支持跨月扩展 |
| 周视图 | 7 天格子，下方展示今日任务和本周任务完成情况 |
| 年视图 | 12 个月任务密度总览，支持今日高亮与滚动定位 |

### 任务管理

- 双击日期格空白处添加任务
- 双击已有任务原地编辑
- 右键菜单：完成 / 取消完成、重要 / 取消重要、删除
- 今日任务面板、本周任务完成情况统计
- 任务支持 `YYYY-MM-DD` 日期、重要标记、完成时间

### 背景与界面

- 12 种主题：玻璃、透明、纯色、无背景、清边框、白雾、灰雾、暗色磨砂、蓝色亚克力、薄荷玻璃、纸感浅白、石墨深色
- 透明度可调
- 无边框圆角窗口
- 桌面嵌入模式，可保持在其他普通窗口之下

### 中国节假日

- 内置本地兜底数据
- 支持联网刷新
- 缓存 TTL 与本地缓存
- 显示休息日、调休补班日

### 系统能力

- 系统托盘
- 开机自启
- 锁定窗口位置
- 导入 / 导出 JSON
- 自动备份
- 定时提醒

## 下载

### 安装包

从 [Releases](https://github.com/bubu-LZY/desktop_todo_Calendar/releases) 下载最新版本：

| 平台 | 文件 |
|---|---|
| Windows | `desktop_todo_Calendar-Setup-3.2.0.exe` |
| macOS (Apple Silicon) | `desktop_todo_Calendar-3.2.0-arm64.dmg` / `.zip` |
| macOS (Intel) | `desktop_todo_Calendar-3.2.0-x64.dmg` / `.zip` |
| Linux | `desktop_todo_Calendar-3.2.0-x64.AppImage`、`desktop_todo_Calendar_3.2.0_amd64.deb` |
| 源码 | `desktop_todo_Calendar-Source-3.2.0.zip` |

发布包为自包含版本，无需额外安装 .NET 运行时。未签名，首次运行 mac 需 `xattr -dr com.apple.quarantine /Applications/...`。

## 从源码构建

环境要求：

- .NET 9 SDK（Windows / macOS / Linux 均可）

```bash
git clone https://github.com/bubu-LZY/desktop_todo_Calendar.git
cd desktop_todo_Calendar

dotnet test MicaAgenda.Tests -c Release
dotnet build MicaAgenda.sln -c Release

# 跨平台宿主：MicaAgenda.Desktop（Avalonia）
dotnet publish MicaAgenda.Desktop -r win-x64 --self-contained true -c Release -o installer/publish
```

### 构建安装程序

需要 [Inno Setup 6](https://jrsoftware.org/isinfo.php)：

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" setup.iss
```

## MCP 集成

`desktop_todo_Calendar` 内置 MCP Server，默认端点：

```text
http://localhost:17802/mcp
```

传输方式：Streamable HTTP + JSON-RPC 2.0。

### 可用 MCP 工具

| 工具 | 作用 |
|---|---|
| `query_tasks` | 查询任务 |
| `add_task` | 添加任务 |
| `update_task` | 编辑任务 |
| `delete_task` | 删除任务 |
| `complete_task` | 标记完成 |
| `uncomplete_task` | 取消完成 |
| `batch_tasks` | 批量操作 |

### MCP 客户端配置

Claude Desktop / Cursor 可添加：

```json
{
  "mcpServers": {
    "desktop_todo_Calendar": {
      "type": "streamable-http",
      "url": "http://localhost:17802/mcp",
      "headers": {
        "Authorization": "Bearer YOUR_TOKEN_HERE"
      }
    }
  }
}
```

应用内“复制 MCP 配置 JSON”会自动带上当前真实 Token。请勿将真实 Token 提交到仓库。

### Skill 文档

完整的 AI 调用说明已放在 [SKILL.md](SKILL.md)。

该文档覆盖：

- MCP 生命周期方法
- 全部业务工具
- 每个工具的参数、返回结构和调用示例
- 鉴权方式
- 错误处理
- Python / JavaScript / curl 示例

## HTTP API

内置 HTTP API，默认端口 `17801`，仅监听 localhost。

```text
http://localhost:17801
```

支持任务查询、新增、编辑、删除、完成/取消完成、批量操作。详见 [API.md](API.md)。

鉴权支持两种方式：

```http
Authorization: Bearer <token>
```

或：

```http
X-Auth-Token: <token>
```

## 数据与隐私

所有任务和配置默认保存在本机用户数据目录中，不上传到服务器。

请勿把以下内容提交到公开仓库：

- 真实 API Token
- 飞书 / 企业微信 webhook 地址
- 本机绝对路径
- 个人账号、密钥、密码

本项目文档中的 Token 一律使用 `YOUR_TOKEN_HERE` 或 `<token>` 占位。

## 项目结构

```text
.
├── MicaAgenda.App/
│   ├── Assets/
│   ├── Converters/
│   ├── Helpers/
│   ├── Models/
│   ├── Services/
│   │   ├── McpServer.cs
│   │   ├── TaskApiServer.cs
│   │   ├── McpDocsProvider.cs
│   │   ├── DesktopEmbedService.cs
│   │   └── ...
│   └── ViewModels/
├── MicaAgenda.Tests/
├── docs/
│   └── index.html
├── setup.iss
├── API.md
├── SKILL.md
├── CHANGELOG.md
├── LICENSE
└── MicaAgenda.sln
```

## License

本项目遵循 [GNU General Public License v3.0](LICENSE)。
