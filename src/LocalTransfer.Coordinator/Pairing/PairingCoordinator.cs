using System.Collections.Concurrent;
using System.Security.Cryptography;
using LocalTransfer.Contracts.Devices;
using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Contracts.Pairing;
using LocalTransfer.Coordinator.Devices;
using LocalTransfer.Core.Security;

namespace LocalTransfer.Coordinator.Pairing;

public sealed class PairingCoordinator
{
    // Requests whose client never polls (crash, abandoned pairing) must not accumulate:
    // every status is pruned once the entry outlives this window. Clients stop polling
    // when their bootstrap expires, which is bounded by the ticket lifetime (minutes).
    private static readonly TimeSpan RequestRetention = TimeSpan.FromMinutes(15);

    private readonly PairingTicketService _tickets = new();
    private readonly ConcurrentDictionary<Guid, PendingPairingRequest> _requests = new();
    private readonly TrustedDeviceStore _trustedDevices;

    public PairingCoordinator(TrustedDeviceStore trustedDevices)
    {
        _trustedDevices = trustedDevices;
    }

    public event EventHandler<PairingRequestInfo>? PairingRequested;

    public PairingTicket CreateTicket(TimeSpan lifetime) => _tickets.Create(lifetime);

    public PairingSubmissionResponse Submit(PairingSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ValidateDevice(submission.Device);

        if (!_tickets.TryConsume(submission.Secret, out var ticketExpiresAtUtc))
        {
            throw new UnauthorizedAccessException("The pairing ticket is invalid or expired.");
        }

        var request = new PendingPairingRequest(
            Guid.NewGuid(),
            submission.Device,
            DateTimeOffset.UtcNow,
            ticketExpiresAtUtc);
        if (!_requests.TryAdd(request.RequestId, request))
        {
            throw new InvalidOperationException("A pairing request identifier collision occurred.");
        }

        PruneExpiredRequests();
        var info = request.ToInfo();
        PairingRequested?.Invoke(this, info);
        return new PairingSubmissionResponse(request.RequestId, info.Status);
    }

    public IReadOnlyList<PairingRequestInfo> GetPendingRequests()
    {
        var now = DateTimeOffset.UtcNow;
        return _requests.Values
            .Where(request => request.IsPending(now))
            .OrderBy(request => request.RequestedAtUtc)
            .Select(request => request.ToInfo())
            .ToArray();
    }

    public async Task<bool> ApproveAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        if (!_requests.TryGetValue(requestId, out var request))
        {
            return false;
        }

        // The pairing ticket also bounds how long the client keeps polling. Approving a request
        // whose ticket already lapsed would write a credential the phone never receives (it gave
        // up when its bootstrap expired), so the desktop would trust a credential that does not
        // exist on the phone — and every later transfer from that phone would fail with 401 until
        // the user paired again by hand. Refuse instead; the desktop reports it to the user.
        if (DateTimeOffset.UtcNow > request.ExpiresAtUtc)
        {
            lock (request.Gate)
            {
                if (request.Status == PairingRequestStatus.Pending)
                {
                    request.Status = PairingRequestStatus.Rejected;
                }
            }

            return false;
        }

        var credential = CreateCredential();
        lock (request.Gate)
        {
            if (request.Status != PairingRequestStatus.Pending || request.ApprovalInProgress)
            {
                return false;
            }

            request.ApprovalInProgress = true;
        }

        try
        {
            await _trustedDevices.AddOrUpdateAsync(request.Device, credential, cancellationToken);
            lock (request.Gate)
            {
                request.Credential = credential;
                request.Status = PairingRequestStatus.Approved;
                request.ApprovalInProgress = false;
            }

            return true;
        }
        catch
        {
            lock (request.Gate)
            {
                request.ApprovalInProgress = false;
                request.Credential = null;
            }

            throw;
        }
    }

    public bool Reject(Guid requestId)
    {
        if (!_requests.TryGetValue(requestId, out var request))
        {
            return false;
        }

        lock (request.Gate)
        {
            // ApprovalInProgress must block Reject: otherwise a rejection landing inside the
            // approval's disk-write window would be overwritten by ApproveAsync, and the
            // rejected peer would still receive a credential on its next poll.
            if (request.Status != PairingRequestStatus.Pending || request.ApprovalInProgress)
            {
                return false;
            }

            request.Status = PairingRequestStatus.Rejected;
            return true;
        }
    }

    public PairingPollResponse? Poll(Guid requestId)
    {
        PruneExpiredRequests();
        if (!_requests.TryGetValue(requestId, out var request))
        {
            return null;
        }

        lock (request.Gate)
        {
            if (request.Status != PairingRequestStatus.Approved)
            {
                return new PairingPollResponse(request.Status, null);
            }

            var credential = request.Credential;
            request.Credential = null;
            request.Status = PairingRequestStatus.CredentialDelivered;
            return new PairingPollResponse(PairingRequestStatus.Approved, credential);
        }
    }

    private static void ValidateDevice(DeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.DeviceId == Guid.Empty || string.IsNullOrWhiteSpace(device.DisplayName))
        {
            throw new ArgumentException("A valid device identifier and display name are required.", nameof(device));
        }

        // The display name is stored permanently in the trusted-device store and rendered in
        // desktop dialogs; unbounded or control-bearing values would let a submission bloat
        // the store and spoof or garble the approval prompt.
        if (device.DisplayName.Length > MaxDisplayNameLength ||
            device.DisplayName.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"The device display name must be at most {MaxDisplayNameLength} characters without control characters.",
                nameof(device));
        }

        if (device.Platform.Length > MaxPlatformLength)
        {
            throw new ArgumentException(
                $"The device platform must be at most {MaxPlatformLength} characters.",
                nameof(device));
        }

        if (device.ProtocolVersion < ProtocolConstants.MinimumSupportedVersion ||
            device.ProtocolVersion > ProtocolConstants.CurrentVersion)
        {
            throw new NotSupportedException("The device protocol version is not supported.");
        }
    }

    private const int MaxDisplayNameLength = 64;

    private const int MaxPlatformLength = 32;

    private void PruneExpiredRequests()
    {
        var oldestAllowed = DateTimeOffset.UtcNow - RequestRetention;
        foreach (var pair in _requests)
        {
            if (pair.Value.RequestedAtUtc < oldestAllowed)
            {
                _requests.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string CreateCredential() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class PendingPairingRequest(
        Guid requestId,
        DeviceDescriptor device,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        public object Gate { get; } = new();

        public Guid RequestId { get; } = requestId;

        public DeviceDescriptor Device { get; } = device;

        public DateTimeOffset RequestedAtUtc { get; } = requestedAtUtc;

        /// <summary>When the pairing ticket that created this request stops being usable.</summary>
        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;

        public PairingRequestStatus Status { get; set; } = PairingRequestStatus.Pending;

        public bool ApprovalInProgress { get; set; }

        public string? Credential { get; set; }

        public bool IsPending(DateTimeOffset now)
        {
            lock (Gate)
            {
                return Status == PairingRequestStatus.Pending && now <= ExpiresAtUtc;
            }
        }

        public PairingRequestInfo ToInfo()
        {
            lock (Gate)
            {
                return new PairingRequestInfo(RequestId, Device, RequestedAtUtc, Status, ExpiresAtUtc);
            }
        }
    }
}
