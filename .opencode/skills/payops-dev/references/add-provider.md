# 新增支付类型 / 扩展支付方式

## 步骤

1. **查官方文档**，确认：下单接口与签名、客户端需要的参数、回调格式与验签、应答格式、查单/关单/退款能力、金额单位、订单号长度限制（平台订单号 21 位）。拿不准的地方标注出来，不要凭印象写。
2. 在 `src/PayOps.Providers/<Name>/<Name>Provider.cs` 新建类，继承 `PayProviderBase`：

   ```csharp
   public sealed class FooProvider(IHttpClientFactory httpFactory) : PayProviderBase(httpFactory)
   {
       public override ProviderMeta Meta { get; } = new()
       {
           Code = "foo",                       // 小写，全局唯一
           Name = "Foo 支付",
           Category = "direct",                // direct | store | mock
           Methods = [new("foo_app", "Foo APP 支付")],   // 方式编码必须以 Code 开头
           Capabilities = new() { Query = true, Refund = true },
           ConfigFields =
           [
               new() { Key = "appId", Label = "AppID", Required = true },
               new() { Key = "secret", Label = "密钥", Type = "password", Required = true, Secret = true },
           ],
           NotifyNote = "回调地址需要配置到哪里",
       };

       public override Task<PayCredential> CreateAsync(CreateContext ctx, CancellationToken ct) { … }
       public override Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct) { … }
       // 按能力覆写：QueryAsync / CloseAsync / RefundAsync / QueryRefundAsync / VerifyReceiptAsync / BuildReply / ValidateKeys
   }
   ```

3. 在 `ProviderRegistry.cs` 的 `AddPayProviders` 里加一行 `services.AddSingleton<IPayProvider, FooProvider>();`。
   后台"通道管理"的配置表单、绑定页的支付方式选项、路由都会自动出现，**不需要改前端**。
4. 在 `tests/PayOps.Tests/ProviderTests.cs` 加测试：用测试内生成的密钥构造一份回调 → 断言解析出的订单号/金额/交易号；篡改后必须验签失败。把新类型加进 `Every_provider_declares_consistent_metadata`。
5. 在 `docs/06-通道接入指南.md` 与 `docs/03-业务接入API.md` 的支付方式表补充说明。

## 实现要点

| 方法 | 要点 |
| --- | --- |
| `CreateAsync` | 上游订单号用 `ctx.OrderNo`；回调地址用 `ctx.NotifyUrl`；过期时间用 `ctx.ExpireAt`（UTC）。请求参数本身的问题抛 `BadRequest(...)`（`RequestFault=true`，不会故障转移）；上游/网络问题抛 `Upstream(code, msg)` 或让 `SendAsync` 抛出（会故障转移并计入熔断）。无需预下单的商店 SDK 直接返回客户端参数即可 |
| `ParseNotifyAsync` | **先验签**，失败返回 `NotifyParseResult.Fail(...)`；非成功状态返回 `Ignore(...)`；成功返回 `Of(new NotifyEvent { Type = Paid, OrderNo, UpstreamTradeNo, Amount, PaidAt })`。`Amount` 是分；拿不到可比金额（如外币、档位价）就填 `null` 表示不校验。表单回调读 `ctx.Form`，JSON 读 `ctx.Body`，GET 读 `ctx.Query` |
| `BuildReply` | 按上游要求的应答格式返回。默认是文本 `success` / `fail`(500) |
| `QueryAsync` | 返回 `UpstreamState`；订单不存在视为 `NotPaid`，不要抛异常 |
| `RefundAsync` | 同步成功返回 `Success`；受理中返回 `Processing`（巡检任务会调 `QueryRefundAsync`）；退款回调 URL 为 `ctx.NotifyUrl`（以 `/refund` 结尾） |
| `VerifyReceiptAsync` | 仅内购。**回源核验或验签**，返回 `BoundOrderNo`（凭证里绑定的平台订单号）与 `ProductId`，由 `OrderService` 做防串单与一证一用校验 |
| 通知无签名的平台 | 只把通知当触发器，从中取出 token 回源查询（参考 `HuaweiProvider`） |
| 访问令牌 | 进程内缓存即可（参考 `HuaweiProvider.Tokens`），各副本各自缓存 |

## 通用工具（`Providers/Common/ProviderBase.cs`）

`KeyUtil`（RSA 加载/签名/验签，支持 PEM 与裸 Base64、MD5、Base64Url）、`JsonExt`（`Str` / `Long` / `Obj` 宽松取值）、`TimeUtil`（北京时间互转、Unix 时间）、`Money`（分 ↔ 元字符串）、`SendAsync`（统一网络异常）。

## 给已有类型加支付方式

在 `Meta.Methods` 加一项，并在 `CreateAsync` 的 `switch` 里处理。已有绑定若 `methods` 为空会自动包含新方式，注意是否符合预期。
