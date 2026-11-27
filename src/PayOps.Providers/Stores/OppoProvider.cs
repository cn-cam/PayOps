using System.Security.Cryptography;
using PayOps.Core.Providers;
using PayOps.Providers.Common;

namespace PayOps.Providers.Stores;

/// <summary>OPPO 联运 SDK 支付：无服务端预下单，支付结果以 OPPO 服务端回调为准（SHA1WithRSA，固定公钥）。</summary>
public sealed class OppoProvider(IHttpClientFactory httpFactory) : PayProviderBase(httpFactory)
{
    public const string DefaultPublicKey =
        "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQCmreYIkPwVovKR8rLHWlFVw7YDfm9uQOJKL89Smt6ypXGVdrAKKl0wNYc3/jecAoPi2ylChfa2iRu5gunJyNmpWZzlCNRIau55fxGW0XEu553IiprOZcaw5OuYGlf60ga8QT6qToP0/dpiL/ZbmNUO9kUhosIjEu22uFgR+5cYyQIDAQAB";

    public override ProviderMeta Meta { get; } = new()
    {
        Code = "oppo",
        Name = "OPPO应用商店支付",
        Category = "store",
        Methods = [new("oppo_sdk", "OPPO SDK支付")],
        Capabilities = new(),
        ConfigFields =
        [
            new() { Key = "appKey", Label = "AppKey", Help = "仅用于标识，可不填" },
            new() { Key = "publicKey", Label = "OPPO回调公钥", Type = "textarea", DefaultValue = DefaultPublicKey, Help = "OPPO 官方统一公钥，一般无需修改" },
        ],
        NotifyNote = "回调地址随下单凭据返回（callbackUrl），由客户端 SDK 传给 OPPO，无需在后台配置。客户端须原样使用凭据中的 order / attach / amount。",
    };

    protected override string? ValidateKeys(PassageConfig config)
    {
        using var pub = KeyUtil.LoadRsaPublicKey(config.Get("publicKey", DefaultPublicKey));
        return null;
    }

    public override Task<PayCredential> CreateAsync(CreateContext ctx, CancellationToken ct) =>
        Task.FromResult(new PayCredential
        {
            Type = CredentialTypes.SdkParams,
            Data =
            {
                ["order"] = ctx.OrderNo,
                ["amount"] = ctx.Amount,
                ["productName"] = Truncate(ctx.Subject, 50),
                ["productDesc"] = Truncate(ctx.Body.Length > 0 ? ctx.Body : ctx.Subject, 100),
                ["callbackUrl"] = ctx.NotifyUrl,
                // attach 固定为订单号：既保证非空（避免空值签名歧义），也便于回调时二次核对
                ["attach"] = ctx.OrderNo,
            },
        });

    public override Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct)
    {
        var f = ctx.Form.Count > 0 ? ctx.Form : ctx.Query;
        string V(string k) => f.TryGetValue(k, out var v) ? v : "";
        var baseString = BaseString(V("notifyId"), V("partnerOrder"), V("productName"), V("productDesc"), V("price"), V("count"), V("attach"));
        if (!KeyUtil.RsaVerify(ctx.Config.Get("publicKey", DefaultPublicKey), baseString, V("sign"), HashAlgorithmName.SHA1))
            return Task.FromResult(NotifyParseResult.Fail("验签失败"));

        if (!long.TryParse(V("price"), out var price)) return Task.FromResult(NotifyParseResult.Fail("price 无效"));
        var count = long.TryParse(V("count"), out var c) && c > 0 ? c : 1;
        return Task.FromResult(NotifyParseResult.Of(new NotifyEvent
        {
            Type = NotifyEventType.Paid,
            OrderNo = V("partnerOrder"),
            UpstreamTradeNo = V("notifyId"),
            Amount = price * count,
            BuyerId = V("userId"),
        }));
    }

    public static string BaseString(string notifyId, string partnerOrder, string productName, string productDesc, string price, string count, string attach) =>
        $"notifyId={notifyId}&partnerOrder={partnerOrder}&productName={productName}&productDesc={productDesc}&price={price}&count={count}&attach={attach}";

    public override NotifyReply BuildReply(NotifyOutcome outcome, string message) =>
        outcome == NotifyOutcome.Success
            ? NotifyReply.Text("result=OK&resultMsg=成功")
            : NotifyReply.Text("result=FAIL&resultMsg=" + message);
}
