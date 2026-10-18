using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LocalTransfer.Contracts.Discovery;
using LocalTransfer.Contracts.Protocol;

namespace LocalTransfer.Coordinator.Discovery;

/// <summary>
/// Broadcasts this computer's identity so a phone that paired with it earlier can find it again
/// without being shown a fresh QR code.
/// <para>
/// Only the desktop announces. The phone never has to be discoverable because it is always the one
/// dialling out, and that is what keeps the security model intact: discovering a computer grants no
/// access whatsoever, the credential stored at pairing time still does.
/// </para>
/// </summary>
public sealed class MulticastAnnouncer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private static readonly IPEndPoint Target = new(DiscoveryConstants.Group, DiscoveryConstants.Port);

    private readonly byte[] _payload;

    /// <summary>
    /// The adapter the announcement leaves through. Pinning it matters on a machine that also runs
    /// a VPN: without it the datagram takes the tunnel and never reaches the phone's LAN.
    /// </summary>
    private readonly IPAddress? _interfaceAddress;

    private readonly CancellationTokenSource _stop = new();

    private Task? _loop;

    public MulticastAnnouncer(string endpoint, string fingerprint, string alias, IPAddress? interfaceAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (fingerprint.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("A SHA-256 certificate fingerprint is required.", nameof(fingerprint));
        }

        _interfaceAddress = interfaceAddress;
        _payload = JsonSerializer.SerializeToUtf8Bytes(
            new MulticastAnnouncement(
                MulticastAnnouncement.ServiceName,
                ProtocolConstants.CurrentVersion,
                string.IsNullOrWhiteSpace(alias) ? "LocalTransfer" : alias,
                fingerprint.ToUpperInvariant(),
                endpoint),
            SerializerOptions);

        if (_payload.Length > DiscoveryConstants.MaxDatagramBytes)
        {
            throw new ArgumentException("The announcement does not fit in one datagram.", nameof(alias));
        }
    }

    /// <summary>The announcement as it goes on the wire. Exposed so tests can assert its shape.</summary>
    public IReadOnlyList<byte> Payload => _payload;

    public void Start()
    {
        _loop ??= RunAsync(_stop.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var delay in DiscoveryConstants.AnnounceBurstDelays)
        {
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await SendOnceAsync(cancellationToken);
        }

        try
        {
            using var timer = new PeriodicTimer(DiscoveryConstants.AnnounceInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await SendOnceAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private async Task SendOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var socket = new UdpClient();
            // The LAN is one hop away; a TTL above 1 only risks leaking the announcement off-site.
            socket.Client.SetSocketOption(
                SocketOptionLevel.IP,
                SocketOptionName.MulticastTimeToLive,
                1);

            if (_interfaceAddress is not null)
            {
                socket.Client.SetSocketOption(
                    SocketOptionLevel.IP,
                    SocketOptionName.MulticastInterface,
                    _interfaceAddress.GetAddressBytes());
            }

            await socket.SendAsync(_payload, Target, cancellationToken);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            // A missing adapter or a network change is transient; the next tick tries again.
            CoordinatorLog.Write($"discovery announce failed: {exception.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
    }
}
