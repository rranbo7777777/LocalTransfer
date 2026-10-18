using System.Net;

namespace LocalTransfer.Contracts.Discovery;

/// <summary>
/// Wire format of the desktop's discovery broadcast.
/// <para>
/// It carries exactly the two facts a phone needs to act on: which computer this is (the
/// certificate fingerprint the phone already pinned when pairing) and where to reach it. Nothing
/// secret travels here - a fingerprint is not a credential - so any device on the LAN may read the
/// datagram without weakening the pairing model.
/// </para>
/// </summary>
public sealed record MulticastAnnouncement(
    string Service,
    int ProtocolVersion,
    string Alias,
    string Fingerprint,
    string Endpoint)
{
    /// <summary>Marks the payload as ours, so other tools sharing the group are ignored.</summary>
    public const string ServiceName = "LocalTransfer";
}

public static class DiscoveryConstants
{
    /// <summary>
    /// Inside <c>224.0.0.0/24</c> deliberately: several Android builds only deliver multicast that
    /// was addressed to that range, and LocalSend uses the same group, so both can share one LAN.
    /// </summary>
    public static readonly IPAddress Group = IPAddress.Parse("224.0.0.167");

    /// <summary>
    /// Deliberately not 53317. That is LocalSend's multicast port, and an Android app cannot bind a
    /// port another app already holds. The HTTP service keeps listening on 53317 either way.
    /// </summary>
    public const int Port = 53318;

    /// <summary>How often the announcement repeats, so a phone joining later still hears it.</summary>
    public static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Delays between the messages of the initial burst. One datagram is easily lost, and a phone
    /// that just joined the network may not be listening yet.
    /// </summary>
    public static readonly TimeSpan[] AnnounceBurstDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(2)
    ];

    /// <summary>Announcements are tiny; anything larger is not ours.</summary>
    public const int MaxDatagramBytes = 4096;
}
