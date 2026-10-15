namespace LocalTransfer.Contracts.Protocol;

public static class ProtocolConstants
{
    public const int CurrentVersion = 1;
    public const int MinimumSupportedVersion = 1;
    public const int DefaultChunkSize = 4 * 1024 * 1024;

    /// <summary>
    /// Upper bound for peer-declared chunk sizes. The coordinator's HTTP body limit
    /// is derived from this value, so manifests with larger chunks could never be delivered.
    /// </summary>
    public const int MaxChunkSize = DefaultChunkSize;

    /// <summary>Upper bound for a single transfer so a malicious manifest cannot exhaust disk space.</summary>
    public const long MaxTransferLength = 1024L * 1024 * 1024 * 1024; // 1 TB

    /// <summary>
    /// Upper bound for the chunk count. Every status poll returns the missing-chunk list,
    /// so a huge count would let a peer amplify one request into a multi-gigabyte response.
    /// </summary>
    public const int MaxChunkCount = 262_144;
}
