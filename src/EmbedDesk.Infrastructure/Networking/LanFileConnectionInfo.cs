using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace EmbedDesk.Infrastructure.Networking;

/// <summary>Versioned clipboard information. Base64 encodes but does not encrypt the access code.</summary>
public sealed class LanFileConnectionInfo
{
    public const string Prefix = "EMBEDDESK-FILES:";
    private const int MaximumAddresses = 32;
    private sealed record Payload(int Version, string[] Addresses, string AccessCode);
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 4 };
    private LanFileConnectionInfo(string[] addresses, string accessCode)
    { Addresses = Array.AsReadOnly(addresses); AccessCode = accessCode; }
    public IReadOnlyList<string> Addresses { get; }
    public string AccessCode { get; }
    public static string Encode(IEnumerable<string> addresses, string accessCode)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var info = Validate(addresses.Take(MaximumAddresses + 1).ToArray(), accessCode);
        return Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Payload(1, info.Addresses.ToArray(), info.AccessCode)));
    }
    public static LanFileConnectionInfo Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("剪贴板中没有连接信息，请先在服务端点击“复制连接信息”。");
        if (text.Length > 16 * 1024) throw new FormatException("连接信息过长，无法导入。");
        text = text.Trim();
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) throw new FormatException("这不是 EmbedDesk 文件连接信息，请复制服务端生成的完整内容。");
        try
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(text[Prefix.Length..]); }
            catch (FormatException) { throw new FormatException("连接信息编码无效，请重新复制完整内容。"); }
            if (bytes.Length > 8 * 1024) throw new FormatException("连接信息过长，无法导入。");
            var payload = JsonSerializer.Deserialize<Payload>(bytes, JsonOptions) ?? throw new FormatException("连接信息为空。");
            if (payload.Version != 1) throw new FormatException("不支持此连接信息版本，请更新软件或重新复制。");
            return Validate(payload.Addresses, payload.AccessCode);
        }
        catch (JsonException) { throw new FormatException("连接信息格式无效，请重新复制完整内容。"); }
    }
    private static LanFileConnectionInfo Validate(string[]? addresses, string? accessCode)
    {
        if (addresses is not { Length: > 0 and <= MaximumAddresses }) throw new FormatException("连接信息中的服务地址数量无效。");
        if (accessCode is not { Length: 12 } || !accessCode.All(char.IsAsciiHexDigit)) throw new FormatException("连接信息中的访问码无效。");
        var normalized = new List<string>();
        foreach (var address in addresses)
        {
            if (address is not { Length: > 0 and <= 128 } || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http"
                || uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.Port < 1
                || !IPAddress.TryParse(uri.Host, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || !LanFileService.IsLan(ip))
                throw new FormatException("连接信息只支持局域网 HTTP IPv4 服务地址。");
            normalized.Add($"http://{ip}:{uri.Port}");
        }
        return new(normalized.Distinct(StringComparer.Ordinal).ToArray(), accessCode.ToUpperInvariant());
    }
}
