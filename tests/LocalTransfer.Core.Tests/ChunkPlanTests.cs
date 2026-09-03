using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Core.Transfers;

namespace LocalTransfer.Core.Tests;

public sealed class ChunkPlanTests
{
    [Fact]
    public void LastChunk_UsesRemainingLength()
    {
        var manifest = CreateManifest(length: 10, chunkSize: 4);
        var plan = new ChunkPlan(manifest);

        Assert.Equal(3, plan.Count);
        Assert.Equal(new ChunkDescriptor(0, 0, 4), plan.GetChunk(0));
        Assert.Equal(new ChunkDescriptor(1, 4, 4), plan.GetChunk(1));
        Assert.Equal(new ChunkDescriptor(2, 8, 2), plan.GetChunk(2));
    }

    [Fact]
    public void EmptyFile_HasNoChunks()
    {
        var plan = new ChunkPlan(CreateManifest(length: 0, chunkSize: 4));

        Assert.Equal(0, plan.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => plan.GetChunk(0));
    }

    private static FileManifest CreateManifest(long length, int chunkSize) =>
        new(
            Guid.NewGuid(),
            "sample.bin",
            length,
            DateTimeOffset.UtcNow,
            chunkSize,
            new string('0', 64));
}
