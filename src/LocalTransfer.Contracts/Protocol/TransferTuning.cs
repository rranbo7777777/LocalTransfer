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

    /// <summary>Base delay before retrying a transfer request; multiplied by the attempt number.</summary>
    public static readonly TimeSpan TransferRetryBaseDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Attempts per transfer request before the failure is surfaced.</summary>
    public const int TransferMaxAttempts = 3;

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
