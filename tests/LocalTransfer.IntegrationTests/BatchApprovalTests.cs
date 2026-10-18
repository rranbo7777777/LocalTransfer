using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Coordinator.Transfers;

namespace LocalTransfer.IntegrationTests;

/// <summary>
/// A batch is one question with one answer. These tests pin the two properties that make a
/// hundred-file send usable instead of a hundred dialogs: the request really blocks until that
/// answer arrives, and a partial answer splits accepted from rejected correctly.
/// </summary>
public sealed class BatchApprovalTests : IDisposable
{
    private readonly string _receiveDirectory;

    public BatchApprovalTests()
    {
        _receiveDirectory = Path.Combine(
            Path.GetTempPath(),
            $"LocalTransfer.BatchTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(_receiveDirectory);
    }

    [Fact]
    public async Task SubmitBatch_ReturnsOnlyAfterTheSingleDecision()
    {
        await using var coordinator = new InboundTransferCoordinator(_receiveDirectory);
        var deviceId = Guid.NewGuid();
        var manifests = new[]
        {
            CreateManifest("a.bin"),
            CreateManifest("b.bin"),
            CreateManifest("c.bin")
        };

        InboundBatchInfo? offered = null;
        coordinator.BatchOffered += (_, batch) => offered = batch;

        var submission = coordinator.SubmitBatchAsync(deviceId, Guid.NewGuid(), manifests);
        Assert.NotNull(offered);
        Assert.False(submission.IsCompleted);

        var accepted = new HashSet<Guid> { manifests[0].TransferId, manifests[2].TransferId };
        Assert.True(await coordinator.ApproveBatchAsync(offered!.BatchId, deviceId, accepted));

        var result = await submission;
        Assert.Equal(accepted, result.Accepted.ToHashSet());
        Assert.Equal(new[] { manifests[1].TransferId }, result.Rejected.ToArray());
    }

    [Fact]
    public async Task SubmitBatch_CanBeRejectedEntirely()
    {
        await using var coordinator = new InboundTransferCoordinator(_receiveDirectory);
        var deviceId = Guid.NewGuid();
        var manifests = new[] { CreateManifest("a.bin"), CreateManifest("b.bin") };

        InboundBatchInfo? offered = null;
        coordinator.BatchOffered += (_, batch) => offered = batch;

        var submission = coordinator.SubmitBatchAsync(deviceId, Guid.NewGuid(), manifests);
        Assert.True(await coordinator.RejectBatchAsync(offered!.BatchId, deviceId));

        var result = await submission;
        Assert.Empty(result.Accepted);
        Assert.Equal(2, result.Rejected.Count);
    }

    [Fact]
    public async Task ApproveBatch_RefusesADeviceThatDoesNotOwnTheBatch()
    {
        await using var coordinator = new InboundTransferCoordinator(_receiveDirectory);
        var owner = Guid.NewGuid();
        var manifests = new[] { CreateManifest("a.bin") };

        InboundBatchInfo? offered = null;
        coordinator.BatchOffered += (_, batch) => offered = batch;

        var submission = coordinator.SubmitBatchAsync(owner, Guid.NewGuid(), manifests);
        Assert.False(await coordinator.ApproveBatchAsync(
            offered!.BatchId,
            Guid.NewGuid(),
            new HashSet<Guid>()));

        // Release the pending submission so the test does not wait out the offer lifetime.
        await coordinator.RejectBatchAsync(offered.BatchId, owner);
        await submission;
    }

    [Fact]
    public async Task SubmitBatch_RejectsListsAboveTheLimit()
    {
        await using var coordinator = new InboundTransferCoordinator(_receiveDirectory);
        var manifests = Enumerable
            .Range(0, BatchLimits.MaxFiles + 1)
            .Select(index => CreateManifest($"file{index}.bin"))
            .ToArray();

        await Assert.ThrowsAsync<ArgumentException>(
            () => coordinator.SubmitBatchAsync(Guid.NewGuid(), Guid.NewGuid(), manifests));
    }

    private static FileManifest CreateManifest(string fileName) =>
        new(
            Guid.NewGuid(),
            fileName,
            1024,
            DateTimeOffset.UtcNow,
            ProtocolConstants.DefaultChunkSize,
            new string('a', 64));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_receiveDirectory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
