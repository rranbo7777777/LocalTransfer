using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Coordinator.History;
using LocalTransfer.Coordinator.Transfers;

namespace LocalTransfer.IntegrationTests;

/// <summary>
/// The history is the only place a finished transfer survives; these tests pin that it survives a
/// torn write, that it reads newest-first, and that a terminal state is recorded by the coordinator
/// itself rather than by whichever caller happened to notice.
/// </summary>
public sealed class TransferHistoryTests : IDisposable
{
    private readonly string _directory;

    public TransferHistoryTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            $"LocalTransfer.HistoryTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Append_ThenRead_ReturnsNewestFirst()
    {
        var store = new TransferHistoryStore(_directory);
        store.Append(CreateEntry("first.bin", TransferOutcome.Completed));
        store.Append(CreateEntry("second.bin", TransferOutcome.Failed));

        var entries = store.Read();

        Assert.Equal(2, entries.Count);
        Assert.Equal("second.bin", entries[0].FileName);
        Assert.Equal("first.bin", entries[1].FileName);
    }

    [Fact]
    public void Read_SkipsATornLine()
    {
        var store = new TransferHistoryStore(_directory);
        store.Append(CreateEntry("good.bin", TransferOutcome.Completed));

        // A crash mid-write leaves a partial line; it must cost that line, not the whole history.
        File.AppendAllText(store.FilePath, "{ this is not json" + Environment.NewLine);

        var entries = store.Read();

        Assert.Single(entries);
        Assert.Equal("good.bin", entries[0].FileName);
    }

    [Fact]
    public async Task RejectingAnOffer_RecordsItAsRejected()
    {
        var store = new TransferHistoryStore(_directory);
        await using var coordinator = new InboundTransferCoordinator(
            Path.Combine(_directory, "received"),
            history: store);

        var deviceId = Guid.NewGuid();
        var offer = coordinator.SubmitOffer(deviceId, CreateManifest("photo.jpg"));
        await coordinator.RejectAsync(offer.TransferId);

        var entry = Assert.Single(store.Read());
        Assert.Equal("photo.jpg", entry.FileName);
        Assert.Equal(deviceId, entry.DeviceId);
        Assert.Equal(TransferDirection.Inbound, entry.Direction);
        Assert.Equal(TransferOutcome.Rejected, entry.Outcome);
    }

    [Fact]
    public async Task CancellingAnOutboundTransfer_RecordsItAsCanceled()
    {
        var store = new TransferHistoryStore(_directory);
        using var coordinator = new OutboundTransferCoordinator(history: store);

        var deviceId = Guid.NewGuid();
        var source = Path.Combine(_directory, "payload.bin");
        await File.WriteAllBytesAsync(source, new byte[512]);

        var queued = await coordinator.EnqueueAsync(deviceId, source);
        Assert.True(await coordinator.CancelAsync(queued.TransferId, deviceId));

        var entry = Assert.Single(store.Read());
        Assert.Equal(TransferDirection.Outbound, entry.Direction);
        Assert.Equal(TransferOutcome.Canceled, entry.Outcome);
    }

    private static TransferHistoryEntry CreateEntry(string fileName, TransferOutcome outcome) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TransferDirection.Inbound,
            fileName,
            1024,
            DateTimeOffset.UtcNow,
            outcome,
            null);

    private static FileManifest CreateManifest(string fileName) =>
        new(
            Guid.NewGuid(),
            fileName,
            2048,
            DateTimeOffset.UtcNow,
            ProtocolConstants.DefaultChunkSize,
            new string('b', 64));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
