# 业务接入 API

面向公司内部业务服务端（服务端到服务端）。客户端 App 不直接调用本系统：客户端 → 业务服务端 → PayOps。

## 1. 约定

- 基础地址：`{PublicBaseUrl}`，全部 JSON，UTF-8。
- 响应包：`{ "code": 0, "message": "ok", "data": {...} }`；`code != 0` 为失败，`errorCode` 为机器可读错误码，`message` 可直接展示。
- 业务失败返回 HTTP 200 + `code=1`；鉴权失败返回 HTTP 401；系统异常返回 HTTP 500。
- 金额单位：分（整数）。时间：ISO-8601 UTC。

### 1.1 签名

每个请求带 4 个头：

| 头 | 说明 |
| --- | --- |
| `X-App-Id` | 应用编码（后台"应用管理"里的 appCode） |
| `X-Timestamp` | 秒级 Unix 时间戳，与服务器相差不超过 300 秒 |
| `X-Nonce` | 随机串（≤64），时间窗内不可重复 |
| `X-Sign` | `hex(HMAC-SHA256(appSecret, 签名串))`，小写 |

```text
签名串 = METHOD + "\n" + 路径与查询串（如 /api/pay/orders?orderNo=P1） + "\n" + X-Timestamp + "\n" + X-Nonce + "\n" + 请求体原文（GET 为空串）
```

C# 示例（`PayOps.Core.Security.ApiSigner` 即服务端使用的实现）：

```csharp
var body = JsonSerializer.Serialize(request);
var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
var nonce = Guid.NewGuid().ToString("N");
var sign = ApiSigner.Sign(appSecret, "POST", "/api/pay/orders", ts, nonce, body);
```

PowerShell 示例见 `scripts/demo.ps1` 的 `Gateway` 函数。

## 2. 统一下单

`POST /api/pay/orders`

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `channel` | 是 | 渠道编码，如 `official` / `huawei` / `appstore` |
| `payMethod` | 是 | 支付方式，见下表 |
| `mchOrderNo` | 是 | 业务订单号（应用内唯一，≤64）。**每次支付尝试使用新的单号** |
| `amount` | 是 | 金额（分） |
| `subject` | 是 | 商品标题（≤128） |
| `body` | 否 | 商品描述 |
| `userId` | 否 | 业务用户 ID（透传、便于检索） |
| `openId` | 视方式 | 微信 JSAPI/小程序必填（openid）；小米可传 uid 以支持主动查单 |
| `productId` | 视方式 | 应用商店内购商品 ID（华为/荣耀/苹果必填） |
| `clientIp` | 否 | 用户 IP（微信 H5 需要） |
| `notifyUrl` | 否 | 本单的业务通知地址，缺省用应用的默认地址 |
| `returnUrl` | 否 | 支付宝网页支付完成后的跳转地址 |
| `attach` | 否 | 透传数据（≤512），通知时原样返回 |
| `expireSeconds` | 否 | 订单有效期，默认 1800 |
| `subscribe` | 否 | `true` = 自动续费订阅的首购（华为 / 荣耀 / 苹果 / 小米），见第 4 节 |
| `signAgreement` | 否 | `true` = 签约代扣的签约单（微信 / 支付宝 / 抖音），返回拉起签约的凭据，不产生支付，见第 5 节 |
| `extra` | 否 | 附加参数。签约代扣用 `{ "periodDays": "30" }` 指定扣款周期（默认 30 天） |

| payMethod | 说明 | `credential.type` → 客户端用法 |
| --- | --- | --- |
| `wechat_app` | 微信 APP | `app_params` → 传给微信 SDK |
| `wechat_jsapi` | 公众号 / 小程序 | `jsapi_params` → `wx.requestPayment` |
| `wechat_native` | 扫码 | `qr_code` → `codeUrl` 生成二维码 |
| `wechat_h5` | H5 | `redirect_url` → 跳转 `url` |
| `alipay_app` | 支付宝 APP | `order_string` → 传给支付宝 SDK |
| `alipay_wap` / `alipay_page` | 手机 / 电脑网站 | `redirect_url` |
| `alipay_qr` | 当面付扫码 | `qr_code` |
| `douyin_app` | 抖音支付 APP | `app_params` → 传给抖音支付 OpenSDK（`package` 固定为 `Sign=DYPay`） |
| `douyin_h5` | 抖音支付 H5 | `redirect_url` → 跳转 `url`（5 分钟内有效；发起页域名须为已配置的 H5 支付域名） |
| `douyin_pc` | 抖音支付 PC 扫码（Native） | `qr_code` → `codeUrl` 生成二维码（**2 分钟内有效**，过期用相同参数重新下单即可刷新） |
| `wechat_papay` | 微信免密支付/自动续费 | 签约时 `sdk_params`（`preEntrustwebId` + 小程序参数，2 分钟内用 `WXLaunchMiniProgram` 拉起） |
| `alipay_agreement` | 支付宝周期扣款 | 签约时 `redirect_url`（协议页） |
| `douyin_deduct` | 抖音免密支付/商户代扣 | 签约时 `sdk_params`（`pre_entrustweb_id` + 调起签名） |
| `xiaomi_sdk` + `subscribe` | 小米自动续费订阅 | `sdk_params`，`clientApi = miSubscribe`（普通支付为 `miUniPay`） |
| `douyinmp_ecpay` | 抖音小程序担保支付 | `sdk_params` → `tt.pay({ orderInfo: { order_id, order_token } })` |
| `huawei_iap` | 华为 IAP | `iap` → `productId` + `developerPayload` 传给 SDK |
| `honor_iap` | 荣耀 IAP | `iap` → `productId` + `bizOrderNo` / `developerPayload` |
| `apple_iap` | 苹果 IAP | `iap` → `productId` + `appAccountToken` 传给 `purchase(options:)` |
| `oppo_sdk` | OPPO | `sdk_params` → `order / amount / productName / productDesc / callbackUrl / attach` |
| `vivo_sdk` | vivo | `sdk_params` → 含 `vivoSignature` 的全部字段 |
| `xiaomi_sdk` | 小米 | `sdk_params` → `cpOrderId / cpUserInfo / amount` |
| `mock` | 模拟（联调） | `mock` |

响应 `data`：

```json
{
  "orderNo": "P261127120000021000037",
  "mchOrderNo": "B20261127001",
  "status": 0,
  "amount": 600,
  "provider": "wechat",
  "payMethod": "wechat_app",
  "passageId": 3,
  "passageName": "微信支付-商户号A",
  "expireAt": "2026-11-27T04:30:00Z",
  "credential": { "type": "app_params", "data": { "appid": "wx...", "prepayid": "..." } }
}
```

幂等：相同 `mchOrderNo` 且参数一致、未过期时返回同一份凭据；已支付返回 `ORDER_PAID`；参数不一致或已关闭返回 `ORDER_EXISTS`。

## 3. 其余接口

| 接口 | 说明 |
| --- | --- |
| `GET /api/pay/orders?orderNo=…` 或 `?mchOrderNo=…`，可加 `&sync=true` | 查询订单；`sync=true` 时对待支付订单主动查询上游 |
| `POST /api/pay/orders/close` `{ orderNo \| mchOrderNo }` | 关闭待支付订单（会先查上游，已支付则返回 `ORDER_PAID`） |
| `POST /api/pay/orders/verify` `{ orderNo \| mchOrderNo, receipt: {...} }` | 内购凭证核验。华为/荣耀：`{ "purchaseToken": "...", "productId": "..." }`；苹果：`{ "transactionId": "..." }` 或 `{ "signedTransaction": "<JWS>" }` |
| `POST /api/pay/refunds` `{ orderNo \| mchOrderNo, mchRefundNo, amount, reason, notifyUrl }` | 退款（微信/支付宝/抖音）。`mchRefundNo` 幂等；可多次部分退款 |
| `GET /api/pay/refunds?refundNo=…` 或 `?mchRefundNo=…`，可加 `&sync=true` | 查询退款 |
| `GET /api/pay/methods?channel=…&amount=…` | 该渠道在此金额下当前可用的支付方式（收银台据此展示按钮） |

订单 `status`：0 待支付，1 成功，2 关闭，3 失败。退款 `status`：0 处理中，1 成功，2 失败。

## 4. 自动续费订阅

适用于应用商店的自动续费订阅型商品（华为、荣耀、苹果；模拟通道可用于联调）。模型：**一个订阅 = 一个订阅号 `subNo` + 每期一笔订单**。

| 环节 | 做法 |
| --- | --- |
| 首购 | `POST /api/pay/orders` 带 `subscribe: true` 与 `productId` → 客户端在商店完成订阅 → `POST /api/pay/orders/verify` 提交凭证（华为需同时提交 `purchaseToken` 与 `subscriptionId`；苹果提交 `transactionId`）。入账后订单的 `orderType = 1`，并生成 `subNo` |
| 续订 | 无需业务发起。商店每期扣款成功 → 系统自动生成一笔 `orderType = 2` 的续费单并顺延周期 → 业务收到一条普通的支付通知（带 `subNo` 与 `subscription`） |
| 退订 | 用户在商店侧退订 → 系统经商店通知同步 → 业务收到 `type = "subscription"`、`event = "canceled"`。也可调用 `POST /api/pay/subscriptions/cancel`（华为、荣耀支持服务端退订；苹果不提供此能力，会返回 `NOT_SUPPORTED` 并提示引导用户到系统订阅管理页） |
| 恢复续订 / 到期 / 撤销 | 均由商店通知或到期巡检驱动，业务收到 `event = resumed / expired / revoked` |

退订只是停止后续续订，**当前已付费周期内订阅仍然有效**（`status = 1`、`autoRenew = false`），到期后才变为 `status = 2`。

| 接口 | 说明 |
| --- | --- |
| `GET /api/pay/subscriptions?subNo=…`（或 `?orderNo=` / `?mchOrderNo=` 用首购单定位），可加 `&sync=true` | 查询订阅；`sync=true` 时向商店查询并同步最新状态 |
| `POST /api/pay/subscriptions/cancel` `{ subNo \| orderNo \| mchOrderNo }` | 服务端退订，幂等 |

订阅对象：`{ subNo, channel, provider, mode, productId, nextProductId, userId, status, autoRenew, amount, periodStart, expiresAt, cancelAt, pausedAt, nextChargeAt, renewCount, firstOrderNo, lastOrderNo, attach, createdAt }`。`status`：1 生效中，2 已过期，3 已撤销（退款/撤回），4 已暂停。`mode`：0 应用商店订阅，1 签约代扣。

升级 / 降级 / 暂停同样由商店通知驱动：`upgraded`（本期商品已变，`productId` 更新）、`downgraded`（下一期生效，看 `nextProductId`）、`paused`、`resumed-from-pause`。苹果没有暂停；荣耀没有降级与暂停。

业务侧建议：以 `expiresAt` 作为权益到期时间；每收到一笔 `orderType` 为 1 或 2 的支付通知就把权益延长到通知里的 `subscription.expiresAt`；收到 `expired` / `revoked` 时回收权益。

## 5. 签约代扣（自有渠道周期扣款）

微信 / 支付宝 / 抖音没有"商店订阅"，要自己签约再扣款。业务侧只需要两件事：发起签约、拿结果。

```text
1) 下单：POST /api/pay/orders { channel, payMethod: "wechat_papay" | "alipay_agreement" | "douyin_deduct",
                                mchOrderNo, amount, subject, productId?, userId, signAgreement: true,
                                extra: { "periodDays": "30" } }
   → data.orderType = 3，data.credential 是拉起签约的凭据（微信/抖音给小程序的调起参数，支付宝给协议页 URL）
2) 用户完成签约后，上游回调落库：签约单变为已支付、建立订阅（mode=1）……
3) 业务收到 type=subscription、event=signed 的通知，里面带 subNo、amount、expiresAt（= 下次扣款时间）
4) 之后每期由系统自动扣款，成功会再推一条支付通知（orderType=2，续费单）
5) 用户退订：POST /api/pay/subscriptions/cancel，系统调上游解约并推 event=canceled
```

- 扣款时间由 `expiresAt`（= `nextChargeAt`）驱动；每成功一期顺延 `periodDays`，失败按 1/2/3/7/15 天退避并累计失败次数。
- 微信需要通道上额外配置 APIv2 密钥与委托代扣模板 `plan_id`；支付宝需要协议场景码 `sign_scene`，单笔上限 100 元、仅 7:00–22:00 可扣；抖音需要 `plan_id`。
- 扣款是真实花钱：初始化阶段建议先把订阅 `autoRenew` 关掉（退订会解约，因此更推荐先在后台确认每笔扣款），确认无误后再开启自动周期。

## 6. 业务通知

支付成功、退款成功/失败时，PayOps 向业务通知地址 `POST` JSON，并带与请求相同算法的签名头（`METHOD=POST`，路径为通知地址的路径与查询串）。

```json
{
  "type": "pay",
  "appId": "demo",
  "orderNo": "P261127120000021000037",
  "mchOrderNo": "B20261127001",
  "channel": "official",
  "provider": "wechat",
  "payMethod": "wechat_app",
  "amount": 600,
  "currency": "CNY",
  "orderType": 0,
  "subNo": "",
  "subscription": null,
  "status": 1,
  "upstreamTradeNo": "4200001234202611270000000001",
  "userId": "u1001",
  "productId": "",
  "attach": "",
  "paidAt": "2026-11-27T04:00:00Z"
}
```

订阅的首购单与续费单也走这条支付通知：`orderType` 为 1 或 2，`subNo` 为订阅号，`subscription` 为 `{ subNo, status, autoRenew, periodStart, expiresAt, renewCount }`。续费单的 `mchOrderNo` 由系统生成（`RENEW-` + 上游交易号）。

退款通知 `type = "refund"`，包含 `refundNo / mchRefundNo / refundAmount / refundStatus / source / reason / errorMsg / refundedAt`。`source = "upstream"` 表示用户在应用商店侧发起的退款（业务侧应回收权益）。

订阅状态变更通知 `type = "subscription"`，`event` 为 `signed`（签约成功）/ `canceled`（退订）/ `resumed`（恢复续订）/ `resumed-from-pause`（暂停后恢复）/ `expired`（到期）/ `revoked`（撤销）/ `upgraded`（升级）/ `downgraded`（降级）/ `paused`（暂停），并带订阅对象的全部字段（`mode`、`productId`、`nextProductId` 等）。

接收方要求：

1. **验签**后再处理（参考 `DemoMerchantController`）；
2. 处理成功返回 HTTP 2xx 且响应体为 `success`；否则按 15s / 30s / 1m / 3m / 10m / 30m / 1h / 2h / 6h 重试；
3. **按 `orderNo` / `refundNo` 幂等**（订阅事件按 `subNo + event + 订阅当前状态` 判断）——通知语义为"至少一次"，可能重复。

### MQ 事件（可选）

内部系统也可以直接订阅 RabbitMQ：交换机 `payops.events`（topic），路由键 `pay.success` / `refund.success` / `refund.failed` / `subscription.signed` / `subscription.canceled` / `subscription.resumed` / `subscription.resumed-from-pause` / `subscription.expired` / `subscription.revoked` / `subscription.upgraded` / `subscription.downgraded` / `subscription.paused`，消息体与 HTTP 通知一致。自行声明队列并绑定即可。

## 7. 错误码

| errorCode | 含义 |
| --- | --- |
| `UNAUTHORIZED` / `SIGN_ERROR` / `APP_DISABLED` | 鉴权失败（HTTP 401） |
| `INVALID_PARAM` | 参数错误 |
| `CHANNEL_NOT_FOUND` | 渠道不存在或停用 |
| `NO_AVAILABLE_PASSAGE` | 没有满足条件的通道（message 中列出每个通道被排除的原因） |
| `PASSAGE_ERROR` | 尝试过的通道全部下单失败 |
| `ORDER_NOT_FOUND` / `ORDER_EXISTS` / `ORDER_PAID` / `ORDER_STATE_ERROR` | 订单类 |
| `REFUND_NOT_FOUND` / `REFUND_EXCEEDED` / `CONFLICT` | 退款类 |
| `RECEIPT_INVALID` | 内购凭证无效 / 不属于该订单 / 已被使用 |
| `NOT_SUPPORTED` | 该支付类型不支持此操作（如商店内购不支持接口退款、苹果不支持服务端退订、非内购类型不支持订阅） |
| `SYSTEM_BUSY` / `INTERNAL_ERROR` | 可重试 |
