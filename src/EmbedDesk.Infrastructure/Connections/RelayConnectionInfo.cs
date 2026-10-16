// SPDX-License-Identifier: GPL-3.0-only
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbedDesk.Infrastructure.Connections;

/// <summary>Versioned relay import code. Contains an admission secret; Base64 is not encryption.</summary>
public sealed class RelayConnectionInfo
{
    public const string Prefix = "EMBEDDESK-RELAY:";
    public const int MaximumTokenLength = 2048;
    private sealed record Payload(int Version, string Name, string Url, string AdmissionToken);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { MaxDepth = 4, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    private RelayConnectionInfo(string name, string url, string admissionToken)
    { Name = name; Url = url; AdmissionToken = admissionToken; }
    public string Name { get; }
    public string Url { get; }
    public string AdmissionToken { get; }

    public static string Encode(string name, string url, string admissionToken)
    {
        var info = Validate(name, url, admissionToken);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Payload(1, info.Name, info.Url, info.AdmissionToken), JsonOptions);
        if (bytes.Length > 8 * 1024) throw new FormatException("中继连接代码过长，请缩短名称、地址或 Token。");
        return Prefix + Convert.ToBase64String(bytes);
    }

    public static RelayConnectionInfo Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("没有中继连接代码，请先复制完整代码。");
        if (text.Length > 16 * 1024) throw new FormatException("中继连接代码过长。");
        text = text.Trim();
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) throw new FormatException("这不是 EmbedDesk 中继连接代码。");
        try
        {
            var bytes = Convert.FromBase64String(text[Prefix.Length..]);
            if (bytes.Length > 8 * 1024) throw new FormatException("中继连接代码过长。");
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("中继连接代码格式无效。");
            var fields = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            if (fields.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length) throw new FormatException("中继连接代码包含重复字段。");
            var payload = JsonSerializer.Deserialize<Payload>(bytes, JsonOptions)
                ?? throw new FormatException("中继连接代码为空。");
            if (payload.Version != 1) throw new FormatException("不支持此中继连接代码版本。");
            return Validate(payload.Name, payload.Url, payload.AdmissionToken);
        }
        catch (JsonException) { throw new FormatException("中继连接代码格式无效，请重新复制完整内容。"); }
    }

    public static bool TryNormalizeUrl(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is not { Length: > 0 and <= 2048 } || value.Any(char.IsControl)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0
            || uri.Port < 1 || uri.Scheme is not ("http" or "https")) return false;
        normalized = uri.AbsoluteUri.TrimEnd('/');
        return normalized.Length <= 2048;
    }

    public static bool IsValidToken(string? token) => token is not null && token.Length <= MaximumTokenLength
        && token.All(c => c is >= '!' and <= '~');

    private static RelayConnectionInfo Validate(string? name, string? url, string? token)
    {
        if (name is null || name.Trim().Length is < 1 or > 64 || name.Any(char.IsControl))
            throw new FormatException("中继名称长度应为 1–64 个字符，不能包含控制字符。");
        if (!TryNormalizeUrl(url, out var normalized))
            throw new FormatException("中继地址须为 HTTP 或 HTTPS URL，不能包含账号、查询参数或片段。");
        if (!IsValidToken(token)) throw new FormatException("准入 Token 最多 2048 个 ASCII 字符，不能包含空格或控制字符。");
        return new(name.Trim(), normalized, token!);
    }
}
