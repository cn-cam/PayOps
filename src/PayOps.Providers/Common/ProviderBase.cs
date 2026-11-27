using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using PayOps.Core.Providers;

namespace PayOps.Providers.Common;

public abstract class PayProviderBase(IHttpClientFactory httpFactory) : IPayProvider
{
    public const string HttpClientName = "payops-upstream";

    public abstract ProviderMeta Meta { get; }

    protected HttpClient Http => httpFactory.CreateClient(HttpClientName);

    public virtual string? ValidateConfig(PassageConfig config)
    {
        foreach (var f in Meta.ConfigFields)
            if (f.Required && config.Get(f.Key).Length == 0)
                return $"缺少必填项：{f.Label}";
        try
        {
            return ValidateKeys(config);
        }
        catch (Exception ex)
        {
            return "密钥格式不正确：" + ex.Message;
        }
    }

    /// <summary>子类在此尝试加载密钥，格式不对直接抛异常。</summary>
    protected virtual string? ValidateKeys(PassageConfig config) => null;

    public abstract Task<PayCredential> CreateAsync(CreateContext ctx, CancellationToken ct);

    public virtual Task<QueryResult> QueryAsync(OrderContext ctx, CancellationToken ct) =>
        Task.FromResult(new QueryResult { State = UpstreamState.Unknown, Message = "该通道不支持主动查单" });

    public virtual Task CloseAsync(OrderContext ctx, CancellationToken ct) => Task.CompletedTask;

    public virtual Task<RefundResult> RefundAsync(RefundContext ctx, CancellationToken ct) =>
        throw new ProviderException("NOT_SUPPORTED", $"{Meta.Name}不支持接口退款，请在对应商店后台处理", requestFault: true);

    public virtual Task<RefundResult> QueryRefundAsync(RefundContext ctx, CancellationToken ct) =>
        throw new ProviderException("NOT_SUPPORTED", $"{Meta.Name}不支持退款查询", requestFault: true);

    public virtual Task<QueryResult> VerifyReceiptAsync(ReceiptContext ctx, CancellationToken ct) =>
        throw new ProviderException("NOT_SUPPORTED", $"{Meta.Name}不需要凭证核验，支付结果以服务端回调为准", requestFault: true);

    public virtual Task<SubscriptionState> QuerySubscriptionAsync(SubscriptionContext ctx, CancellationToken ct) =>
        Task.FromResult(new SubscriptionState { Known = false, Message = "该通道不支持主动查询订阅状态" });

    public virtual Task CancelSubscriptionAsync(SubscriptionContext ctx, CancellationToken ct) =>
        throw new ProviderException("NOT_SUPPORTED", $"{Meta.Name}不支持服务端退订，请引导用户在对应商店的订阅管理页退订，退订结果会经通知同步", requestFault: true);

    public virtual Task<List<BillItem>> DownloadBillAsync(BillContext ctx, CancellationToken ct) =>
        throw new ProviderException("NOT_SUPPORTED", $"{Meta.Name}不支持下载对账单", requestFault: true);

    public virtual Task<FundBillSummary> DownloadFundBillAsync(BillContext ctx, CancellationToken ct) =>
        throw new ProviderException("NOT_SUPPORTED", $"{Meta.Name}不支持下载资金账单", requestFault: true);

    public virtual Task<DeductResult> DeductAsync(DeductContext ctx, CancellationToken ct) =>
        throw new ProviderException("NOT_SUPPORTED", $"{Meta.Name}不支持签约代扣", requestFault: true);

    public virtual Task CancelAgreementAsync(SubscriptionContext ctx, CancellationToken ct) =>
        throw new ProviderException("NOT_SUPPORTED", $"{Meta.Name}不支持服务端解约", requestFault: true);

    public abstract Task<NotifyParseResult> ParseNotifyAsync(NotifyContext ctx, CancellationToken ct);

    public virtual NotifyReply BuildReply(NotifyOutcome outcome, string message) =>
        outcome == NotifyOutcome.Success ? NotifyReply.Text("success") : NotifyReply.Text("fail", 500);

    protected static ProviderException Upstream(string code, string message) => new(code, message);

    protected static ProviderException BadRequest(string message) => new("INVALID_PARAM", message, requestFault: true);

    protected async Task<(int Status, string Body)> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var resp = await Http.SendAsync(request, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            return ((int)resp.StatusCode, body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            throw new ProviderException("UPSTREAM_UNREACHABLE", $"{Meta.Name}网络异常：{ex.Message}", inner: ex);
        }
    }

    protected static JsonElement ParseJson(string body, string providerName)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ProviderException("UPSTREAM_BAD_RESPONSE", $"{providerName}返回了无法解析的内容：{Truncate(body, 200)}");
        }
    }

    protected static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

public static class JsonExt
{
    public static string Str(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null or JsonValueKind.Undefined => "",
            _ => v.GetRawText(),
        };
    }

    public static long? Long(this JsonElement e, string name)
    {
        var s = e.Str(name);
        if (s.Length == 0) return null;
        if (long.TryParse(s, out var n)) return n;
        return decimal.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? (long)d
            : null;
    }

    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;
}

public static class KeyUtil
{
    public static RSA LoadRsaPrivateKey(string text)
    {
        var rsa = RSA.Create();
        text = text.Trim();
        if (text.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            rsa.ImportFromPem(text);
            return rsa;
        }
        var der = Convert.FromBase64String(StripWhitespace(text));
        try
        {
            rsa.ImportPkcs8PrivateKey(der, out _);
        }
        catch (CryptographicException)
        {
            rsa.ImportRSAPrivateKey(der, out _);
        }
        return rsa;
    }

    /// <summary>支持 PEM 公钥、PEM 证书、裸 Base64（SPKI 或 PKCS#1）。</summary>
    public static RSA LoadRsaPublicKey(string text)
    {
        text = text.Trim();
        if (text.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
        {
            using var cert = X509Certificate2.CreateFromPem(text);
            return cert.GetRSAPublicKey() ?? throw new CryptographicException("证书中没有 RSA 公钥");
        }
        var rsa = RSA.Create();
        if (text.Contains("-----BEGIN", StringComparison.Ordinal))
        {
            rsa.ImportFromPem(text);
            return rsa;
        }
        var der = Convert.FromBase64String(StripWhitespace(text));
        try
        {
            rsa.ImportSubjectPublicKeyInfo(der, out _);
        }
        catch (CryptographicException)
        {
            rsa.ImportRSAPublicKey(der, out _);
        }
        return rsa;
    }

    private static string StripWhitespace(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    public static string RsaSign(string privateKey, string content, HashAlgorithmName hash)
    {
        using var rsa = LoadRsaPrivateKey(privateKey);
        return Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(content), hash, RSASignaturePadding.Pkcs1));
    }

    public static bool RsaVerify(string publicKey, string content, string signatureBase64, HashAlgorithmName hash, RSASignaturePadding? padding = null)
    {
        try
        {
            using var rsa = LoadRsaPublicKey(publicKey);
            return rsa.VerifyData(Encoding.UTF8.GetBytes(content), Convert.FromBase64String(signatureBase64), hash, padding ?? RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    public static string Md5Hex(string s) => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    public static string Base64Url(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] FromBase64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}

public static class TimeUtil
{
    public static readonly TimeSpan Beijing = TimeSpan.FromHours(8);

    public static string BeijingString(DateTime utc, string format) =>
        new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToOffset(Beijing).ToString(format);

    public static DateTime? ParseBeijing(string value, string format) =>
        DateTime.TryParseExact(value, format, null, System.Globalization.DateTimeStyles.None, out var dt)
            ? new DateTimeOffset(dt, Beijing).UtcDateTime
            : null;

    public static DateTime FromUnixMs(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    public static DateTime FromUnixSeconds(long s) => DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;
}

/// <summary>对账单 CSV 的宽松解析：按表头名取列，容忍反引号/制表符前缀与带引号的字段。</summary>
public static class BillCsv
{
    public static List<string> SplitLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(Clean(sb.ToString())); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(Clean(sb.ToString()));
        return fields;
    }

    /// <summary>去掉首尾空白以及各家用来防止 Excel 科学计数的前缀字符（` 与制表符）。</summary>
    public static string Clean(string value) => value.Trim().TrimStart('`', '\t', '\uFEFF').Trim();

    /// <summary>
    /// 找到包含全部 requiredHeaders 的表头行，之后的每一行解析为 列名→值。
    /// 明细区在以下任一情况结束：首列等于 stopFirstField（汇总区表头）、或该行列数少于表头。
    /// </summary>
    public static IEnumerable<Dictionary<string, string>> Rows(string text, string[] requiredHeaders, string? stopFirstField = null)
    {
        List<string>? headers = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var fields = SplitLine(line);
            if (headers is null)
            {
                if (requiredHeaders.All(h => fields.Contains(h))) headers = fields;
                continue;
            }
            if (stopFirstField is not null && fields[0] == stopFirstField) yield break;
            if (fields.Count < headers.Count) yield break;
            var row = new Dictionary<string, string>(headers.Count);
            for (var i = 0; i < headers.Count; i++) row[headers[i]] = fields[i];
            yield return row;
        }
    }

    /// <summary>账单文本解码：优先严格 UTF-8，失败则按 GBK（各家账单编码并不统一）。</summary>
    public static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(936).GetString(bytes);
        }
    }

    /// <summary>若为 gzip 流则解压，否则原样返回。</summary>
    public static byte[] Gunzip(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0x1f || bytes[1] != 0x8b) return bytes;
        using var input = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>元（可能不带小数或带任意位小数）→ 分。</summary>
    public static long Fen(string yuan) => Money.FromYuan(yuan.Length == 0 ? "0" : yuan) ?? 0;
}
public static class Money
{
    /// <summary>分 → "0.00" 元。</summary>
    public static string ToYuan(long fen) => (fen / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    public static long? FromYuan(string yuan) =>
        decimal.TryParse(yuan, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? (long)Math.Round(d * 100m)
            : null;
}
