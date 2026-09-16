# desktop_todo_Calendar 桌面日历 MCP Skill 文档

## 1. 这个程序是什么

desktop_todo_Calendar 是一个 Windows 桌面日历/任务管理程序，内置 MCP Server。外部 AI 客户端可以通过 MCP 协议连接本程序，执行任务查询、添加、编辑、删除、完成/取消完成和批量操作；支持给任务设置具体时刻和多个提醒（含“提前一天”）。

本程序暴露的任务对象字段固定如下：

| 字段 | 类型 | 说明 |
|---|---|---|
| id | string | 任务唯一标识，UUID |
| date | string | 任务日期，格式 `YYYY-MM-DD` |
| time | string / null | 任务时刻，格式 `HH:mm`；`null` 表示没设具体时间（按当天 9:00 参与提醒与逾期计算） |
| scheduledAt | string | 实际计划时刻（本地时间）`YYYY-MM-DDTHH:mm:ss`；没设 time 时为当天 `09:00:00` |
| title | string | 任务标题 |
| isCompleted | boolean | 是否已完成 |
| isImportant | boolean | 是否重要 |
| isOverdue | boolean | 是否已逾期：未完成且当前时间已过 scheduledAt |
| reminderLeadMinutes | number[] | 提醒提前量（分钟）数组，按界面档位顺序；空数组表示不提醒。`0`=到时提醒，`1440`=提前一天 |
| reminders | string[] | 提醒档位标签数组（与 reminderLeadMinutes 一一对应），可原样回传给 add_task / update_task |
| createdAt | string | 创建时间，ISO 8601，带时区偏移 |
| completedAt | string / null | 完成时间；未完成时为 `null` |
| updatedAt | string / null | 状态最后变更时间（完成、取消完成都会刷新） |

提醒档位标签的固定取值（多选时每个档位各提醒一次）：

| 标签 | 提前量 |
|---|---|
| 到时提醒 | 0 分钟（任务时刻到点提醒） |
| 提前3分钟 | 3 |
| 提前5分钟 | 5 |
| 提前10分钟 | 10 |
| 提前15分钟 | 15 |
| 提前30分钟 | 30 |
| 提前1个小时 | 60 |
| 提前3个小时 | 180 |
| 提前一天 | 1440（任务时刻往前 24 小时） |

## 2. 连接信息

- 端点地址：`http://localhost:17804/mcp`
- 传输方式：Streamable HTTP（HTTP + JSON-RPC 2.0）
- 方法：客户端调用统一使用 `POST`

### 推荐 MCP 配置

```json
{
  "mcpServers": {
    "desktop_todo_Calendar": {
      "type": "streamable-http",
      "url": "http://localhost:17804/mcp",
      "headers": {
        "Authorization": "Bearer YOUR_TOKEN_HERE"
      }
    }
  }
}
```

### 鉴权

每个 HTTP 请求至少携带以下两种方式之一：

```http
Authorization: Bearer YOUR_TOKEN_HERE
```

或：

```http
X-Auth-Token: YOUR_TOKEN_HERE
```

令牌错误或缺失返回 HTTP `401`。来自非本机网页的跨源请求可能返回 HTTP `403`。

## 3. MCP 生命周期与协议方法

除业务工具外，MCP 客户端通常还会自动调用以下协议方法：

| method | 说明 |
|---|---|
| initialize | 握手，返回协议版本、能力与服务器信息（serverInfo.version 即应用版本号） |
| notifications/initialized | 客户端初始化完成通知，无 JSON-RPC `id` |
| ping | 健康检查 |
| tools/list | 获取全部可用工具定义（含参数枚举） |
| tools/call | 调用具体业务工具 |

### initialize 请求示例

```json
{
  "jsonrpc": "2.0",
  "method": "initialize",
  "params": {
    "protocolVersion": "2024-11-05",
    "capabilities": {},
    "clientInfo": {
      "name": "example-client",
      "version": "1.0.0"
    }
  },
  "id": 1
}
```

## 4. 通用请求格式

所有业务调用使用 JSON-RPC 2.0 `tools/call`：

```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "<工具名称>",
    "arguments": {
      "<参数名>": "<参数值>"
    }
  },
  "id": 1
}
```

`id` 可以是字符串或数字；服务端会原样回传。

## 5. 通用响应格式

### 成功响应

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "content": [
      {
        "type": "text",
        "text": "<业务返回数据的 JSON 字符串>"
      }
    ],
    "isError": false
  }
}
```

### 工具执行错误

工具内部的“任务不存在、参数错误”等业务异常，不会作为顶层 JSON-RPC `error` 返回，而是放在 `result` 中：

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "content": [
      {
        "type": "text",
        "text": "{\"error\":\"task not found: <id>\"}"
      }
    ],
    "isError": true
  }
}
```

调用方应先检查 `result.isError`；若为 `true`，解析 `result.content[0].text` 得到错误对象。参数类错误（如时间格式、提醒标签不合法）的错误信息会附带合法取值，可直接据此自我修正后重试。

## 6. 可用工具总览

| 工具名 | 作用 |
|---|---|
| query_tasks | 按范围 / 指定日期 / 任意区间查询，支持状态过滤与标题关键词 |
| add_task | 添加任务（可带时刻、多个提醒） |
| update_task | 编辑任务（标题、日期、时刻、重要性、提醒均可改） |
| delete_task | 删除任务 |
| complete_task | 标记任务已完成 |
| uncomplete_task | 取消任务完成状态 |
| batch_tasks | 批量执行多种操作 |

## 7. 工具详细说明

### 7.1 query_tasks

**作用**：查询任务。三种定位方式按以下优先级生效：`date`（某一天） > `start`/`end`（任意区间） > `range`（预设范围，默认 today）。

**参数**

| 参数 | 类型 | 必填 | 默认值 | 说明 |
|---|---|---|---|---|
| range | string | 否 | `today` | `today` / `week` / `month` / `year` / `all`；仅在未给 date、start、end 时生效 |
| date | string | 否 | 无 | 指定日期 `YYYY-MM-DD`；提供时优先 |
| start | string | 否 | 无 | 区间起始日期 `YYYY-MM-DD`（含当天）；只给 start 表示“从那天起” |
| end | string | 否 | 无 | 区间结束日期 `YYYY-MM-DD`（含当天）；只给 end 表示“截止那天” |
| status | string | 否 | `all` | `all` / `open`（未完成，含逾期）/ `completed`（已完成）/ `overdue`（仅逾期） |
| q | string | 否 | 无 | 标题关键词，不区分大小写、包含匹配 |

`start` 晚于 `end`、`range` 或 `status` 不在枚举内都会返回业务错误。

**返回结构**

```json
{
  "range": "today",
  "date": null,
  "start": null,
  "end": null,
  "status": "all",
  "query": null,
  "count": 2,
  "tasks": [
    {
      "id": "uuid",
      "date": "2026-09-20",
      "time": "14:30",
      "scheduledAt": "2026-09-20T14:30:00",
      "title": "任务标题",
      "isCompleted": false,
      "isImportant": true,
      "isOverdue": false,
      "reminderLeadMinutes": [30, 1440],
      "reminders": ["提前30分钟", "提前一天"],
      "createdAt": "2026-09-17T12:32:59+08:00",
      "completedAt": null,
      "updatedAt": null
    }
  ]
}
```

任务按 `date` 升序、同一天内按 `scheduledAt` 升序排列。

**请求示例：查本周所有未完成且标题含“周”的任务**

```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "query_tasks",
    "arguments": { "range": "week", "status": "open", "q": "周" }
  },
  "id": 1
}
```

**请求示例：查 9 月 15 日到 9 月 30 日之间的逾期任务**

```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "query_tasks",
    "arguments": { "start": "2026-09-15", "end": "2026-09-30", "status": "overdue" }
  },
  "id": 2
}
```

### 7.2 add_task

**作用**：添加任务。

**参数**

| 参数 | 类型 | 必填 | 默认值 | 说明 |
|---|---|---|---|---|
| title | string | 是 | 无 | 任务标题，不能为空 |
| date | string | 否 | 今天 | 任务日期 `YYYY-MM-DD` |
| time | string | 否 | `null` | 任务时刻 `HH:mm`（如 `14:30`，也兼容 `9:05`）；null/空串 = 不设具体时间（按当天 9:00） |
| isImportant | boolean | 否 | `false` | 是否重要 |
| reminders | string[] | 否 | `[]` | 提醒档位标签数组，可多选；空数组/省略 = 不提醒。合法值见第 1 节表格 |

**提醒语义**：每个档位以 `scheduledAt` 为锚点各推一次。例如任务是 9/20 14:30，`reminders=["提前一天","提前30分钟"]` 会在 9/19 14:30 和 9/20 14:00 各提醒一次。

**返回结构**：返回新创建的完整任务对象。

**请求示例**

```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "add_task",
    "arguments": {
      "title": "季度汇报",
      "date": "2026-09-20",
      "time": "10:00",
      "isImportant": true,
      "reminders": ["提前一天", "提前30分钟", "到时提醒"]
    }
  },
  "id": 3
}
```

### 7.3 update_task

**作用**：编辑任务，只改传入的字段（PATCH 语义）。

**参数**

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| id | string | 是 | 任务 UUID |
| title | string | 否 | 新标题；空白字符串会被忽略（不会清空标题） |
| date | string | 否 | 新日期 `YYYY-MM-DD` |
| time | string | 否 | 新时刻 `HH:mm`；显式传 `null` 或空串 = 清除时刻（回到当天 9:00 语义） |
| isImportant | boolean | 否 | 新的重要性状态 |
| reminders | string[] | 否 | **整组替换**提醒档位；传 `[]` 或 `null` = 清空全部提醒；合法值见第 1 节 |

修改日期、时刻或提醒档位后，旧的“已提醒”记录会全部作废，提醒按新计划重新触发。

**返回结构**：更新后的完整任务对象。

**请求示例：把会议改到下午 4 点，只保留提前一天提醒**

```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "update_task",
    "arguments": {
      "id": "51c43002-4013-47cc-88ce-f2b9b6d6524d",
      "time": "16:00",
      "reminders": ["提前一天"]
    }
  },
  "id": 4
}
```

### 7.4 delete_task

**作用**：永久删除任务。

**参数**

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| id | string | 是 | 任务 UUID |

**返回结构**

```json
{
  "id": "51c43002-4013-47cc-88ce-f2b9b6d6524d",
  "deleted": true
}
```

### 7.5 complete_task

**作用**：将任务标记为已完成。

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| id | string | 是 | 任务 UUID |

**返回结构**：更新后的完整任务对象；其中 `isCompleted=true`，`completedAt` 为完成时间。

### 7.6 uncomplete_task

**作用**：将已完成任务恢复为未完成。

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| id | string | 是 | 任务 UUID |

**返回结构**：更新后的完整任务对象；其中 `isCompleted=false`，`completedAt=null`。

### 7.7 batch_tasks

**作用**：一次请求执行多个操作。

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| operations | array | 是 | 操作对象数组 |

**操作对象**

| action | 说明 | 必需参数 | 可选参数 | 成功返回 |
|---|---|---|---|---|
| add / create | 添加任务 | title | date, time, isImportant, reminders | 新任务对象 |
| update / edit | 更新任务 | id | title, date, time, isImportant, reminders | 更新后任务对象 |
| delete / remove | 删除任务 | id | 无 | `{ id, deleted: true }` |
| complete | 标记完成 | id | 无 | 更新后任务对象 |
| uncomplete / incomplete | 取消完成 | id | 无 | 更新后任务对象 |

每个操作独立执行，单个操作失败（含 time / reminders 格式错误）只在该位置返回 `{ "error": "<失败原因>" }`，不影响其他操作、不回滚。

**请求示例**

```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "batch_tasks",
    "arguments": {
      "operations": [
        {
          "action": "add",
          "title": "新任务",
          "date": "2026-09-20",
          "time": "09:30",
          "reminders": ["提前一天"],
          "isImportant": true
        },
        {
          "action": "complete",
          "id": "51c43002-4013-47cc-88ce-f2b9b6d6524d"
        }
      ]
    }
  },
  "id": 5
}
```

## 8. 调用示例

### curl 查询今日未完成任务

```bash
curl -X POST http://localhost:17804/mcp \
  -H "Authorization: Bearer YOUR_TOKEN_HERE" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "tools/call",
    "params": {
      "name": "query_tasks",
      "arguments": {"range": "today", "status": "open"}
    },
    "id": 1
  }'
```

### curl 添加带多提醒的任务

```bash
curl -X POST http://localhost:17804/mcp \
  -H "Authorization: Bearer YOUR_TOKEN_HERE" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "tools/call",
    "params": {
      "name": "add_task",
      "arguments": {
        "title": "写周报",
        "date": "2026-09-20",
        "time": "18:00",
        "reminders": ["提前一天", "提前1个小时"]
      }
    },
    "id": 2
  }'
```

### Python

```python
import json
import requests

url = "http://localhost:17804/mcp"
headers = {
    "Authorization": "Bearer YOUR_TOKEN_HERE",
    "Content-Type": "application/json",
}

payload = {
    "jsonrpc": "2.0",
    "method": "tools/call",
    "params": {
        "name": "add_task",
        "arguments": {
            "title": "项目复盘",
            "date": "2026-09-20",
            "time": "15:00",
            "reminders": ["提前30分钟", "到时提醒"],
        },
    },
    "id": 1,
}

response = requests.post(url, headers=headers, data=json.dumps(payload))
result = response.json()
print(result["result"]["content"][0]["text"])
```

### JavaScript / TypeScript

```js
const response = await fetch("http://localhost:17804/mcp", {
  method: "POST",
  headers: {
    "Authorization": "Bearer YOUR_TOKEN_HERE",
    "Content-Type": "application/json",
  },
  body: JSON.stringify({
    jsonrpc: "2.0",
    method: "tools/call",
    params: {
      name: "query_tasks",
      arguments: { start: "2026-09-15", end: "2026-09-30", status: "overdue" },
    },
    id: 1,
  }),
});

const data = await response.json();
console.log(data.result.content[0].text);
```

## 9. 调用注意事项

1. 日期必须使用 `YYYY-MM-DD`；时刻必须使用 `HH:mm`（24 小时制，如 `09:05`、`14:30`）。
2. `reminders` 的元素必须是第 1 节表格里的中文档位标签；写错（如“提前两小时”）会返回业务错误，错误信息中附带全部合法值。
3. 没设 `time` 的任务在提醒与逾期判定上按当天 9:00 处理；`scheduledAt` 是真正参与计算的字段。
4. 任务 ID 是 UUID 字符串，必须使用已有任务返回的完整 ID；按 ID 操作的任务不存在时返回 `isError=true`，错误文本形如 `task not found: <id>`。
5. `update_task` 只改传入字段；`reminders` 只要出现就是整组替换（含空数组清空），不会与原有档位合并。
6. `batch_tasks` 的每个操作独立执行，单条失败不会回滚其他操作。
7. 所有增删改操作都会立即持久化到本地存储。
8. MCP 请求体上限为 1 MB；超大请求返回 HTTP `413`。
9. 本服务只面向本机/本地进程，跨源调用仅允许本机 Origin；非本机网页跨源调用会被拒绝。
