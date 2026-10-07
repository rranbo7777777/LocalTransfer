using LocalTransfer.Contracts.Devices;
using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Contracts.Pairing;
using LocalTransfer.Coordinator.Devices;
using LocalTransfer.Coordinator.Pairing;

namespace LocalTransfer.IntegrationTests;

public sealed class PairingCoordinatorTests
{
    [Fact]
    public async Task ApprovedDevice_ReceivesCredentialOnceAndCanAuthenticate()
    {
        using var directory = new PairingTemporaryDirectory();
        var trustedDevices = new TrustedDeviceStore(directory.Path);
        var coordinator = new PairingCoordinator(trustedDevices);
        var ticket = coordinator.CreateTicket(TimeSpan.FromMinutes(2));
        var descriptor = CreateDevice();

        var submission = coordinator.Submit(new PairingSubmission(ticket.Secret, descriptor));

        Assert.Equal(PairingRequestStatus.Pending, submission.Status);
        Assert.False(coordinator.SubmitWithSameTicketSucceeds(ticket.Secret, descriptor));
        Assert.True(await coordinator.ApproveAsync(submission.RequestId));

        var firstPoll = coordinator.Poll(submission.RequestId);
        Assert.NotNull(firstPoll?.Credential);
        Assert.Equal(PairingRequestStatus.Approved, firstPoll.Status);
        Assert.True(trustedDevices.ValidateCredential(descriptor.DeviceId, firstPoll.Credential!));

        var secondPoll = coordinator.Poll(submission.RequestId);
        Assert.Equal(PairingRequestStatus.CredentialDelivered, secondPoll?.Status);
        Assert.Null(secondPoll?.Credential);

        var persistedJson = await File.ReadAllTextAsync(
            System.IO.Path.Combine(directory.Path, "trusted-devices.json"));
        Assert.DoesNotContain(firstPoll.Credential!, persistedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Submit_RejectsOversizedOrControlBearingDisplayName()
    {
        using var directory = new PairingTemporaryDirectory();
        var coordinator = new PairingCoordinator(new TrustedDeviceStore(directory.Path));
        var ticket = coordinator.CreateTicket(TimeSpan.FromMinutes(2));

        var oversized = CreateSubmission(coordinator, CreateDevice(displayName: new string('x', 65)));
        var controlBearing = CreateSubmission(coordinator, CreateDevice(displayName: "device\nname"));

        Assert.Throws<ArgumentException>(() => coordinator.Submit(oversized));
        Assert.Throws<ArgumentException>(() => coordinator.Submit(controlBearing));
    }

    [Fact]
    public void Submit_RejectsOversizedPlatform()
    {
        using var directory = new PairingTemporaryDirectory();
        var coordinator = new PairingCoordinator(new TrustedDeviceStore(directory.Path));

        var submission = CreateSubmission(
            coordinator, CreateDevice(platform: new string('x', 33)));

        Assert.Throws<ArgumentException>(() => coordinator.Submit(submission));
    }

    private static PairingSubmission CreateSubmission(
        PairingCoordinator coordinator,
        DeviceDescriptor device)
    {
        var ticket = coordinator.CreateTicket(TimeSpan.FromMinutes(2));
        return new PairingSubmission(ticket.Secret, device);
    }

    private static DeviceDescriptor CreateDevice(
        string displayName = "测试手机",
        string platform = "Android") =>
        new(
            Guid.NewGuid(),
            displayName,
            platform,
            ProtocolConstants.CurrentVersion,
            DeviceCapabilities.Upload | DeviceCapabilities.Download | DeviceCapabilities.Resume);

    private sealed class PairingTemporaryDirectory : IDisposable
    {
        public PairingTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"LocalTransfer.PairingTests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}

internal static class PairingCoordinatorTestExtensions
{
    public static bool SubmitWithSameTicketSucceeds(
        this PairingCoordinator coordinator,
        string secret,
        DeviceDescriptor descriptor)
    {
        try
        {
            coordinator.Submit(new PairingSubmission(secret, descriptor));
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
