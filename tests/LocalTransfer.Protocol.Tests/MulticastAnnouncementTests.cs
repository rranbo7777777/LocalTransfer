using System.Text.Json;
using LocalTransfer.Contracts.Discovery;
using LocalTransfer.Contracts.Protocol;

namespace LocalTransfer.Protocol.Tests;

/// <summary>
/// The announcement is the only thing a phone acts on without presenting a credential, so its wire
/// shape and the rules that keep it trustworthy are pinned here.
/// </summary>
public sealed class MulticastAnnouncementTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Announcement_RoundTripsThroughJson()
    {
        var original = new MulticastAnnouncement(
            MulticastAnnouncement.ServiceName,
            ProtocolConstants.CurrentVersion,
            "工作室台式机",
            new string('A', 64),
            "https://192.168.1.20:53317");

        var restored = JsonSerializer.Deserialize<MulticastAnnouncement>(
            JsonSerializer.Serialize(original, Options),
            Options);

        Assert.Equal(original, restored);
    }

    [Fact]
    public void Announcement_StaysInsideOneDatagram()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new MulticastAnnouncement(
                MulticastAnnouncement.ServiceName,
                ProtocolConstants.CurrentVersion,
                new string('名', 64),
                new string('A', 64),
                "https://255.255.255.255:65535"),
            Options);

        Assert.True(
            payload.Length <= DiscoveryConstants.MaxDatagramBytes,
            $"The announcement is {payload.Length} bytes; the receiver drops anything above " +
            $"{DiscoveryConstants.MaxDatagramBytes}.");
    }

    [Fact]
    public void DiscoveryPort_MustNotCollideWithLocalSendsMulticastPort()
    {
        // LocalSend multicasts on 53317. Two Android apps cannot bind one UDP port, so sharing it
        // would break discovery whenever both are installed on the same phone.
        Assert.NotEqual(53317, DiscoveryConstants.Port);
    }

    [Fact]
    public void DiscoveryGroup_IsInsideTheLinkLocalMulticastRange()
    {
        // Several Android builds only deliver multicast addressed inside 224.0.0.0/24.
        Assert.Equal(
            new byte[] { 224, 0, 0, 167 },
            DiscoveryConstants.Group.GetAddressBytes());
    }
}
