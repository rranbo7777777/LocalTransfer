using System.Security.Cryptography;
using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Coordinator.Transfers;

namespace LocalTransfer.IntegrationTests;

public sealed class InboundTransferCoordinatorTests
{
    [Fact]
    public async Task SubmitOffer_BoundsPendingOffersPerDevice()
    {
        using var directory = new CoordinatorTemporaryDirectory();
        await using var coordinator = new InboundTransferCoordinator(directory.Path);
        var deviceId = Guid.NewGuid();

        for (var index = 0; index < 10; index++)
        {
            coordinator.SubmitOffer(deviceId, CreateManifest($"file-{index}.bin", length: 1));
        }

        Assert.Throws<InvalidOperationException>(
            () => coordinator.SubmitOffer(deviceId, CreateManifest("file-11.bin", length: 1)));

        // Another device is unaffected by the first device's pending offers.
        coordinator.SubmitOffer(Guid.NewGuid(), CreateManifest("other-device.bin", length: 1));
    }

    [Fact]
    public async Task UnansweredOffer_ExpiresAndReleasesItsSlot()
    {
        using var directory = new CoordinatorTemporaryDirectory();
        // A lifetime far shorter than the real one exercises exactly the same path as an
        // approval prompt nobody answered, without making the test wait eleven minutes.
        await using var coordinator = new InboundTransferCoordinator(
            directory.Path,
            TimeSpan.FromMilliseconds(120));
        var deviceId = Guid.NewGuid();

        var offerIds = new List<Guid>();
        for (var index = 0; index < 10; index++)
        {
            offerIds.Add(coordinator.SubmitOffer(deviceId, CreateManifest($"file-{index}.bin", 1)).TransferId);
        }

        // The per-device cap is what used to make ignored prompts permanent: with every slot
        // held by an unanswered prompt this submission fails and keeps failing until restart.
        Assert.Throws<InvalidOperationException>(
            () => coordinator.SubmitOffer(deviceId, CreateManifest("blocked.bin", 1)));

        await Task.Delay(220);

        // Sweeping happens on the next call, so every abandoned offer is now terminal...
        Assert.Empty(coordinator.GetPendingOffers());
        Assert.All(
            coordinator.GetAll().Where(info => offerIds.Contains(info.TransferId)),
            info => Assert.Equal(TransferState.Rejected, info.State));

        // ...and the phone can send again without restarting the desktop.
        var retry = coordinator.SubmitOffer(deviceId, CreateManifest("after-expiry.bin", 1));
        Assert.Equal(TransferState.WaitingForApproval, retry.State);
    }

    [Fact]
    public async Task ApprovalAfterExpiry_IsRefused()
    {
        using var directory = new CoordinatorTemporaryDirectory();
        await using var coordinator = new InboundTransferCoordinator(
            directory.Path,
            TimeSpan.FromMilliseconds(120));
        var deviceId = Guid.NewGuid();

        var offer = coordinator.SubmitOffer(deviceId, CreateManifest("late.bin", length: 1));
        Assert.True(offer.ExpiresAtUtc > DateTimeOffset.UtcNow);

        await Task.Delay(220);

        // Deliberately no sweeping call in between: the guard has to be local to the approval
        // so a late click cannot slip through a window where nothing else swept first.
        Assert.False(await coordinator.ApproveAsync(offer.TransferId));
        Assert.Equal(
            TransferState.Rejected,
            (await coordinator.GetAsync(offer.TransferId, deviceId))!.State);
    }

    [Fact]
    public async Task PendingOfferWithinItsLifetime_IsStillApprovable()
    {
        using var directory = new CoordinatorTemporaryDirectory();
        await using var coordinator = new InboundTransferCoordinator(
            directory.Path,
            TimeSpan.FromMilliseconds(120));
        var deviceId = Guid.NewGuid();

        var offer = coordinator.SubmitOffer(deviceId, CreateManifest("prompt.bin", length: 0));

        Assert.Single(coordinator.GetPendingOffers());
        Assert.True(await coordinator.ApproveAsync(offer.TransferId));
        Assert.Equal(
            TransferState.Queued,
            (await coordinator.GetAsync(offer.TransferId, deviceId))!.State);
    }

    private static FileManifest CreateManifest(string fileName, long length) =>
        new(
            Guid.NewGuid(),
            fileName,
            length,
            DateTimeOffset.UtcNow,
            1024,
            Convert.ToHexString(SHA256.HashData(new byte[] { 0 })));

    private sealed class CoordinatorTemporaryDirectory : IDisposable
    {
        public CoordinatorTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"LocalTransfer.InboundTests.{Guid.NewGuid():N}");
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
