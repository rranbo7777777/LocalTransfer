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

    /// <summary>
    /// Delay between pairing status polls. The desktop approval dialog stays open until a
    /// human answers it, so this value multiplied by the ticket lifetime is the number of
    /// requests the coordinator's pairing poll limiter has to tolerate per minute
    /// (60 / 2 s = 30 requests). Shrinking it without widening the server-side budget makes
    /// a slow human beat the rate limiter and aborts the pairing with HTTP 429.
    /// </summary>
    public static readonly TimeSpan PairingPollInterval = TimeSpan.FromSeconds(2);
}
