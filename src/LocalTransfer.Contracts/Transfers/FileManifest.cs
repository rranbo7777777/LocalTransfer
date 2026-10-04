using LocalTransfer.Contracts.Protocol;

namespace LocalTransfer.Contracts.Transfers;

public sealed record FileManifest(
    Guid TransferId,
    string FileName,
    long Length,
    DateTimeOffset LastModifiedUtc,
    int ChunkSize,
    string Sha256Hex)
{
    public int ChunkCount => Length == 0
        ? 0
        : checked((int)((Length + ChunkSize - 1L) / ChunkSize));

    public void Validate()
    {
        if (TransferId == Guid.Empty)
        {
            throw new ArgumentException("TransferId cannot be empty.", nameof(TransferId));
        }

        if (string.IsNullOrWhiteSpace(FileName) || FileName.Contains('/') || FileName.Contains('\\'))
        {
            throw new ArgumentException("FileName must be a single non-empty file name.", nameof(FileName));
        }

        if (Length < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Length));
        }

        if (Length > ProtocolConstants.MaxTransferLength)
        {
            throw new ArgumentOutOfRangeException(nameof(Length), Length, "The declared transfer length exceeds the protocol limit.");
        }

        if (ChunkSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ChunkSize));
        }

        if (ChunkSize > ProtocolConstants.MaxChunkSize)
        {
            throw new ArgumentOutOfRangeException(nameof(ChunkSize), ChunkSize, "The declared chunk size exceeds the protocol limit.");
        }

        if (Length > 0)
        {
            var chunkCount = (Length + ChunkSize - 1L) / ChunkSize;
            if (chunkCount > ProtocolConstants.MaxChunkCount)
            {
                throw new ArgumentException(
                    "The declared length and chunk size would exceed the maximum chunk count.",
                    nameof(Length));
            }
        }

        if (Sha256Hex.Length != 64 || !Sha256Hex.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("Sha256Hex must contain a 32-byte SHA-256 value.", nameof(Sha256Hex));
        }
    }
}
