using System.Text;
using LocalTransfer.Coordinator.Discovery;

namespace LocalTransfer.IntegrationTests;

/// <summary>
/// The announcement is built once at startup and then only sent, so its contents are the one place
/// where a mistake would silently make every phone ignore the desktop.
/// </summary>
public sealed class DiscoveryAnnouncementTests
{
    [Fact]
    public async Task Payload_CarriesTheFingerprintInTheFormThePhoneCompares()
    {
        var fingerprint = new string('a', 64);
        await using var announcer = new MulticastAnnouncer(
            "https://192.168.1.5:53317",
            fingerprint,
            "DESKTOP-TEST",
            null);

        var payload = Encoding.UTF8.GetString(announcer.Payload.ToArray());

        // The phone compares case-insensitively, but normalising here keeps a packet capture
        // readable against the fingerprint shown in the desktop settings page.
        Assert.Contains(fingerprint.ToUpperInvariant(), payload);
        Assert.Contains("https://192.168.1.5:53317", payload);
        Assert.Contains("LocalTransfer", payload);
    }

    [Fact]
    public async Task Constructor_RejectsAnInvalidFingerprint()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await using var announcer = new MulticastAnnouncer(
                "https://192.168.1.5:53317",
                "not-a-fingerprint",
                "DESKTOP-TEST",
                null);
        });
    }
}
