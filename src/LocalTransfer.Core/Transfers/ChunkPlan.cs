using LocalTransfer.Contracts.Transfers;

namespace LocalTransfer.Core.Transfers;

public readonly record struct ChunkDescriptor(int Index, long Offset, int Length);

public sealed class ChunkPlan
{
    private readonly FileManifest _manifest;

    public ChunkPlan(FileManifest manifest)
    {
        manifest.Validate();
        _manifest = manifest;
    }

    public int Count => _manifest.ChunkCount;

    public ChunkDescriptor GetChunk(int index)
    {
        if (index < 0 || index >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var offset = checked((long)index * _manifest.ChunkSize);
        var remaining = _manifest.Length - offset;
        var length = checked((int)Math.Min(_manifest.ChunkSize, remaining));
        return new ChunkDescriptor(index, offset, length);
    }
}
