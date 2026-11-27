# 常见改动配方

## 新增管理后台接口 + 页面

1. 在 `docs/04-管理后台API.md` 写清路径、入参、出参。
2. 服务：在 `src/PayOps.Service/Admin/` 对应的 `Admin*Service` 加方法；DTO 放 `AdminModels.cs`。列表用 `db.PageAsync<T>(select, from, new SqlWhere().Add("col = {0}", value), orderBy, page, pageSize)`——`SqlWhere.Add` 在值为 null/空串时自动跳过条件。
3. 控制器：在 `src/PayOps.Server/Controllers/Admin/` 继承 `AdminControllerBase`，返回 `Done(data)`；写操作加 `[Authorize(Roles = WriteRoles)]` 并调用 `await AuditAsync("动作", "对象", 详情)`（详情里不能有凭证）。业务错误抛 `PayException(ErrorCodes.X, "可展示的中文")`。
4. 前端：`web/admin/src/api/` 加类型与请求函数，`src/views/` 加页面，`src/router` 注册。金额输入用元、提交转分；时间显示本地、提交 ISO UTC。
5. `npm run build` 必须通过。

响应包固定为 `{ code, message, data, errorCode }`，业务错误 HTTP 200 + `code=1`。JSON 为 camelCase；DTO 属性直接用 PascalCase。

## 数据库变更

- **不要修改已发布的迁移脚本**。新建 `src/PayOps.Service/Data/Migrations/V00N__描述.sql`（文件名按字典序执行，自动内嵌）。
- 脚本要能与旧版本代码共存（滚动发布期间新旧副本同时在线）：先加可空列/带默认值的列，下个版本再收紧。
- 列名 snake_case，实体属性 PascalCase（Dapper 已开启下划线匹配）；`Db.InsertAsync` 按属性名自动生成列名，新增实体要在 `Db.Tables` 登记表名。
- 布尔列用 `TINYINT(1)`；时间用 `DATETIME(3)` 存 UTC；金额 `BIGINT`。
- 同步更新 `docs/02-数据库设计.md`。

## 新增后台定时任务

照 `OrderService.ClaimDueChecksAsync` + `DispatchJob` + `ConsumerJob` 的模式：

1. 表上要有"到期时间"列（兼作租约）与 `(status, 到期时间)` 索引；
2. 领取：事务内 `SELECT … WHERE status = x AND due <= UTC_TIMESTAMP(3) ORDER BY due LIMIT n FOR UPDATE SKIP LOCKED`，随即把 `due` 向后拨一个租约时长；
3. 把主键发到 MQ（`MqBus.PublishAsync(MqBus.CheckQueue, "前缀:" + key)`），在 `ConsumerJob.HandleCheckAsync` 里按前缀分发；
4. 处理函数必须幂等，结束时要么把状态推进到终态，要么写下一次 `due`。

不要用"分布式锁选一个副本跑定时任务"的方式——那会让 Worker 无法水平扩展。

## 调整路由规则

- 算法只在 `PayOps.Core/Routing/PassageRouter.cs`。新增过滤条件：`RouteCandidate` 加字段 → `Check` 加判断并返回中文原因 → `ConfigCache.ToCandidate` 映射 → 库表与 `BindingInput` 加字段 → `AdminConfigService.ToEntity` 校验。
- 需要多副本共享的运行时数据（计数、状态）放 Redis，经 `RedisStore.GetRuntimeAsync` 读入 `RouteRuntime`，读失败要能放行。
- 每条规则在 `tests/PayOps.Tests/CoreTests.cs` 的 `PassageRouterTests` 加用例；权重类断言用固定种子 + 区间。
- 后台"路由模拟"自动复用同一算法，无需另改。

## 改订单 / 退款 / 通知链路

- 入账只走 `OrderService.HandlePaidAsync`；退款终态只走 `RefundService.MarkSuccessAsync / MarkFailedAsync`。
- 新增订单状态要同时检查：条件更新的 `WHERE status`、`ClaimDueChecksAsync` 的筛选、后台与前端的枚举、`docs/03`、`docs/04`。
- 业务通知载荷在 `NotifyService.BuildPayPayload / BuildRefundPayload`，只能加字段，不能改名或删除（业务方已接入）。
- 可退余额只通过 `pay_order.refund_amount` 的原子条件更新占用/释放。
