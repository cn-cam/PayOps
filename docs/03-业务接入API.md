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

| payMethod | 说明 | `credential.type` → 客户端用法 |
| --- | --- | --- |
| `wechat_app` | 微信 APP | `app_params` → 传给微信 SDK |
| `wechat_jsapi` | 公众号 / 小程序 | `jsapi_params` → `wx.requestPayment` |
| `wechat_native` | 扫码 | `qr_code` → `codeUrl` 生成二维码 |
| `wechat_h5` | H5 | `redirect_url` → 跳转 `url` |
| `alipay_app` | 支付宝 APP | `order_string` → 传给支付宝 SDK |
| `alipay_wap` / `alipay_page` | 手机 / 电脑网站 | `redirect_url` |
| `alipay_qr` | 当面付扫码 | `qr_code` |
| `douyin_mp` | 抖音小程序担保支付 | `sdk_params` → `tt.pay({ orderInfo: { order_id, order_token } })` |
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

## 4. 业务通知

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
  "status": 1,
  "upstreamTradeNo": "4200001234202611270000000001",
  "userId": "u1001",
  "productId": "",
  "attach": "",
  "paidAt": "2026-11-27T04:00:00Z"
}
```

退款通知 `type = "refund"`，包含 `refundNo / mchRefundNo / refundAmount / refundStatus / source / reason / errorMsg / refundedAt`。`source = "upstream"` 表示用户在应用商店侧发起的退款（业务侧应回收权益）。

接收方要求：

1. **验签**后再处理（参考 `DemoMerchantController`）；
2. 处理成功返回 HTTP 2xx 且响应体为 `success`；否则按 15s / 30s / 1m / 3m / 10m / 30m / 1h / 2h / 6h 重试；
3. **按 `orderNo` / `refundNo` 幂等**——通知语义为"至少一次"，可能重复。

### MQ 事件（可选）

内部系统也可以直接订阅 RabbitMQ：交换机 `payops.events`（topic），路由键 `pay.success` / `refund.success` / `refund.failed`，消息体与 HTTP 通知一致。自行声明队列并绑定即可。

## 5. 错误码

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
| `NOT_SUPPORTED` | 该支付类型不支持此操作（如商店内购不支持接口退款） |
| `SYSTEM_BUSY` / `INTERNAL_ERROR` | 可重试 |
