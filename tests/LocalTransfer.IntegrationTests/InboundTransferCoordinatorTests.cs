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
