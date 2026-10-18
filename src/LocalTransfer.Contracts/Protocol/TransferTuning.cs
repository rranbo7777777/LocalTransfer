namespace LocalTransfer.Contracts.Protocol;

/// <summary>
/// Every timeout, retry and retention value the transfer pipeline depends on, in one place.
/// <para>
/// These are not arbitrary numbers: the relationships between them are load-bearing, and several
/// past defects came from changing one value without the other it is paired with. Read the note on
/// a member before changing it.
/// </para>
/// <list type="bullet">
/// <item>An inbound offer must outlive the client's approval wait, or a phone still polling sees
/// its offer disappear underneath it (see <see cref="OfferLifetime"/> vs <see cref="ClientApprovalWait"/>).</item>
/// <item>The pairing poll limiter must tolerate the client's own poll rate, or a slow human
/// answering the desktop dialog trips HTTP 429 and aborts the pairing (see <see cref="PairingPollPermitLimit"/>).</item>
/// </list>
/// </summary>
public static class TransferTuning
{
    // ------------------------------------------------------------------ 配对

    /// <summary>
    /// How long a pairing QR/bootstrap stays usable, and therefore how long the phone keeps
    /// polling. Approving after this has lapsed is refused, because the phone has already given up.
    /// </summary>
    public static readonly TimeSpan PairingTicketLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Delay between pairing status polls on the client.</summary>
    public static readonly TimeSpan PairingPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Base delay before retrying a pairing request; multiplied by the attempt number.</summary>
    public static readonly TimeSpan PairingRetryBaseDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Attempts per pairing request before the failure is surfaced.</summary>
    public const int PairingMaxAttempts = 4;

    /// <summary>
    /// Pairing submissions per minute per source address. Cheap to serve and consumes a one-time
    /// ticket, so this stays deliberately tight.
    /// </summary>
    public const int PairingSubmitPermitLimit = 30;

    /// <summary>
    /// Pairing status polls per minute per source address. Must stay comfortably above
    /// 60 / <see cref="PairingPollInterval"/> (= 30): the poll runs on a timer for as long as a
    /// human leaves the desktop dialog open, so a budget near the client's own rate guarantees
    /// HTTP 429 mid-pairing.
    /// </summary>
    public const int PairingPollPermitLimit = 240;

    /// <summary>Window of both pairing rate limiters.</summary>
    public static readonly TimeSpan PairingRateLimitWindow = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long an unanswered pairing request stays queryable before it is pruned. Must outlive
    /// <see cref="PairingTicketLifetime"/>, since clients stop polling when their bootstrap expires.
    /// </summary>
    public static readonly TimeSpan PairingRequestRetention = TimeSpan.FromMinutes(15);

    // ------------------------------------------------------------------ 入站邀约（手机 → 电脑）

    /// <summary>
    /// How long an unanswered "may I send you this file?" prompt stays open. It has to outlast
    /// <see cref="ClientApprovalWait"/> so a phone that is still polling never sees its offer
    /// disappear. An ignored prompt also releases its per-device offer slot only when this expires.
    /// </summary>
    public static readonly TimeSpan OfferLifetime = TimeSpan.FromMinutes(11);

    // ------------------------------------------------------------------ 传输

    /// <summary>How long the phone waits for the desktop to accept an upload offer.</summary>
    public static readonly TimeSpan ClientApprovalWait = TimeSpan.FromMinutes(10);

    /// <summary>Delay between upload-approval polls on the client.</summary>
    public static readonly TimeSpan TransferPollInterval = TimeSpan.FromMilliseconds(750);

    /// <summary>
    /// Base delay before retrying a transfer request. The delay grows exponentially (×3 per
    /// attempt) up to <see cref="TransferRetryMaxDelay"/>.
    /// <para>
    /// A flat sub-second backoff was the reason a brief Wi-Fi drop turned into a permanent
    /// failure: the three attempts it allowed had all expired long before the interface came
    /// back, so the transfer was abandoned even though the coordinator still held every chunk
    /// that had already arrived.
    /// </para>
    /// </summary>
    public static readonly TimeSpan TransferRetryBaseDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Upper bound of a single retry backoff step.</summary>
    public static readonly TimeSpan TransferRetryMaxDelay = TimeSpan.FromSeconds(10);

    /// <summary>Attempts per transfer request before the failure is surfaced.</summary>
    public const int TransferMaxAttempts = 5;

    /// <summary>
    /// Backoff before retry number <paramref name="attempt"/> (1-based): exponential from
    /// <see cref="TransferRetryBaseDelay"/>, capped at <see cref="TransferRetryMaxDelay"/>.
    /// </summary>
    public static TimeSpan GetTransferRetryDelay(int attempt)
    {
        if (attempt < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(attempt));
        }

        var scaled = TransferRetryBaseDelay.Ticks * Math.Pow(3, attempt - 1);
        return scaled >= TransferRetryMaxDelay.Ticks
            ? TransferRetryMaxDelay
            : TimeSpan.FromTicks((long)scaled);
    }

    /// <summary>
    /// Worst-case time a single request spends waiting between its attempts. Callers that must
    /// bound a poll loop (see the pairing and transfer poll budgets) use this instead of
    /// assuming a linear backoff.
    /// </summary>
    public static TimeSpan TotalTransferRetryDelay
    {
        get
        {
            var total = TimeSpan.Zero;
            for (var attempt = 1; attempt < TransferMaxAttempts; attempt++)
            {
                total += GetTransferRetryDelay(attempt);
            }

            return total;
        }
    }

    /// <summary>
    /// How long the client keeps resuming one upload after transient failures before giving up.
    /// The coordinator retains its per-chunk checkpoint for this whole window, so a transfer that
    /// survives a network blip resumes from where it stopped instead of starting over.
    /// </summary>
    public static readonly TimeSpan TransferRecoveryBudget = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Delay between recovery rounds once a round failed. Kept short: the round also probes the
    /// coordinator's health endpoint, which is what actually decides when to keep going.
    /// </summary>
    public static readonly TimeSpan TransferRecoveryRetryDelay = TimeSpan.FromSeconds(3);

    /// <summary>Timeout of the health probe used to decide whether the network is back.</summary>
    public static readonly TimeSpan RecoveryProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the client waits for a batch approval. Longer than a single offer because the
    /// desktop reviews a list rather than answering one prompt, and it has to outlive the
    /// coordinator's own offer lifetime or the client gives up while the desktop is still deciding.
    /// </summary>
    public static readonly TimeSpan BatchApprovalTimeout = OfferLifetime + TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many recovery rounds in a row may make zero progress before the upload gives up.
    /// <para>
    /// A round that fails without moving a single byte means the source itself is unusable (a
    /// revoked content-URI grant, a file deleted mid-transfer). Retrying that for the whole
    /// recovery budget would hold the UI for half an hour to reach the same error.
    /// </para>
    /// </summary>
    public const int MaxStalledRecoveryRounds = 3;

    /// <summary>
    /// How long a terminal-state transfer is kept so the peer can still observe the final status.
    /// Bounds dictionary growth in the long-running desktop service.
    /// </summary>
    public static readonly TimeSpan TerminalRetention = TimeSpan.FromMinutes(10);

    /// <summary>Per-request HTTP timeout for client calls.</summary>
    public static readonly TimeSpan HttpClientTimeout = TimeSpan.FromMinutes(2);

    // ------------------------------------------------------------------ 界面刷新

    /// <summary>How often the desktop "transfers" page refreshes while it is visible.</summary>
    public static readonly TimeSpan DesktopTransfersRefreshInterval = TimeSpan.FromSeconds(2);
}
