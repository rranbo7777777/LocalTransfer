namespace LocalTransfer.Client;

/// <summary>
/// One file of a batch, described the same way the single-file path describes a file.
/// <para>
/// <paramref name="Sha256Hex"/> and <paramref name="Length"/> are optional: when the caller already
/// knows them (because it read the file once while staging it), the upload skips hashing entirely.
/// </para>
/// </summary>
public sealed record BatchUploadItem(
    string FileName,
    Func<CancellationToken, Task<Stream>> OpenReadAsync,
    DateTimeOffset? LastModifiedUtc = null,
    string? Sha256Hex = null,
    long? Length = null);

/// <summary>
/// What happened to each file of a batch, reported by its position in the submitted list so the
/// caller can map results back without keeping a transfer-id table of its own.
/// <para>
/// The three outcomes are separate because the user acts on them differently: retry the failed
/// ones, accept that the rejected ones were declined, move on from the rest.
/// </para>
/// </summary>
public sealed record BatchUploadResult(
    IReadOnlyList<int> CompletedIndexes,
    IReadOnlyList<int> RejectedIndexes,
    IReadOnlyList<BatchUploadFailure> Failed);

public sealed record BatchUploadFailure(int Index, Guid TransferId, string FileName, string Reason);
