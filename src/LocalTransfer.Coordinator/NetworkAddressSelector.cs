using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LocalTransfer.Coordinator;

internal static class NetworkAddressSelector
{
    // Adapters that look reachable but are not on the physical LAN. Advertising one of their
    // addresses puts an unusable host in the pairing QR code, which is the most common way the
    // handshake fails on a machine that also runs a VPN. This is a ranking penalty, not a
    // filter: if a virtual adapter is the only candidate it is still used.
    private static readonly string[] VirtualAdapterMarkers =
    [
        "virtual", "vpn", "tap-", "tun", "wintun", "wireguard", "openvpn", "tailscale",
        "zerotier", "radmin", "hyper-v", "vmware", "virtualbox", "docker", "wsl", "clash",
        "loopback", "bluetooth", "npcap", "wan miniport"
    ];

    public static IPAddress SelectIPv4Address()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .Where(network => network.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(network => GetCandidates(network))
            .OrderBy(candidate => candidate.IsVirtual)
            .ThenByDescending(candidate => candidate.HasGateway)
            .ThenByDescending(candidate => IsPrivate(candidate.Address))
            .ThenBy(candidate => candidate.Address.ToString(), StringComparer.Ordinal)
            .ToArray();

        return candidates.FirstOrDefault()?.Address ?? IPAddress.Loopback;
    }

    private static IEnumerable<AddressCandidate> GetCandidates(NetworkInterface network)
    {
        IPInterfaceProperties properties;
        try
        {
            properties = network.GetIPProperties();
        }
        catch (NetworkInformationException)
        {
            yield break;
        }

        var hasGateway = properties.GatewayAddresses.Any(gateway =>
            gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
            !IPAddress.Any.Equals(gateway.Address));
        var isVirtual = HasVirtualMarker(network);
        foreach (var address in properties.UnicastAddresses
                     .Select(unicast => unicast.Address)
                     .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                     .Where(address => !IPAddress.IsLoopback(address)))
        {
            yield return new AddressCandidate(address, hasGateway, isVirtual);
        }
    }

    private static bool HasVirtualMarker(NetworkInterface network)
    {
        string description;
        try
        {
            description = $"{network.Name} {network.Description}";
        }
        catch (NetworkInformationException)
        {
            return false;
        }

        return VirtualAdapterMarkers.Any(marker =>
            description.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private sealed record AddressCandidate(IPAddress Address, bool HasGateway, bool IsVirtual);
}
