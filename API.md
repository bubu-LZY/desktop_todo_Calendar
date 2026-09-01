# desktop_todo_Calendar 桌面日历 — 接口说明

程序内置两类接口：**HTTP API**（供任意程序/脚本调用）和 **MCP Server**（供 AI 客户端调用）。此外还提供定时提醒、自动备份、桌面嵌入等能力，均可通过应用内「⚙ 设置」面板配置。

## MCP 接口（供外部 AI 客户端连接）

内置 MCP Server（Model Context Protocol，Streamable HTTP 传输），默认端口 `17802`。

- **端点**：`http://localhost:17802/mcp`
- **鉴权**：与 HTTP API 共用同一个 Token（`Authorization: Bearer <token>`）
- 支持 Claude Desktop / Cursor 等支持 MCP 的客户端直接连接。

### 可用工具（与 HTTP API 能力完全一致）

| 工具 | 说明 | 关键参数 |
|---|---|---|
| `query_tasks` | 查询任务 | `range`(today/week/month/year/all)、`date`(YYYY-MM-DD) |
| `add_task` | 添加任务 | `title`(必填)、`date`、`isImportant` |
| `update_task` | 编辑任务 | `id`(必填)、`title`、`date`、`isImportant` |
| `delete_task` | 删除任务 | `id`(必填) |
| `complete_task` | 标记完成 | `id`(必填) |
| `uncomplete_task` | 取消完成 | `id`(必填) |
| `batch_tasks` | 批量增删改查 | `operations`(数组) |

### MCP 配置 JSON（一键复制）

在设置面板点击「复制 MCP 配置 JSON」，得到（已含鉴权 Token）：

```json
{
  "mcpServers": {
    "micaagenda": {
      "url": "http://localhost:17802/mcp",
      "headers": {
        "Authorization": "Bearer <token>"
      }
    }
  }
}
```

### Skill 文档（一键复制）

设置面板点击「复制 Skill 文档」，得到描述全部工具能力的 Markdown，可直接交给外部 AI 作为调用说明。

---

## HTTP API

内置 HTTP 服务，默认端口 `17801`，仅监听本机（`localhost` / `127.0.0.1`）。
供其他 Agent 通过 HTTP 调用，管理日历任务。

## 鉴权

每次请求需带 Token（Token 见 `%AppData%\MicaAgenda\api-token.txt`，首次运行自动生成）。

两种方式任选其一：

```
X-Auth-Token: <token>
```
或
```
Authorization: Bearer <token>
```

无 Token 或 Token 错误返回 `401`。

## 接口一览

基础地址：`http://localhost:17801`

### 1. 健康检查
```
GET /api/health
```

### 2. 查询任务清单
```
GET /api/tasks?range=<范围>
GET /api/tasks?date=<YYYY-MM-DD>
```
`range` 取值：`today`（今日，默认）/ `week`（本周）/ `month`（本月）/ `year`（本年）/ `all`（全部）。
`date` 指定某一天（优先级高于 range）。

返回示例：
```json
{ "range": "today", "count": 1, "tasks": [ { "id": "...", "date": "2026-08-25", "title": "开会", "isCompleted": false, "isImportant": true, "createdAt": "...", "completedAt": null } ] }
```

### 3. 添加任务（某一天）
```
POST /api/tasks
Content-Type: application/json
{ "title": "任务内容", "date": "2026-08-25", "isImportant": true }
```
`date` 省略时默认今天，`isImportant` 省略时默认 false。

### 4. 编辑任务
```
PUT /api/tasks/{id}
{ "title": "新标题", "date": "2026-08-26", "isImportant": false }
```
字段均可选，只更新传入的字段。

### 5. 删除任务
```
DELETE /api/tasks/{id}
```

### 6. 标记完成 / 取消完成
```
POST /api/tasks/{id}/complete     # 标记完成
POST /api/tasks/{id}/uncomplete   # 取消完成
```
（POST 无 body，调用方请确保带 `Content-Length: 0`；requests/fetch 等客户端会自动处理。）

### 7. 批量增删改查
```
PUT /api/tasks
{ "operations": [
    { "action": "add", "title": "任务A", "date": "2026-08-25", "isImportant": true },
    { "action": "update", "id": "<id>", "title": "改名" },
    { "action": "delete", "id": "<id>" },
    { "action": "complete", "id": "<id>" },
    { "action": "uncomplete", "id": "<id>" }
] }
```
`action` 取值：`add`/`create`、`update`/`edit`、`delete`/`remove`、`complete`、`uncomplete`/`incomplete`。
返回 `{ "results": [ ...每个操作的结果或错误... ] }`。

## 调用示例（curl）

```bash
TOKEN="<你的token>"
BASE="http://localhost:17801"

# 查今日
curl -H "X-Auth-Token: $TOKEN" "$BASE/api/tasks?range=today"

# 加任务
curl -X POST -H "X-Auth-Token: $TOKEN" -H "Content-Type: application/json" \
  -d '{"title":"写周报","date":"2026-08-25"}' "$BASE/api/tasks"

# 标记完成
curl -X POST -d '' -H "X-Auth-Token: $TOKEN" "$BASE/api/tasks/<id>/complete"
```

## 定时提醒配置

提醒走飞书 / 企业微信机器人 webhook，配置在 `%AppData%\MicaAgenda\app-config.json`：

```json
{
  "ReminderEnabled": true,
  "ReminderTime": "09:00",
  "FeishuWebhook": "https://open.feishu.cn/open-apis/bot/v2/hook/xxx",
  "WeComWebhook": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=xxx"
}
```

- `ReminderTime`：全局提醒时间（HH:mm），每天到点若当日有任务则推送。
- `FeishuWebhook` / `WeComWebhook`：留空则对应渠道不启用，两者可同时填。

> **开机补发**：提醒状态会持久化到本地（`reminder-state.json`）。若电脑在提醒时间前关机，开机自启后程序会自动检查——当天尚未提醒且已过提醒时间，则立即补发一次；若还没到点则正常等待。

## 自动备份

每天到点自动把**全年任务**导出为 JSON，保存到本地并推送飞书/企微。相关配置：

```json
{
  "BackupEnabled": true,
  "BackupTime": "23:00"
}
```

- `BackupTime`：每天备份时间（HH:mm）。
- 本地备份文件保存在 `%AppData%\MicaAgenda\backups\`，文件名形如 `micaagenda-backup-2026-20260825-230000.json`。
- **企业微信**：直接推送完整 JSON 文件（通过 webhook 上传文件接口）。
- **飞书**：飞书机器人 webhook 不支持发文件，因此以 JSON 文本形式推送（内容超长会自动截断，完整文件请用企微渠道或本地备份）。

> **开机补备份**：备份状态持久化到 `backup-state.json`。电脑关机重启后，若当天尚未备份且已过备份时间，自启时立即补一次备份。

## 导入 / 导出 JSON

在应用设置面板中提供三个按钮：

- **立即备份**：手动触发一次备份（导出 + 推送飞书/企微）。
- **导出 JSON**：把全年任务导出为 JSON 文件到任意位置。
- **导入 JSON**：从 JSON 文件导入任务（按任务 Id 去重合并，已存在的跳过）。

导出的 JSON 结构：

```json
{
  "app": "MicaAgenda",
  "version": 1,
  "exportedAt": "2026-08-25T...",
  "year": 2026,
  "tasks": [ { "id": "...", "date": "2026-08-25", "title": "...", "isCompleted": false, "isImportant": true, "createdAt": "...", "completedAt": null } ]
}
```

## 桌面嵌入说明

`app-config.json` 中的行为开关：

- `EmbedDesktop`（默认 true）：嵌入壁纸层，位于桌面图标下方，Win+D 不消失。
- `LockWindow`（默认 true）：锁定位置，禁止拖动/缩放。
- `ApiPort`：API 端口（默认 17801）。
- `ApiToken`：API Token（留空首次运行自动生成）。
- `McpEnabled`（默认 true）：是否开放 MCP 接口。
- `McpPort`：MCP 端口（默认 17802）。

> 以上所有配置均可直接在应用内「⚙ 设置」面板修改，无需手改 JSON 文件。关闭窗口会隐藏到系统托盘（不退出），托盘图标可重新打开、打开设置或退出。
