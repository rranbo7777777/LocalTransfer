using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using LocalTransfer.Contracts.Discovery;

namespace LocalTransfer.Mobile;

/// <summary>
/// Listens for the desktop's discovery broadcast, so a paired phone can find the computer again
/// after its address changes instead of demanding a fresh QR scan.
/// <para>
/// Android only delivers UDP multicast to an app that holds a <c>MulticastLock</c>. Without it the
/// socket binds successfully and then stays silent forever, which is the single most common reason
/// "auto discovery does not work". The lock is released on dispose so it does not keep the Wi-Fi
/// radio awake after the page goes away.
/// </para>
/// </summary>
internal sealed class MulticastDiscovery : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly CancellationTokenSource _stop = new();

    private UdpClient? _socket;

#if ANDROID
    private Android.Net.Wifi.WifiManager.MulticastLock? _multicastLock;
#endif

    public void Start()
    {
        AcquireMulticastLock();

        var socket = new UdpClient();
        try
        {
            // A lingering instance of this app must not make binding fail outright.
            socket.Client.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryConstants.Port));
            socket.JoinMulticastGroup(DiscoveryConstants.Group);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
    }

    /// <summary>
    /// Waits for an announcement from the computer whose certificate fingerprint matches, then
    /// returns its current endpoint.
    /// <para>
    /// Announcements from anything else on the group are ignored. Matching on the fingerprint the
    /// phone pinned during pairing is precisely what makes the address carried by a plain datagram
    /// safe to trust.
    /// </para>
    /// </summary>
    public async Task<string?> FindAsync(
        string expectedFingerprint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var socket = _socket ?? throw new InvalidOperationException("Discovery has not been started.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _stop.Token);
        deadline.CancelAfter(timeout);

        try
        {
            while (!deadline.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(deadline.Token);
                if (received.Buffer.Length is 0 or > DiscoveryConstants.MaxDatagramBytes)
                {
                    continue;
                }

                MulticastAnnouncement? announcement;
                try
                {
                    announcement = JsonSerializer.Deserialize<MulticastAnnouncement>(
                        received.Buffer,
                        SerializerOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (announcement is null ||
                    !string.Equals(
                        announcement.Service,
                        MulticastAnnouncement.ServiceName,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        announcement.Fingerprint,
                        expectedFingerprint,
                        StringComparison.OrdinalIgnoreCase) ||
                    !Uri.TryCreate(announcement.Endpoint, UriKind.Absolute, out var endpoint) ||
                    endpoint.Scheme != Uri.UriSchemeHttps)
                {
                    continue;
                }

                return announcement.Endpoint;
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out, or the caller cancelled.
        }

        return null;
    }

    private void AcquireMulticastLock()
    {
#if ANDROID
        try
        {
            var wifi = (Android.Net.Wifi.WifiManager?)Android.App.Application.Context
                .GetSystemService(Android.Content.Context.WifiService);
            _multicastLock = wifi?.CreateMulticastLock("LocalTransferDiscovery");
            if (_multicastLock is not null)
            {
                _multicastLock.SetReferenceCounted(false);
                _multicastLock.Acquire();
            }
        }
        catch (Exception exception)
        {
            // Without the lock multicast is simply never delivered; discovery then degrades to the
            // QR path, which has to keep working. Logged rather than thrown, for that reason.
            MobileDiagnostics.Log("discovery-lock", exception);
        }
#endif
    }

    public void Dispose()
    {
        _stop.Cancel();
        _socket?.Dispose();
        _socket = null;

#if ANDROID
        try
        {
            _multicastLock?.Release();
            _multicastLock?.Dispose();
        }
        catch (Exception exception)
        {
            MobileDiagnostics.Log("discovery-lock", exception);
        }

        _multicastLock = null;
#endif

        _stop.Dispose();
    }
}
