using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EmbedDesk.App;

internal sealed record NetworkListenAddressOption(string Label, string Address, bool IsCustom = false)
{
    public static IReadOnlyList<NetworkListenAddressOption> GetOptions() =>
    [
        .. NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter =>
            {
                try
                {
                    return adapter.GetIPProperties().UnicastAddresses
                        .Select(item => item.Address)
                        .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
                            && !IPAddress.IsLoopback(address)
                            && !address.Equals(IPAddress.Any)
                            && !address.Equals(IPAddress.IPv6Any))
                        .Distinct()
                        .Select(address => new NetworkListenAddressOption($"{adapter.Name} · {address}", address.ToString()));
                }
                catch (NetworkInformationException) { return []; }
            }),
        new("本机环回 · IPv4（仅本机）", IPAddress.Loopback.ToString()),
        new("本机环回 · IPv6（仅本机）", IPAddress.IPv6Loopback.ToString()),
        new("所有 IPv4 网卡", IPAddress.Any.ToString()),
        new("所有 IPv6 网卡", IPAddress.IPv6Any.ToString()),
        new("自定义地址…", string.Empty, true)
    ];
}
