using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Coordinator.Transfers;
using LocalTransfer.Core.Transfers;

namespace LocalTransfer.IntegrationTests;

// The outbound queue used to live only in memory: a desktop restart silently dropped every
// queued file. These tests pin the on-disk queue behaviour — round-trip restore, purge on
// terminal state, and the freshness gate that stops stale source files from re-queuing.
public sealed class OutboundQueuePersistenceTests : IDisposable
{
    private readonly string _dataDirectory;
    private readonly string _sourcesDirectory;

    public OutboundQueuePersistenceTests()
    {
        _dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"LocalTransfer.QueueTests.data.{Guid.NewGuid():N}");
        _sourcesDirectory = Path.Combine(
            Path.GetTempPath(),
            $"LocalTransfer.QueueTests.src.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_sourcesDirectory);
    }

    public void Dispose()
    {
        Directory.Delete(_dataDirectory, recursive: true);
        Directory.Delete(_sourcesDirectory, recursive: true);
    }

    private string CreateSourceFile(string name, int length)
    {
        var path = Path.Combine(_sourcesDirectory, name);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    [Fact]
    public async Task Enqueue_WritesQueueFile_AndNewInstanceRestoresTransfer()
    {
        var sourcePath = CreateSourceFile("roundtrip.bin", 4096);
        Guid transferId;
        Guid deviceId;
        using (var coordinator = new OutboundTransferCoordinator(_dataDirectory))
        {
            var queued = await coordinator.EnqueueAsync(Guid.NewGuid(), sourcePath);
            transferId = queued.TransferId;
            deviceId = queued.DeviceId;
        }

        Assert.True(File.Exists(Path.Combine(_dataDirectory, "outbound-queue.json")));

        using var restored = new OutboundTransferCoordinator(_dataDirectory);
        var all = restored.GetAll();
        var entry = Assert.Single(all);
        Assert.Equal(transferId, entry.TransferId);
        Assert.Equal(deviceId, entry.DeviceId);
        Assert.Equal(TransferState.Queued, entry.State);
        Assert.Equal(Path.GetFileName(sourcePath), entry.Manifest.FileName);
    }

    [Fact]
    public async Task Cancel_RemovesEntryFromQueueFile()
    {
        var sourcePath = CreateSourceFile("cancel.bin", 2048);
        Guid deviceId;
        Guid transferId;
        using (var coordinator = new OutboundTransferCoordinator(_dataDirectory))
        {
            var queued = await coordinator.EnqueueAsync(Guid.NewGuid(), sourcePath);
            deviceId = queued.DeviceId;
            transferId = queued.TransferId;
            Assert.True(await coordinator.CancelAsync(transferId, deviceId));
        }

        using var restored = new OutboundTransferCoordinator(_dataDirectory);
        Assert.Empty(restored.GetAll());
    }

    [Fact]
    public async Task MissingSourceFile_IsDroppedOnRestore()
    {
        var sourcePath = CreateSourceFile("gone.bin", 1024);
        using (var coordinator = new OutboundTransferCoordinator(_dataDirectory))
        {
            await coordinator.EnqueueAsync(Guid.NewGuid(), sourcePath);
        }

        File.Delete(sourcePath);

        using var restored = new OutboundTransferCoordinator(_dataDirectory);
        Assert.Empty(restored.GetAll());
        // The queue file itself is purged so the entry does not linger for the next restart.
        Assert.Equal("[]", File.ReadAllText(Path.Combine(_dataDirectory, "outbound-queue.json")));
    }

    [Fact]
    public async Task ModifiedSourceFile_IsDroppedOnRestore()
    {
        var sourcePath = CreateSourceFile("changed.bin", 1024);
        using (var coordinator = new OutboundTransferCoordinator(_dataDirectory))
        {
            await coordinator.EnqueueAsync(Guid.NewGuid(), sourcePath);
        }

        // Same name, different size — the freshness rule in ReadChunkAsync must reject it.
        File.WriteAllBytes(sourcePath, new byte[4096]);

        using var restored = new OutboundTransferCoordinator(_dataDirectory);
        Assert.Empty(restored.GetAll());
    }

    [Fact]
    public async Task CorruptQueueFile_DoesNotPreventStartup()
    {
        var sourcePath = CreateSourceFile("intact.bin", 1024);
        using (var coordinator = new OutboundTransferCoordinator(_dataDirectory))
        {
            await coordinator.EnqueueAsync(Guid.NewGuid(), sourcePath);
        }

        File.WriteAllText(Path.Combine(_dataDirectory, "outbound-queue.json"), "{ not json");

        using var restored = new OutboundTransferCoordinator(_dataDirectory);
        Assert.Empty(restored.GetAll());
    }

    [Fact]
    public async Task ParameterlessConstructor_KeepsMemoryOnlyBehaviour()
    {
        var sourcePath = CreateSourceFile("memory.bin", 1024);
        using var coordinator = new OutboundTransferCoordinator();
        var queued = await coordinator.EnqueueAsync(Guid.NewGuid(), sourcePath);
        Assert.Equal(TransferState.Queued, queued.State);
    }

    // ---------------------------------------------------------------- 调参不变量

    // These relationships are load-bearing (see TransferTuning's remarks); breaking one of them
    // in the past produced "the phone saw its offer vanish" and "pairing trips HTTP 429" bugs.
    [Fact]
    public void OfferLifetime_MustOutliveClientApprovalWait()
    {
        Assert.True(
            TransferTuning.OfferLifetime > TransferTuning.ClientApprovalWait,
            $"OfferLifetime ({TransferTuning.OfferLifetime}) must exceed the client's approval " +
            $"wait ({TransferTuning.ClientApprovalWait}) or a phone still polling sees its " +
            "offer disappear underneath it.");
    }

    [Fact]
    public void PairingPollLimiter_MustTolerateClientPollRate()
    {
        var pollsPerMinute = (int)Math.Ceiling(
            TimeSpan.FromMinutes(1) / TransferTuning.PairingPollInterval);
        Assert.True(
            TransferTuning.PairingPollPermitLimit > pollsPerMinute * 2,
            $"PairingPollPermitLimit ({TransferTuning.PairingPollPermitLimit}) must stay well " +
            $"above the client's own poll rate ({pollsPerMinute}/min) or a slow human answering " +
            "the desktop dialog trips HTTP 429 mid-pairing.");
    }

    [Fact]
    public void PairingRequestRetention_MustOutliveTicketLifetime()
    {
        Assert.True(
            TransferTuning.PairingRequestRetention > TransferTuning.PairingTicketLifetime,
            $"PairingRequestRetention ({TransferTuning.PairingRequestRetention}) must outlive " +
            $"the ticket lifetime ({TransferTuning.PairingTicketLifetime}).");
    }

    [Fact]
    public void TransferRetry_MustStayWithinApprovalWait()
    {
        // The backoff is exponential, so the bound has to come from the tuning helper rather than
        // assuming a linear delay per attempt.
        var worstCase = TransferTuning.TransferPollInterval
            + TransferTuning.TotalTransferRetryDelay;
        Assert.True(
            worstCase < TransferTuning.ClientApprovalWait,
            $"One failed request cycle ({worstCase}) must fit comfortably inside the approval " +
            $"wait ({TransferTuning.ClientApprovalWait}).");
    }
}
