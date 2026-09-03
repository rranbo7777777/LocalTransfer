using System.Security.Cryptography;
using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Core.Transfers;
using LocalTransfer.Infrastructure.Transfers;

namespace LocalTransfer.IntegrationTests;

public sealed class ResumableFileReceiverTests
{
    [Fact]
    public async Task Transfer_ResumesAfterSessionIsReopened()
    {
        using var directory = new TemporaryDirectory();
        var data = RandomNumberGenerator.GetBytes(10_000);
        var manifest = CreateManifest("payload.bin", data, chunkSize: 4_096);
        var receiver = new ResumableFileReceiver();

        await using (var firstSession = await receiver.OpenAsync(manifest, directory.Path))
        {
            await WriteChunkAsync(receiver, firstSession, data, chunkIndex: 1);
        }

        await using var resumedSession = await receiver.OpenAsync(manifest, directory.Path);
        Assert.Equal([0, 2], await receiver.GetMissingChunksAsync(resumedSession));

        await WriteChunkAsync(receiver, resumedSession, data, chunkIndex: 0);
        await WriteChunkAsync(receiver, resumedSession, data, chunkIndex: 2);
        var finalPath = await receiver.CompleteAsync(resumedSession);

        Assert.Equal(data, await File.ReadAllBytesAsync(finalPath));
        Assert.False(File.Exists(resumedSession.TemporaryPath));
    }

    [Fact]
    public async Task InvalidChunkHash_DoesNotAdvanceCheckpoint()
    {
        using var directory = new TemporaryDirectory();
        var data = RandomNumberGenerator.GetBytes(128);
        var manifest = CreateManifest("payload.bin", data, chunkSize: 128);
        var receiver = new ResumableFileReceiver();
        await using var session = await receiver.OpenAsync(manifest, directory.Path);
        await using var content = new MemoryStream(data, writable: false);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => receiver.WriteChunkAsync(session, 0, content, new string('0', 64)));

        Assert.Equal([0], await receiver.GetMissingChunksAsync(session));
        await receiver.CancelAsync(session);
        Assert.False(File.Exists(session.TemporaryPath));
    }

    [Fact]
    public async Task ExistingFile_UsesNonDestructiveName()
    {
        using var directory = new TemporaryDirectory();
        var existingPath = System.IO.Path.Combine(directory.Path, "payload.bin");
        await File.WriteAllTextAsync(existingPath, "existing");
        var data = RandomNumberGenerator.GetBytes(256);
        var manifest = CreateManifest("payload.bin", data, chunkSize: 128);
        var receiver = new ResumableFileReceiver();
        await using var session = await receiver.OpenAsync(manifest, directory.Path);

        await WriteChunkAsync(receiver, session, data, 0);
        await WriteChunkAsync(receiver, session, data, 1);
        var finalPath = await receiver.CompleteAsync(session);

        Assert.Equal("payload (1).bin", System.IO.Path.GetFileName(finalPath));
        Assert.Equal("existing", await File.ReadAllTextAsync(existingPath));
        Assert.Equal(data, await File.ReadAllBytesAsync(finalPath));
    }

    private static FileManifest CreateManifest(string fileName, byte[] data, int chunkSize) =>
        new(
            Guid.NewGuid(),
            fileName,
            data.LongLength,
            DateTimeOffset.UtcNow,
            chunkSize,
            Convert.ToHexString(SHA256.HashData(data)));

    private static async Task WriteChunkAsync(
        ResumableFileReceiver receiver,
        ReceiveSession session,
        byte[] data,
        int chunkIndex)
    {
        var chunk = new ChunkPlan(session.Manifest).GetChunk(chunkIndex);
        await using var content = new MemoryStream(data, chunk.OffsetAsInt(), chunk.Length, writable: false);
        var hash = Convert.ToHexString(SHA256.HashData(content));
        content.Position = 0;
        await receiver.WriteChunkAsync(session, chunkIndex, content, hash);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"LocalTransfer.Tests.{Guid.NewGuid():N}");
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

internal static class ChunkDescriptorExtensions
{
    public static int OffsetAsInt(this ChunkDescriptor chunk) => checked((int)chunk.Offset);
}
