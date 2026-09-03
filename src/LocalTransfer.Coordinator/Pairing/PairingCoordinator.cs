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

        if (!_tickets.TryConsume(submission.Secret))
        {
            throw new UnauthorizedAccessException("The pairing ticket is invalid or expired.");
        }

        var request = new PendingPairingRequest(
            Guid.NewGuid(),
            submission.Device,
            DateTimeOffset.UtcNow);
        if (!_requests.TryAdd(request.RequestId, request))
        {
            throw new InvalidOperationException("A pairing request identifier collision occurred.");
        }

        var info = request.ToInfo();
        PairingRequested?.Invoke(this, info);
        return new PairingSubmissionResponse(request.RequestId, info.Status);
    }

    public IReadOnlyList<PairingRequestInfo> GetPendingRequests() =>
        _requests.Values
            .Select(request => request.ToInfo())
            .Where(request => request.Status == PairingRequestStatus.Pending)
            .OrderBy(request => request.RequestedAtUtc)
            .ToArray();

    public async Task<bool> ApproveAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        if (!_requests.TryGetValue(requestId, out var request))
        {
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
            if (request.Status != PairingRequestStatus.Pending)
            {
                return false;
            }

            request.Status = PairingRequestStatus.Rejected;
            return true;
        }
    }

    public PairingPollResponse? Poll(Guid requestId)
    {
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

        if (device.ProtocolVersion < ProtocolConstants.MinimumSupportedVersion ||
            device.ProtocolVersion > ProtocolConstants.CurrentVersion)
        {
            throw new NotSupportedException("The device protocol version is not supported.");
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
        DateTimeOffset requestedAtUtc)
    {
        public object Gate { get; } = new();

        public Guid RequestId { get; } = requestId;

        public DeviceDescriptor Device { get; } = device;

        public DateTimeOffset RequestedAtUtc { get; } = requestedAtUtc;

        public PairingRequestStatus Status { get; set; } = PairingRequestStatus.Pending;

        public bool ApprovalInProgress { get; set; }

        public string? Credential { get; set; }

        public PairingRequestInfo ToInfo()
        {
            lock (Gate)
            {
                return new PairingRequestInfo(RequestId, Device, RequestedAtUtc, Status);
            }
        }
    }
}
