using System.Net;

namespace EmbeddedDebugger.App;

public sealed record CidrCalculation(
    string Address,
    int Prefix,
    string Network,
    string Broadcast,
    string Mask,
    string FirstHost,
    string LastHost,
    string HostCount,
    string Family);

public static class NetworkCidrCalculator
{
    public static CidrCalculation Calculate(string input)
    {
        var parts = input.Trim().Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) || !int.TryParse(parts[1], out var prefix))
            throw new FormatException("请输入 CIDR，例如 192.168.1.0/24。");
        var bytes = address.GetAddressBytes();
        var bits = bytes.Length * 8;
        if (prefix is < 0 || prefix > bits) throw new FormatException($"前缀长度必须在 0–{bits} 之间。");
        var mask = BuildMask(bytes.Length, prefix);
        var network = ApplyMask(bytes, mask);
        var broadcast = network.Zip(mask, (value, bit) => (byte)(value | (byte)~bit)).ToArray();
        var isV4 = bytes.Length == 4;
        var (first, last, count) = isV4 ? IPv4Hosts(network, broadcast, prefix) : IPv6Hosts(network, broadcast, bits - prefix);
        return new(
            address.ToString(), prefix,
            new IPAddress(network).ToString(),
            new IPAddress(broadcast).ToString(),
            isV4 ? new IPAddress(mask).ToString() : $"/{prefix}",
            new IPAddress(first).ToString(),
            new IPAddress(last).ToString(),
            count,
            isV4 ? "IPv4" : "IPv6");
    }

    private static byte[] BuildMask(int length, int prefix)
    {
        var mask = new byte[length];
        for (var index = 0; index < length; index++)
        {
            var remaining = prefix - index * 8;
            mask[index] = remaining >= 8 ? (byte)255 : remaining <= 0 ? (byte)0 : (byte)(256 - (1 << (8 - remaining)));
        }
        return mask;
    }

    private static byte[] ApplyMask(byte[] address, byte[] mask) => address.Zip(mask, (value, bit) => (byte)(value & bit)).ToArray();

    private static (byte[] First, byte[] Last, string Count) IPv4Hosts(byte[] network, byte[] broadcast, int prefix)
    {
        var first = (byte[])network.Clone();
        var last = (byte[])broadcast.Clone();
        if (prefix <= 30) { Add(first, 1); Add(last, -1); }
        var hostCount = prefix switch
        {
            32 => "1",
            31 => "2",
            _ => ((1UL << (32 - prefix)) - 2).ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        return (first, last, hostCount);
    }

    private static (byte[] First, byte[] Last, string Count) IPv6Hosts(byte[] network, byte[] broadcast, int hostBits)
        => (network, broadcast, hostBits <= 63 ? $"2^{hostBits}" : $"2^{hostBits}（地址空间）");

    private static void Add(byte[] value, int delta)
    {
        var carry = delta;
        for (var index = value.Length - 1; index >= 0 && carry != 0; index--)
        {
            var next = value[index] + carry;
            value[index] = (byte)next;
            carry = next < 0 ? -1 : next > 255 ? 1 : 0;
        }
    }
}
