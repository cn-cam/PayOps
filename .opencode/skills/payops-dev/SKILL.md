---
name: PayOps 开发
description: 在 PayOps（自用多渠道多通道支付系统：.NET 10 + Vue3 + MySQL/Redis/RabbitMQ）仓库里开发、排障或部署时使用——包括新增/修改支付类型（微信、支付宝、抖音、各应用商店内购）、调整渠道通道路由规则、改动订单/退款/通知链路、新增管理后台接口与页面、数据库迁移、单体/Compose/K8s 部署。
---

# PayOps 开发技能

## 先建立的心智模型

**自用**：没有商户、代理、费率、结算。收款主体是公司自己。只有四个配置对象：

```text
应用 App ─1:N─> 渠道 Channel ─绑定(路由规则)─> 通道 Passage ─N:1─> 支付类型 Provider
```

- **渠道** = 应用的分发渠道（官方包、华为、OPPO、App Store…）。渠道决定能用哪些支付。
- **通道** = 某支付类型下的一套自有凭证（如"微信-商户号A"）。全局维护，可被多个渠道复用。
- **绑定** = 路由规则，字段在 `pay_channel_passage`：`methods / priority / weight / min_amount(起付金额) / max_amount / daily_limit_* / start_time / end_time`。同一渠道可绑定多个同类型通道。
- **支付方式** `payMethod` 形如 `wechat_app`，前缀即支付类型。

不要引入"商户""费率""分账"之类的概念；需求里出现"通道"一律指自有凭证，不是上游服务商。

## 仓库地图

| 路径 | 内容 |
| --- | --- |
| `src/PayOps.Core` | 实体、`IPayProvider` 契约、`PassageRouter`（路由纯函数）、`ApiSigner`、`SecretProtector`、`OrderNoGenerator`、`PayOpsOptions` |
| `src/PayOps.Providers` | 各支付类型实现；`ProviderRegistry.cs` 注册 |
| `src/PayOps.Service/Pay` | `OrderService`（下单/入账/查单/关单/巡检/续费单）、`RefundService`、`SubscriptionService`（订阅事件/退订/到期巡检）、`ReconService`（对账）、`NotifyService`（发件箱）、`UpstreamNotifyService`、`RouteService`、`AppAuthService` |
| `src/PayOps.Service/Infra` | `RedisStore`、`MqBus`、`ConfigCache`、`NodeService` |
| `src/PayOps.Service/Jobs` | `DispatchJob`（领取到期任务发 MQ）、`ConsumerJob`（消费） |
| `src/PayOps.Service/Admin` | 管理端服务与 DTO |
| `src/PayOps.Service/Data` | `Db`（Dapper）、`Migrator`、`Migrations/V###__*.sql` |
| `src/PayOps.Server` | 控制器（`Gateway/`、`Admin/`）、`Infrastructure/Web.cs`（响应包、角色裁剪、签名过滤器）、`Program.cs` |
| `web/admin` | Vue3 + Element Plus 管理后台 |
| `docs/` | 设计、库表、接入 API、管理 API 契约、部署、通道接入 |
| `deploy/` | `standalone` / `docker` / `compose` / `k8s` |
| `scripts/demo.ps1` | 端到端冒烟（模拟通道走完整链路） |

## 常用命令

```powershell
dotnet build PayOps.slnx
dotnet test tests/PayOps.Tests                    # 路由 / 签名 / 各类型验签
dotnet run --project src/PayOps.Server            # http://localhost:5080（Development，连本机 *.dev 中间件）
pwsh scripts/demo.ps1                             # 端到端冒烟，全部 [PASS] 才算没改坏
cd web/admin; npm run dev                         # http://localhost:5175
cd web/admin; npm run build                       # vue-tsc 类型检查 + 打包
kubectl kustomize deploy/k8s/overlays/demo        # 校验 K8s 清单可渲染
```

改动后端后至少跑：`dotnet test` + `scripts/demo.ps1`。改动前端后跑 `npm run build`。

## 不可破坏的约定

1. **金额是分（long），时间是 UTC**。SQL 里用 `UTC_TIMESTAMP(3)`，不要用 `NOW()`；C# 用 `DateTime.UtcNow`。
2. **状态变更只用条件更新**（`UPDATE … WHERE status = 期望值`），用受影响行数判断是否成功。不要"先查再改"。
3. **支付成功只有一个入口**：`OrderService.HandlePaidAsync`。回调、查单、凭证核验都走它；它负责幂等、金额校验、发件箱、限额计数，订阅首购还会在同一事务里建立订阅。唯一的例外是订阅续费单：由上游扣款事件经 `OrderService.RecordRenewalAsync` 直接生成为已支付。
4. **状态变更与通知任务同一事务**（`NotifyService.InsertTaskAsync` 传入同一个 conn/tx）。提交后再 `EnqueueAsync`。
5. **后台任务不选主**：领取用 `FOR UPDATE SKIP LOCKED` + 把 `next_*` 字段向后拨作为租约。新增定时任务照此模式，不要加"只允许一个副本运行"的开关。
6. **MQ 消息只带主键**，数据库是事实来源；处理器必须幂等，可重复、可丢失。
7. **Redis 失败放行**：路由状态、锁、防重放读不到时不能让下单失败（见 `RedisStore` 里的 `LogDegraded`）。
8. **不信任客户端与通知内容**：内购凭证回源核验或验签；上游回调必须验签，并校验订单属于 URL 中的通道。
9. **凭证不落日志、不进审计、不回显**：密钥字段在元数据里标 `Secret = true`。
10. **改配置表后调用 `ConfigCache.InvalidateAllAsync()`**，否则其他副本要等 15 秒 TTL。
11. **管理 API 改动先改 `docs/04-管理后台API.md`**，前后端都以它为准。
12. 任何进程内状态（静态字典、内存队列）都要问一句：多副本下还对吗？

## 任务指引

- 新增支付类型、扩展支付方式、给某类型加订阅或对账单能力 → 读 `references/add-provider.md`
- 新增管理接口/页面、数据库迁移、新增后台任务、改路由规则 → 读 `references/recipes.md`
- 部署与排障 → 读 `references/ops.md`

## 完成前自检

- [ ] `dotnet build` 0 警告 0 错误，`dotnet test` 全绿
- [ ] `pwsh scripts/demo.ps1` 全部 `[PASS]`（需要服务在运行）
- [ ] 涉及前端：`npm run build` 通过
- [ ] 涉及库表：新增了迁移脚本而不是修改已发布的脚本；`docs/02-数据库设计.md` 已同步
- [ ] 涉及接口：`docs/03` 或 `docs/04` 已同步
- [ ] 真实支付类型的改动无法本地验证时，明确告诉用户"未经真实凭证联调"
