# 管理后台 API 契约

> 前缀 `/api/admin`，前后端以本文为准。后端实现位于 `src/PayOps.Server/Controllers/Admin`。

## 1. 通用约定

| 项 | 约定 |
| --- | --- |
| 鉴权 | 除登录外全部需要 `Authorization: Bearer <token>`；失效返回 HTTP 401 |
| 权限 | 角色 `admin`（全部）、`operator`（配置与订单操作，不含用户管理/查看密钥）、`viewer`（只读）；越权返回 HTTP 403 |
| 响应包 | 一律 HTTP 200 + `{ "code": 0, "message": "ok", "data": ... }`；`code != 0` 为业务错误，`message` 可直接展示 |
| 分页 | 入参 `page`（从 1 开始）、`pageSize`（默认 20，最大 200）；出参 `data: { "total": 123, "items": [] }` |
| 金额 | 整数，单位 **分** |
| 时间 | ISO-8601 UTC，如 `2026-11-27T08:00:00.000Z`；筛选参数 `startTime` / `endTime` 同格式 |
| ID | 数字（自增 bigint，安全范围内）；订单/退款对外标识用字符串 `orderNo` / `refundNo` |
| 字段命名 | camelCase |

### 枚举

| 枚举 | 值 |
| --- | --- |
| 启停 `status` | `1` 启用，`0` 停用 |
| 订单 `OrderStatus` | `0` 待支付，`1` 支付成功，`2` 已关闭，`3` 支付失败 |
| 退款 `RefundStatus` | `0` 处理中，`1` 退款成功，`2` 退款失败 |
| 通知 `NotifyStatus` | `0` 待通知，`1` 通知成功，`2` 通知失败（重试耗尽） |
| 通知类型 `NotifyType` | `1` 支付，`2` 退款 |
| 角色 | `admin` / `operator` / `viewer` |

## 2. 认证

| 方法 | 路径 | 入参 | 出参 `data` |
| --- | --- | --- | --- |
| POST | `/auth/login` | `{ username, password }` | `{ token, expiresIn, user: { id, username, displayName, role } }` |
| GET | `/auth/me` | - | `{ id, username, displayName, role }` |
| POST | `/auth/password` | `{ oldPassword, newPassword }` | `null` |

## 3. 元数据

`GET /meta/providers` → 支付类型（provider）元数据数组，前端据此动态渲染通道配置表单：

```json
[
  {
    "code": "wechat",
    "name": "微信支付",
    "category": "direct",
    "methods": [{ "code": "wechat_app", "name": "微信APP支付" }],
    "capabilities": { "query": true, "close": true, "refund": true, "receiptVerify": false, "serverNotify": true },
    "configFields": [
      {
        "key": "appId", "label": "AppID", "type": "text",
        "required": true, "secret": false,
        "placeholder": "wx...", "help": "说明文字",
        "defaultValue": "", "options": [{ "label": "正式", "value": "prod" }]
      }
    ],
    "notifyNote": "回调地址需要配置到哪里的说明"
  }
]
```

- `category`：`direct`（直连支付：微信/支付宝/抖音）、`store`（应用商店内购）、`mock`（演示）。
- `configFields[].type`：`text` | `password` | `textarea` | `number` | `switch` | `select`。`switch` 的值为字符串 `"true"` / `"false"`。所有配置值都以字符串传输。
- `secret = true` 的字段在详情接口中返回 `"******"`；更新时原样回传 `"******"` 或空串表示保持不变。

## 4. 仪表盘

| 方法 | 路径 | 出参 `data` |
| --- | --- | --- |
| GET | `/dashboard/summary` | `{ today: Stat, yesterday: Stat, pendingNotify, failedNotify, processingRefund }`，`Stat = { orderCount, paidCount, paidAmount, refundAmount, successRate }`（`successRate` 0~1） |
| GET | `/dashboard/trend?days=7` | `[{ date: "2026-11-27", orderCount, paidCount, paidAmount }]`（升序，日期按业务时区） |
| GET | `/dashboard/distribution?dimension=channel\|passage\|provider&days=1` | `[{ key, name, orderCount, paidCount, paidAmount, successRate }]` |
| GET | `/system/info` | `{ version, instanceId, roles: [], workerId, startedAt, checks: [{ name, ok, message }], nodes: [{ instanceId, host, roles: [], workerId, version, startedAt, lastSeen }] }` |

## 5. 应用（App，自有业务产品）

| 方法 | 路径 | 入参 | 出参 `data` |
| --- | --- | --- | --- |
| GET | `/apps?page&pageSize&keyword&status` | - | 分页 `AppItem` |
| GET | `/apps/all` | - | `[{ id, appCode, name, status }]` |
| POST | `/apps` | `{ appCode, name, notifyUrl, remark, status }` | `{ id, appSecret }`（密钥仅此时与重置时返回） |
| PUT | `/apps/{id}` | `{ name, notifyUrl, remark, status }` | `null` |
| DELETE | `/apps/{id}` | - | `null`（存在渠道时拒绝） |
| GET | `/apps/{id}/secret` | - | `{ appSecret }`（仅 admin） |
| POST | `/apps/{id}/reset-secret` | - | `{ appSecret }`（仅 admin） |

`AppItem = { id, appCode, name, notifyUrl, status, remark, channelCount, createdAt, updatedAt }`

## 6. 渠道（Channel，分发渠道，隶属应用）

| 方法 | 路径 | 入参 | 出参 `data` |
| --- | --- | --- | --- |
| GET | `/channels?page&pageSize&appId&keyword&status` | - | 分页 `ChannelItem` |
| GET | `/channels/all?appId=` | - | `[{ id, appId, code, name, status }]` |
| GET | `/channels/{id}` | - | `ChannelItem` |
| POST | `/channels` | `{ appId, code, name, remark, status }` | `{ id }` |
| PUT | `/channels/{id}` | `{ name, remark, status }` | `null` |
| DELETE | `/channels/{id}` | - | `null`（同时删除其通道绑定） |
| POST | `/channels/{id}/copy` | `{ targetAppId, code, name }` | `{ id }`（连同通道绑定一起复制） |

`ChannelItem = { id, appId, appCode, appName, code, name, status, remark, bindingCount, createdAt, updatedAt }`

## 7. 渠道通道绑定（路由规则）

| 方法 | 路径 | 入参 | 出参 `data` |
| --- | --- | --- | --- |
| GET | `/channels/{channelId}/bindings` | - | `BindingItem[]`（按 priority、id 升序） |
| POST | `/channels/{channelId}/bindings` | `BindingInput + { passageId }` | `{ id }` |
| PUT | `/bindings/{id}` | `BindingInput` | `null` |
| DELETE | `/bindings/{id}` | - | `null` |
| POST | `/bindings/{id}/reset-breaker` | - | `null`（手动恢复熔断） |
| POST | `/channels/{channelId}/route-simulate` | `{ payMethod, amount, times }`（times 默认 1000，最大 100000） | `RouteSimulateResult` |

```text
BindingInput = {
  methods: string[],        // 允许的支付方式，空数组 = 该通道类型支持的全部
  weight: number,           // 权重 0~10000；同优先级内按权重随机；0 = 备用（仅当同组无正权重可用通道时才使用）
  priority: number,         // 优先级，越小越优先；高优先级组全部不可用才落到下一组
  minAmount: number,        // 起付金额（分），订单金额 >= 该值才可用；0 = 不限
  maxAmount: number,        // 单笔上限（分），0 = 不限
  dailyLimitAmount: number, // 日成功金额限额（分），0 = 不限
  dailyLimitCount: number,  // 日成功笔数限额，0 = 不限
  startTime: string,        // 可用时段起 "HH:mm"，空串 = 全天
  endTime: string,          // 可用时段止 "HH:mm"，支持跨零点
  status: number,
  remark: string
}

BindingItem = BindingInput + {
  id, channelId, passageId, passageName, passageStatus, provider, providerName,
  createdAt, updatedAt,
  runtime: { todayAmount, todayCount, breakerOpen, recentFailures }
}

RouteSimulateResult = {
  payMethod, amount, times,
  items: [{
    bindingId, passageId, passageName, provider, priority, weight,
    eligible: boolean,   // 是否通过全部过滤条件
    reason: string,      // 不可用原因（eligible=false 时）
    hitCount: number,    // 模拟中被选为首选通道的次数
    hitRate: number      // 0~1
  }]
}
```

## 8. 通道（Passage，一个支付类型下的一套商户/应用凭证）

| 方法 | 路径 | 入参 | 出参 `data` |
| --- | --- | --- | --- |
| GET | `/passages?page&pageSize&provider&keyword&status` | - | 分页 `PassageItem` |
| GET | `/passages/all?provider=` | - | `[{ id, name, provider, providerName, status }]` |
| GET | `/passages/{id}` | - | `PassageItem + { config: { [key]: string } }`（密钥字段为 `******`） |
| POST | `/passages` | `{ name, provider, status, remark, config }` | `{ id }` |
| PUT | `/passages/{id}` | `{ name, status, remark, config }` | `null`（provider 不可改） |
| DELETE | `/passages/{id}` | - | `null`（已被渠道绑定时拒绝） |
| POST | `/passages/{id}/test` | - | `{ ok, message }`（配置自检：必填项、密钥格式等） |

`PassageItem = { id, name, provider, providerName, status, remark, bindingCount, notifyUrl, createdAt, updatedAt }`，`notifyUrl` 为该通道的上游回调地址（需配置到对应开放平台）。

## 9. 订单

| 方法 | 路径 | 入参 | 出参 `data` |
| --- | --- | --- | --- |
| GET | `/orders?page&pageSize&orderNo&mchOrderNo&upstreamTradeNo&appId&channelId&passageId&provider&status&userId&startTime&endTime` | - | 分页 `OrderItem` |
| GET | `/orders/{orderNo}` | - | `OrderDetail` |
| POST | `/orders/{orderNo}/sync` | - | `{ status, changed, message }`（主动查询上游并同步，即"补单"） |
| POST | `/orders/{orderNo}/close` | - | `null` |
| POST | `/orders/{orderNo}/refund` | `{ amount, reason }` | `{ refundNo, status }` |
| POST | `/orders/{orderNo}/renotify` | - | `null`（重新通知业务方） |
| POST | `/orders/{orderNo}/mock-pay` | - | `null`（仅 mock 通道订单，模拟上游支付成功回调） |
| POST | `/orders/test` | `{ appId, channelCode, payMethod, amount, subject, productId, openId, userId }` | `TestOrderResult`（后台测试下单） |

```text
OrderItem = {
  id, orderNo, mchOrderNo, appId, appCode, channelId, channelCode,
  passageId, passageName, provider, payMethod, subject,
  amount, refundAmount, currency, status, upstreamTradeNo, userId,
  createdAt, paidAt, expireAt
}

OrderDetail = OrderItem + {
  body, productId, clientIp, buyerId, notifyUrl, returnUrl, attach,
  credential,        // 下单返回给客户端的支付凭据（JSON 字符串，可能为空）
  errorCode, errorMsg, closedAt, checkCount, nextCheckAt, updatedAt, bindingId,
  refunds: RefundItem[],
  notifyTasks: NotifyTaskItem[],
  upstreamNotifies: UpstreamNotifyItem[]
}

TestOrderResult = {
  orderNo, mchOrderNo, status, amount, provider, payMethod, passageId, passageName, expireAt,
  credential: { type: string, data: object }
}
```

## 10. 退款

| 方法 | 路径 | 出参 `data` |
| --- | --- | --- |
| GET | `/refunds?page&pageSize&refundNo&orderNo&mchRefundNo&appId&status&startTime&endTime` | 分页 `RefundItem` |
| POST | `/refunds/{refundNo}/sync` | `{ status, changed, message }` |

`RefundItem = { id, refundNo, orderNo, mchRefundNo, appId, appCode, passageId, provider, amount, reason, status, upstreamRefundNo, errorMsg, source, createdAt, successAt }`，`source`：`api`（业务发起）/ `admin`（后台发起）/ `upstream`（上游/商店侧发起）。

## 11. 业务通知任务

| 方法 | 路径 | 出参 `data` |
| --- | --- | --- |
| GET | `/notify-tasks?page&pageSize&bizNo&appId&status&type&startTime&endTime` | 分页 `NotifyTaskItem` |
| POST | `/notify-tasks/{id}/retry` | `null`（立即重发，重置为待通知） |

`NotifyTaskItem = { id, appId, appCode, type, bizNo, url, payload, status, attempts, maxAttempts, nextTime, lastHttpStatus, lastResponse, lastTime, createdAt }`

## 12. 上游回调日志

`GET /upstream-notifies?page&pageSize&provider&orderNo&passageId&verified&startTime&endTime` → 分页 `UpstreamNotifyItem`

`UpstreamNotifyItem = { id, provider, passageId, kind, orderNo, verified, result, requestBody, clientIp, createdAt }`，`verified`：布尔，验签是否通过；`result`：处理结果描述。

## 13. 用户与审计

| 方法 | 路径 | 入参 | 出参 `data` |
| --- | --- | --- | --- |
| GET | `/users` | - | `[{ id, username, displayName, role, status, lastLoginAt, createdAt }]` |
| POST | `/users` | `{ username, displayName, password, role, status }` | `{ id }` |
| PUT | `/users/{id}` | `{ displayName, role, status }` | `null` |
| POST | `/users/{id}/reset-password` | `{ password }` | `null` |
| DELETE | `/users/{id}` | - | `null` |
| GET | `/audit-logs?page&pageSize&username&action&startTime&endTime` | - | 分页 `[{ id, userId, username, action, target, detail, ip, createdAt }]` |

用户管理接口仅 `admin` 可用。
