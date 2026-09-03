using System.Text.Json;
using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Contracts.Transfers;

namespace LocalTransfer.Protocol.Tests;

public sealed class FileManifestTests
{
    [Fact]
    public void Manifest_RoundTripsThroughJson()
    {
        var manifest = new FileManifest(
            Guid.NewGuid(),
            "测试文件.zip",
            9_000_000,
            DateTimeOffset.Parse("2026-08-25T08:00:00+08:00"),
            ProtocolConstants.DefaultChunkSize,
            new string('A', 64));

        var json = JsonSerializer.Serialize(manifest);
        var restored = JsonSerializer.Deserialize<FileManifest>(json);

        Assert.Equal(manifest, restored);
        restored!.Validate();
    }

    [Theory]
    [InlineData("folder/file.txt")]
    [InlineData("folder\\file.txt")]
    [InlineData("")]
    public void Manifest_RejectsUnsafeFileName(string fileName)
    {
        var manifest = CreateManifest(fileName, new string('0', 64));

        Assert.Throws<ArgumentException>(manifest.Validate);
    }

    [Fact]
    public void Manifest_RejectsInvalidHash()
    {
        var manifest = CreateManifest("file.bin", "not-a-sha256");

        Assert.Throws<ArgumentException>(manifest.Validate);
    }

    private static FileManifest CreateManifest(string fileName, string hash) =>
        new(Guid.NewGuid(), fileName, 10, DateTimeOffset.UtcNow, 4, hash);
}
