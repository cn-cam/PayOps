# PayOps 仓库约定

自用多渠道多通道支付系统（.NET 10 + Vue3 + MySQL / Redis / RabbitMQ）。动手前先加载技能 `payops-dev`（`.opencode/skills/payops-dev/SKILL.md`），其中有心智模型、不可破坏的约定与各类改动的配方。

- 概念：应用 → 渠道 → （绑定 = 路由规则）→ 通道 → 支付类型。没有商户 / 费率 / 结算。
- 金额单位分、时间 UTC；状态变更只用条件更新；支付成功只走 `OrderService.HandlePaidAsync`。
- 后台任务不选主（`SKIP LOCKED` + 租约）；任何进程内状态都要考虑多副本。
- 验证：`dotnet test tests/PayOps.Tests`，服务运行时 `pwsh scripts/demo.ps1` 必须全部 `[PASS]`；前端 `npm run build`。
- 管理 API 以 `docs/04-管理后台API.md` 为契约，改接口先改文档。
- 真实支付类型无法本地联调，改动后如实说明"未经真实凭证验证"。
