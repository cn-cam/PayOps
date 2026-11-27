using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using PayOps.Core.Providers;

namespace PayOps.Providers.Common;

/// <summary>
/// 微信支付 APIv2（XML + MD5/HMAC-SHA256）的最小实现：签约代扣（免密支付/委托代扣）只存在于 v2。
/// 签名串：参数名 ASCII 升序拼接 k=v&amp;…，末尾追加 &amp;key=APIv2密钥，取 MD5 或 HMAC-SHA256 后转大写。
/// </summary>
public static class WechatV2
{
    public const string Host = "https://api.mch.weixin.qq.com";

    public static string Sign(IReadOnlyDictionary<string, string> parameters, string apiKey, string signType = "MD5")
    {
        var content = string.Join('&', parameters
            .Where(kv => kv.Key != "sign" && !string.IsNullOrEmpty(kv.Value))
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key}={kv.Value}"));
        var text = content + "&key=" + apiKey;
        return signType.Equals("HMAC-SHA256", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(apiKey), Encoding.UTF8.GetBytes(text)))
            : Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static bool Verify(IReadOnlyDictionary<string, string> parameters, string apiKey)
    {
        var sign = parameters.TryGetValue("sign", out var s) ? s : "";
        var signType = parameters.TryGetValue("sign_type", out var t) && t.Length > 0 ? t : "MD5";
        return sign.Length > 0 && string.Equals(Sign(parameters, apiKey, signType), sign, StringComparison.OrdinalIgnoreCase);
    }

    public static string ToXml(IEnumerable<KeyValuePair<string, string>> parameters) =>
        "<xml>" + string.Concat(parameters.Select(kv => $"<{kv.Key}><![CDATA[{kv.Value}]]></{kv.Key}>")) + "</xml>";

    public static string ToXml(IReadOnlyDictionary<string, string> parameters) => ToXml(parameters.AsEnumerable());

    public static Dictionary<string, string> ParseXml(string xml)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var root = XDocument.Parse(xml).Root ?? throw new FormatException("缺少根节点");
            foreach (var element in root.Elements()) result[element.Name.LocalName] = element.Value;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException)
        {
            throw new ProviderException("UPSTREAM_BAD_RESPONSE", "微信 v2 报文无法解析：" + ex.Message);
        }
        return result;
    }

    /// <summary>v2 统一应答（签约/解约/扣款通知都要求返回 SUCCESS）。</summary>
    public static string SuccessReply() =>
        "<xml><return_code><![CDATA[SUCCESS]]></return_code><return_msg><![CDATA[OK]]></return_msg></xml>";

    public static string FailReply(string message) =>
        $"<xml><return_code><![CDATA[FAIL]]></return_code><return_msg><![CDATA[{message.Replace("]]>", "")}]]></return_msg></xml>";
}

/// <summary>支付宝周期扣款（商家扣款）用到的少量常量与序列化辅助。</summary>
public static class AlipayAgreement
{
    public const string PersonalProductCode = "GENERAL_WITHHOLDING_P";
    public const string ProductCode = "GENERAL_WITHHOLDING";
}
