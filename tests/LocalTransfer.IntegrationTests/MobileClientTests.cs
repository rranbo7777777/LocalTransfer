using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LocalTransfer.Client;
using LocalTransfer.Contracts.Devices;
using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Coordinator;

namespace LocalTransfer.IntegrationTests;

public sealed class MobileClientTests
{
    [Fact]
    public async Task MobileClient_PairsUploadsAndDownloadsOverPinnedHttps()
    {
        using var directory = new ClientTemporaryDirectory();
        var certificate = CreateCertificate();
        var options = new CoordinatorOptions
        {
            ListenAddress = IPAddress.Loopback,
            Port = GetAvailablePort(),
            DataDirectory = Path.Combine(directory.Path, "coordinator-data"),
            ReceiveDirectory = Path.Combine(directory.Path, "computer-received")
        };
        await using var host = new CoordinatorHost(options, () => certificate);
        await host.StartAsync();

        var device = new DeviceDescriptor(
            Guid.NewGuid(),
            "共享客户端测试手机",
            "Android",
            ProtocolConstants.CurrentVersion,
            DeviceCapabilities.Upload | DeviceCapabilities.Download | DeviceCapabilities.Resume);
        var pairingRequested = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Pairing.PairingRequested += (_, request) => pairingRequested.TrySetResult(request.RequestId);
        var pairingTask = PairingClient.PairAsync(host.CreatePairingBootstrap(), device);
        var pairingRequestId = await pairingRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await host.Pairing.ApproveAsync(pairingRequestId));
        var connection = await pairingTask;
        using var client = new LocalTransferClient(connection);

        var phoneContent = "来自手机文件选择器的内容"u8.ToArray();
        var transferOffered = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.InboundTransfers.TransferOffered += (_, transfer) =>
            transferOffered.TrySetResult(transfer.TransferId);
        var uploadTask = client.UploadAsync(
            "手机文件.txt",
            _ => Task.FromResult<Stream>(new MemoryStream(phoneContent, writable: false)));
        var offeredTransferId = await transferOffered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(await host.InboundTransfers.ApproveAsync(offeredTransferId));
        Assert.Equal(offeredTransferId, await uploadTask);
        Assert.Equal(
            phoneContent,
            await File.ReadAllBytesAsync(Path.Combine(options.ReceiveDirectory, "手机文件.txt")));

        var computerContent = RandomNumberGenerator.GetBytes(ProtocolConstants.DefaultChunkSize + 29);
        var computerSource = Path.Combine(directory.Path, "电脑文件.bin");
        await File.WriteAllBytesAsync(computerSource, computerContent);
        var queued = await host.OutboundTransfers.EnqueueAsync(device.DeviceId, computerSource);
        var available = await client.GetAvailableDownloadsAsync();
        var remote = Assert.Single(available);
        Assert.Equal(queued.TransferId, remote.TransferId);

        var mobileDownloads = Path.Combine(directory.Path, "mobile-downloads");
        var finalPath = await client.DownloadAsync(remote, mobileDownloads);
        Assert.Equal(computerContent, await File.ReadAllBytesAsync(finalPath));
        Assert.Empty(await client.GetAvailableDownloadsAsync());
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=LocalTransfer Client Tests", key, HashAlgorithmName.SHA256);
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(10));
        return new X509Certificate2(generated.Export(X509ContentType.Pfx));
    }

    private sealed class ClientTemporaryDirectory : IDisposable
    {
        public ClientTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"LocalTransfer.ClientTests.{Guid.NewGuid():N}");
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
