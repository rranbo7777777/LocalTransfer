namespace LocalTransfer.Contracts.Transfers;

/// <summary>
/// One "may I send you these N files?" request, replacing N separate offers that each needed their
/// own confirmation.
/// <para>
/// The coordinator answers only after a human has decided, so this call is long-running by design.
/// A client must not apply its usual per-request timeout to it.
/// </para>
/// </summary>
public sealed record BatchSubmission(Guid BatchId, IReadOnlyList<FileManifest> Files);

/// <summary>
/// Which files of a batch the user accepted. Rejected files stay rejected: re-offering them would
/// just ask the same question again, so the client reports them to the user instead.
/// </summary>
public sealed record BatchSubmissionResult(
    Guid BatchId,
    IReadOnlyList<Guid> Accepted,
    IReadOnlyList<Guid> Rejected);

public static class BatchLimits
{
    /// <summary>
    /// Upper bound per batch. One confirmation covering hundreds of files is the point; an
    /// unbounded list would let a single request allocate an arbitrary number of transfers.
    /// </summary>
    public const int MaxFiles = 500;
}
