using System.Text.Json;

namespace MicaAgenda.App.Services;

/// <summary>
/// 生成供外部 AI 客户端使用的 Skill 文档与 MCP 配置 JSON 文本。
/// 文档以 McpServer 当前实现为基准，覆盖 MCP 生命周期、全部工具、
/// 参数、返回结构和调用示例。
/// </summary>
public static class McpDocsProvider
{
    /// <summary>生成 MCP 配置 JSON（Claude Desktop / Cursor 的 mcpServers 片段），含 token 鉴权。</summary>
    public static string BuildMcpConfigJson(string endpoint, string token)
    {
        var config = new
        {
            mcpServers = new
            {
                desktop_todo_Calendar = new
                {
                    type = "streamable-http",
                    url = endpoint,
                    headers = new
                    {
                        Authorization = $"Bearer {token}"
                    }
                }
            }
        };

        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>生成 Skill 文档（Markdown），描述 MCP 工具能力，供外部 AI 理解并调用。</summary>
    public static string BuildSkillDoc(string endpoint, string token)
    {
        return SkillDocTemplate
            .Replace("__ENDPOINT__", endpoint)
            .Replace("__TOKEN__", token);
    }

    private const string SkillDocTemplate = """
# desktop_todo_Calendar 桌面日历 MCP Skill 文档

## 1. 这个程序是什么

desktop_todo_Calendar 是一个 Windows 桌面日历/任务管理程序，内置 MCP Server。外部 AI 客户端可以通过 MCP 协议连接本程序，执行任务查询、添加、编辑、删除、完成/取消完成和批量操作。

本程序暴露的任务对象字段固定如下：

| 字段 | 类型 | 说明 |
|---|---|---|
| id | string | 任务唯一标识，UUID |
| date | string | 任务日期，格式 `YYYY-MM-DD` |
| title | string | 任务标题 |
| isCompleted | boolean | 是否已完成 |
| isImportant | boolean | 是否重要 |
| createdAt | string | 创建时间，ISO 8601，带时区偏移 |
| completedAt | string / null | 完成时间；未完成时为 `null` |

## 2. 连接信息

- 端点地址：`__ENDPOINT__`
- 传输方式：Streamable HTTP（HTTP + JSON-RPC 2.0）
- 方法：客户端调用统一使用 `POST`

### 推荐 MCP 配置

```json
{
  "mcpServers": {
    "desktop_todo_Calendar": {
      "type": "streamable-http",
      "url": "__ENDPOINT__",
      "headers": {
        "Authorization": "Bearer __TOKEN__"
      }
    }
  }
}
```

### 鉴权

每个 HTTP 请求至少携带以下两种方式之一：

```http
Authorization: Bearer __TOKEN__
```

或：

```http
X-Auth-Token: __TOKEN__
```

令牌错误或缺失返回 HTTP `401`。来自非本机网页的跨源请求可能返回 HTTP `403`。

## 3. MCP 生命周期与协议方法

除业务工具外，MCP 客户端通常还会自动调用以下协议方法：

| method | 说明 |
|---|---|
| initialize | 握手，返回协议版本、能力与服务器信息 |
| notifications/initialized | 客户端初始化完成通知，无 JSON-RPC `id` |
| ping | 健康检查 |
| tools/list | 获取全部可用工具定义 |
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

### initialize 响应结构

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "protocolVersion": "2024-11-05",
    "capabilities": {
      "tools": {}
    },
    "serverInfo": {
      "name": "desktop_todo_Calendar",
      "version": "1.4.0"
    }
  }
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

调用方应先检查 `result.isError`；若为 `true`，解析 `result.content[0].text` 得到错误对象。

## 6. 可用工具总览

| 工具名 | 作用 |
|---|---|
| query_tasks | 按范围或指定日期查询任务 |
| add_task | 添加任务 |
| update_task | 编辑任务 |
| delete_task | 删除任务 |
| complete_task | 标记任务已完成 |
| uncomplete_task | 取消任务完成状态 |
| batch_tasks | 批量执行多种操作 |

## 7. 工具详细说明

### 7.1 query_tasks

**作用**：按范围或指定日期查询任务。

**参数**

| 参数 | 类型 | 必填 | 默认值 | 说明 |
|---|---|---|---|---|
| range | string | 否 | `today` | `today` / `week` / `month` / `year` / `all` |
| date | string | 否 | 无 | 指定日期，`YYYY-MM-DD`；提供时优先按该日期查询 |

**返回结构**

```json
{
  "range": "today",
  "count": 2,
  "tasks": [
    {
      "id": "uuid",
      "date": "2026-08-26",
      "title": "任务标题",
      "isCompleted": false,
      "isImportant": true,
      "createdAt": "2026-08-26T12:32:59+08:00",
      "completedAt": null
    }
  ]
}
```

任务按 `date` 升序排列。`range` 字段回显请求传入的值；未传时回显 `today`。

**请求示例**

```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "query_tasks",
    "arguments": {
      "range": "week"
    }
  },
  "id": 1
}
```

### 7.2 add_task

**作用**：添加任务。

**参数**

| 参数 | 类型 | 必填 | 默认值 | 说明 |
|---|---|---|---|---|
| title | string | 是 | 无 | 任务标题，不能为空 |
| date | string | 否 | 今天 | 任务日期，`YYYY-MM-DD` |
| isImportant | boolean | 否 | `false` | 是否重要 |

**返回结构**：返回新创建的完整任务对象。

```json
{
  "id": "uuid",
  "date": "2026-08-26",
  "title": "写周报",
  "isCompleted": false,
  "isImportant": true,
  "createdAt": "2026-08-26T12:32:59+08:00",
  "completedAt": null
}
```

**请求示例**

```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "add_task",
    "arguments": {
      "title": "写周报",
      "date": "2026-08-26",
      "isImportant": true
    }
  },
  "id": 2
}
```

### 7.3 update_task

**作用**：编辑任务。

**参数**

| 参数 | 类型 | 必填 | 默认值 | 说明 |
|---|---|---|---|---|
| id | string | 是 | 无 | 任务 UUID |
| title | string | 否 | 不变 | 新标题；提供时不能为空 |
| date | string | 否 | 不变 | 新日期，`YYYY-MM-DD` |
| isImportant | boolean | 否 | 不变 | 新的重要性状态 |

**返回结构**：更新后的完整任务对象。

**请求示例**

```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "update_task",
    "arguments": {
      "id": "51c43002-4013-47cc-88ce-f2b9b6d6524d",
      "title": "改标题",
      "date": "2026-08-27",
      "isImportant": false
    }
  },
  "id": 3
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

**请求示例**

```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "delete_task",
    "arguments": {
      "id": "51c43002-4013-47cc-88ce-f2b9b6d6524d"
    }
  },
  "id": 4
}
```

### 7.5 complete_task

**作用**：将任务标记为已完成。

**参数**

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| id | string | 是 | 任务 UUID |

**返回结构**：更新后的完整任务对象；其中 `isCompleted=true`，`completedAt` 为完成时间。

**请求示例**

```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "complete_task",
    "arguments": {
      "id": "51c43002-4013-47cc-88ce-f2b9b6d6524d"
    }
  },
  "id": 5
}
```

### 7.6 uncomplete_task

**作用**：将已完成任务恢复为未完成。

**参数**

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| id | string | 是 | 任务 UUID |

**返回结构**：更新后的完整任务对象；其中 `isCompleted=false`，`completedAt=null`。

**请求示例**

```json
{
  "jsonrpc": "2.0",
  "method": "tools/call",
  "params": {
    "name": "uncomplete_task",
    "arguments": {
      "id": "51c43002-4013-47cc-88ce-f2b9b6d6524d"
    }
  },
  "id": 6
}
```

### 7.7 batch_tasks

**作用**：一次请求执行多个操作。

**参数**

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| operations | array | 是 | 操作对象数组 |

**操作对象**

| action | 说明 | 必需参数 | 可选参数 | 成功返回 |
|---|---|---|---|---|
| add / create | 添加任务 | title | date, isImportant | 新任务对象 |
| update / edit | 更新任务 | id | title, date, isImportant | 更新后任务对象 |
| delete / remove | 删除任务 | id | 无 | `{ id, deleted: true }` |
| complete | 标记完成 | id | 无 | 更新后任务对象 |
| uncomplete / incomplete | 取消完成 | id | 无 | 更新后任务对象 |

**返回结构**

```json
{
  "results": [
    { "id": "uuid", "date": "...", "title": "...", "isCompleted": false, "isImportant": false, "createdAt": "...", "completedAt": null },
    { "id": "uuid2", "deleted": true }
  ]
}
```

每个操作独立执行，单个操作失败会返回 `{ "error": "<失败原因>" }`，不影响其他操作。

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
          "date": "2026-08-26",
          "isImportant": true
        },
        {
          "action": "complete",
          "id": "51c43002-4013-47cc-88ce-f2b9b6d6524d"
        }
      ]
    }
  },
  "id": 7
}
```

## 8. 调用示例

### curl 查询今日任务

```bash
curl -X POST __ENDPOINT__ \
  -H "Authorization: Bearer __TOKEN__" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "tools/call",
    "params": {
      "name": "query_tasks",
      "arguments": {"range": "today"}
    },
    "id": 1
  }'
```

### curl 添加任务

```bash
curl -X POST __ENDPOINT__ \
  -H "Authorization: Bearer __TOKEN__" \
  -H "Content-Type: application/json" \
  -d '{
    "jsonrpc": "2.0",
    "method": "tools/call",
    "params": {
      "name": "add_task",
      "arguments": {
        "title": "写周报",
        "date": "2026-08-26",
        "isImportant": true
      }
    },
    "id": 2
  }'
```

### Python

```python
import json
import requests

url = "__ENDPOINT__"
headers = {
    "Authorization": "Bearer __TOKEN__",
    "Content-Type": "application/json",
}

payload = {
    "jsonrpc": "2.0",
    "method": "tools/call",
    "params": {
        "name": "query_tasks",
        "arguments": {"range": "today"},
    },
    "id": 1,
}

response = requests.post(url, headers=headers, data=json.dumps(payload))
result = response.json()
print(result["result"]["content"][0]["text"])
```

### JavaScript / TypeScript

```js
const response = await fetch("__ENDPOINT__", {
  method: "POST",
  headers: {
    "Authorization": "Bearer __TOKEN__",
    "Content-Type": "application/json",
  },
  body: JSON.stringify({
    jsonrpc: "2.0",
    method: "tools/call",
    params: {
      name: "add_task",
      arguments: {
        title: "新任务",
        date: "2026-08-26",
        isImportant: false,
      },
    },
    id: 1,
  }),
});

const data = await response.json();
console.log(data.result.content[0].text);
```

## 9. 调用注意事项

1. 日期必须使用 `YYYY-MM-DD`。
2. 任务 ID 是 UUID 字符串，区分大小写，必须使用已有任务返回的完整 ID。
3. 除 `query_tasks` 外，所有按 ID 操作的任务必须真实存在，否则返回 `isError=true`，错误文本形如 `task not found: <id>`。
4. `batch_tasks` 的每个操作独立执行，单条失败不会回滚其他操作。
5. 所有增删改操作都会立即持久化到本地存储。
6. MCP 请求体上限为 1 MB；超大请求返回 HTTP `413`。
7. 本服务只面向本机/本地进程，跨源调用仅允许本机 Origin；非本机网页跨源调用会被拒绝。
""";
}
