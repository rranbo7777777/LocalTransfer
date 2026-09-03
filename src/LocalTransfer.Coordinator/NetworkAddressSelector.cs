using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LocalTransfer.Coordinator;

internal static class NetworkAddressSelector
{
    public static IPAddress SelectIPv4Address()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up)
            .Where(network => network.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(network => GetCandidates(network))
            .OrderByDescending(candidate => candidate.HasGateway)
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
        foreach (var address in properties.UnicastAddresses
                     .Select(unicast => unicast.Address)
                     .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                     .Where(address => !IPAddress.IsLoopback(address)))
        {
            yield return new AddressCandidate(address, hasGateway);
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private sealed record AddressCandidate(IPAddress Address, bool HasGateway);
}
