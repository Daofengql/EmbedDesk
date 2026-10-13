using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace EmbedDesk.Core.Models;

public sealed record HostsMapping
{
    public string Domain { get; init; } = "";
    public string Address { get; init; } = "";
    public string Comment { get; init; } = "";
    public bool Enabled { get; init; } = true;

    public HostsMapping Normalize()
    {
        var domain = NormalizeDomain(Domain);
        if (!IPAddress.TryParse(Address?.Trim(), out var ip) || ip.IsIPv4MappedToIPv6
            || (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0))
            throw new FormatException("请填写有效的 IPv4 / IPv6 地址（不支持作用域编号）。");
        var comment = Comment?.Trim() ?? "";
        if (comment.Length > 200 || comment.Any(char.IsControl)) throw new FormatException("备注最多 200 字，不能包含换行或控制字符。");
        return this with { Domain = domain, Address = ip.ToString(), Comment = comment };
    }

    public static string NormalizeDomain(string? value)
    {
        var input = value?.Trim().TrimEnd('.') ?? "";
        if (input.Length == 0 || input.Length > 253 || IPAddress.TryParse(input, out _)) throw new FormatException("请输入完整域名，不要包含 URL、端口、IP 或通配符；每个子域名需单独添加规则。");
        string domain;
        try { domain = new IdnMapping().GetAscii(input).ToLowerInvariant(); }
        catch (ArgumentException ex) { throw new FormatException("域名格式无效。", ex); }
        if (domain.Length > 253 || domain.Split('.').Any(label => label.Length is < 1 or > 63
            || label[0] == '-' || label[^1] == '-' || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')))
            throw new FormatException("域名标签格式无效。请使用字母、数字、连字符或下划线。");
        return domain;
    }

    public static HostsMapping[] Validate(IEnumerable<HostsMapping> mappings)
    {
        var result = mappings.Take(1001).Select(mapping => mapping.Normalize()).ToArray();
        if (result.Length > 1000) throw new FormatException("每个方案最多 1000 条规则。");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in result.Where(item => item.Enabled))
        {
            var family = IPAddress.Parse(mapping.Address).AddressFamily;
            if (!keys.Add($"{mapping.Domain}/{family}"))
                throw new FormatException($"域名 {mapping.Domain} 存在重复启用的同类型规则。");
        }
        return result;
    }
}
