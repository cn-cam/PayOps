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

## 订阅能力（`Capabilities.Subscription`）

- 首购：`CreateAsync` 收到 `ctx.Subscribe` 时返回商店需要的参数；`VerifyReceiptAsync` 与"扣款成功"通知在结果里带上 `SubscriptionInfo`（`UpstreamSubId` 必须是各期不变的订阅标识；`Token` 是后续查询/退订要用的凭证；`Amount` 仅记录不校验）。
- 通知：首购和续费都上报 `NotifyEvent { Type = Subscription, SubKind = Charged, UpstreamTradeNo = 本期交易号, OrderNo = 凭证里绑定的首购订单号（没有就留空）}`，由 `SubscriptionService` 按订阅是否已存在区分；退订/恢复/到期/撤销分别用 `AutoRenewOff / AutoRenewOn / Expired / Revoked`。
- 退款：订阅的每期扣款对应不同订单，退款事件的 `OrderNo` 留空、`UpstreamTradeNo` 填被退的那一期交易号，服务层按上游交易号反查订单。
- `QuerySubscriptionAsync`：到期巡检用。能确定就返回 `Known = true` 以及 `Active / AutoRenew / ExpiresAt / LatestTradeNo`；不能主动查询就保持默认（`Known = false`，到期 1 天后自动判过期）。
- `CancelSubscriptionAsync`：商店支持服务端退订才实现并声明 `SubscriptionCancel = true`；否则保持默认（抛 NOT_SUPPORTED，提示引导用户到商店退订）。
- 模拟通道（`MockProvider`）是参考实现，`scripts/demo.ps1` 第 7 步是端到端用例。

## 对账单能力（`Capabilities.Bill`）

- 实现 `DownloadBillAsync(BillContext)`：按 `ctx.BillDate` 下载并解析为 `BillItem` 列表（支付行：`OrderNo` + `UpstreamTradeNo` + 订单金额；退款行：`RefundNo` + 退款金额；金额一律为正的分）。`ctx.LocalItems` 只给模拟通道用，真实类型必须忽略。
- 当日无交易返回空列表；账单尚未生成抛 `ProviderException`（`ReconService` 会按间隔重试）。
- 把"下载"和"解析"分开，解析写成 `public static List<BillItem> ParseBill(string csv)` 以便单测；用 `BillCsv.Rows` 按**表头名**取列（各家都可能增减列），`BillCsv.Decode` 处理 UTF-8/GBK，`BillCsv.Gunzip` 处理压缩，`BillCsv.Fen` 把元转分。
- 只把终态成功的退款行计入（账单里的退款状态是出账时的快照）。

## 签约代扣能力（`Capabilities.Deduct`）

- 签约单：`CreateAsync` 收到 `ctx.SignAgreement` 时返回拉起签约的凭据（不产生支付）；上游的签约通知上报 `NotifyEvent { Type = Agreement, OrderNo = 签约单号, Agreement = { AgreementId, PayerId, SignedAt, Signed } }`，服务层据此把签约单置为成功并建立 `mode = 1` 的订阅。
- 扣款：`DeductAsync`，同步成功返回 `Paid = true`（支付宝可拿到 `trade_no`）；只返回"受理"的（微信 `pappayapply`、抖音 `deduct/payapply`）必须返回 `Paid = false`，由扣款结果通知（`Subscription/Charged`）入账。
- 解约：`CancelAgreementAsync`。查询：`QuerySubscriptionAsync`。
- 注意各家的枚举陷阱：微信 `contract_state=0` 已签约、抖音 `contract_status=0` 已签约（语义不同别写反）。
- 参考实现：`MockProvider`（含签约/事件模拟）与 `WechatProvider` 的 v2 部分（`Common/WechatV2.cs`）。

## 资金账单（`Capabilities.FundBill`）

- `DownloadFundBillAsync` 返回 `FundBillSummary { Income, Expense, Refund, Fee }`（分，收入/支出均为正数）。手续费在资金账单里是独立行：扣除交易手续费 = 支出、退还交易手续费 = 收入。
- 用 `BillCsv.Rows(..., stopFirstField: "资金流水总笔数")` 按表头名解析，`BillCsv.Gunzip/Decode` 处理压缩与编码。
- 支付宝资金账单（`bill_type=signcustomer`）官方未公开列定义，因此不实现（`FundBill = false`）。

## 手续费

- 交易账单的每行手续费放进 `BillItem.Fee`（保留上游正负号：支付为正、退款为负）。通道上配置 `fee_rate_bp` 后，`ReconService` 会按"金额 × 费率"算应收并与上游合计比较；容差 `PayOps:Recon:FeeTolerance`。

## 通用工具（`Providers/Common/ProviderBase.cs`）

`KeyUtil`（RSA 加载/签名/验签，支持 PEM 与裸 Base64、MD5、Base64Url）、`JsonExt`（`Str` / `Long` / `Obj` 宽松取值）、`TimeUtil`（北京时间互转、Unix 时间）、`Money`（分 ↔ 元字符串）、`SendAsync`（统一网络异常）。

## 给已有类型加支付方式

在 `Meta.Methods` 加一项，并在 `CreateAsync` 的 `switch` 里处理。已有绑定若 `methods` 为空会自动包含新方式，注意是否符合预期。
