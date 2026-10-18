using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Contracts.Pairing;
using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Coordinator.Devices;
using LocalTransfer.Coordinator.Discovery;
using LocalTransfer.Coordinator.History;
using LocalTransfer.Coordinator.Pairing;
using LocalTransfer.Coordinator.Security;
using LocalTransfer.Coordinator.Transfers;
using LocalTransfer.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LocalTransfer.Coordinator;

public sealed class CoordinatorHost : IAsyncDisposable
{
    private readonly CoordinatorOptions _options;
    private readonly Func<X509Certificate2> _certificateFactory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private WebApplication? _application;
    private X509Certificate2? _certificate;
    private MulticastAnnouncer? _announcer;

    public CoordinatorHost(
        CoordinatorOptions? options = null,
        Func<X509Certificate2>? certificateFactory = null)
    {
        _options = options ?? new CoordinatorOptions();
        _certificateFactory = certificateFactory ?? (() => new LocalCertificateProvider().GetOrCreate());
        Directory.CreateDirectory(_options.DataDirectory);
        Directory.CreateDirectory(_options.ReceiveDirectory);
        CoordinatorLog.Initialize(_options.DataDirectory);
        TrustedDevices = new TrustedDeviceStore(_options.DataDirectory);
        History = new TransferHistoryStore(_options.DataDirectory);
        Pairing = new PairingCoordinator(TrustedDevices);
        InboundTransfers = new InboundTransferCoordinator(_options.ReceiveDirectory, history: History);
        OutboundTransfers = new OutboundTransferCoordinator(_options.DataDirectory, History);
    }

    public PairingCoordinator Pairing { get; }

    /// <summary>Terminal-state record for both directions; backs the history page.</summary>
    public TransferHistoryStore History { get; }

    public TrustedDeviceStore TrustedDevices { get; }

    public InboundTransferCoordinator InboundTransfers { get; }

    public OutboundTransferCoordinator OutboundTransfers { get; }

    public bool IsRunning => _application is not null;

    public string Endpoint => $"https://{FormatHost(GetAdvertisedAddress())}:{_options.Port}";

    public int Port => _options.Port;

    public string ReceiveDirectory => _options.ReceiveDirectory;

    public string DataDirectory => _options.DataDirectory;

    public string? CertificateSha256
    {
        get
        {
            var certificate = _certificate;
            return certificate is null
                ? null
                : LocalCertificateProvider.GetSha256Fingerprint(certificate);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_application is not null)
            {
                return;
            }

            var certificate = _certificateFactory();
            _certificate = certificate;
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(CoordinatorHost).Assembly.FullName
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(serverOptions => ConfigureKestrel(serverOptions, certificate));
            builder.Services.ConfigureHttpJsonOptions(options =>
                options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
            builder.Services.AddRateLimiter(options =>
            {
                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                // The pairing endpoints are the only unauthenticated surface; partition by
                // remote address so one LAN peer cannot flood the approval dialog or the
                // poll loop without throttling other devices.
                //
                // Submit and poll get separate budgets. They used to share one 30/min window,
                // which the legitimate client then tripped on its own: the poll runs on a
                // timer for as long as a human leaves the desktop dialog open, so a shared
                // budget guaranteed HTTP 429 after ~22 seconds and aborted every pairing the
                // user did not answer instantly. Each named policy owns its counter cache,
                // so the two limits below do not consume each other's permits.
                options.AddPolicy(PairingSubmitRateLimitPolicy, context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            // Cheap to create and consumes a one-time ticket; keep this tight.
                            PermitLimit = PairingSubmitPermitLimit,
                            Window = TransferTuning.PairingRateLimitWindow,
                            QueueLimit = 0
                        }));
                options.AddPolicy(PairingPollRateLimitPolicy, context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            // Must stay well above 60 / TransferTuning.PairingPollInterval
                            // (30/min) so a slow human cannot exhaust the budget mid-pairing.
                            PermitLimit = PairingPollPermitLimit,
                            Window = TransferTuning.PairingRateLimitWindow,
                            QueueLimit = 0
                        }));
            });

            var application = builder.Build();
            application.UseRateLimiter();
            application.Use(EnforcePerAddressLimit());
            MapEndpoints(application);

            try
            {
                await application.StartAsync(cancellationToken);
                _application = application;
                StartDiscovery(certificate);
            }
            catch
            {
                await application.DisposeAsync();
                certificate.Dispose();
                _certificate = null;
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public PairingBootstrap CreatePairingBootstrap(TimeSpan? lifetime = null)
    {
        // Snapshot the certificate: StopAsync may null the field concurrently during shutdown.
        var certificate = _certificate;
        if (certificate is null)
        {
            throw new InvalidOperationException("The coordinator is not running.");
        }

        PairingTicket ticket = Pairing.CreateTicket(lifetime ?? TransferTuning.PairingTicketLifetime);
        return new PairingBootstrap(
            Endpoint,
            LocalCertificateProvider.GetSha256Fingerprint(certificate),
            ProtocolConstants.CurrentVersion,
            ticket.Secret,
            ticket.ExpiresAtUtc);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var application = Interlocked.Exchange(ref _application, null);
            if (_announcer is not null)
            {
                await _announcer.DisposeAsync();
                _announcer = null;
            }

            if (application is not null)
            {
                await application.StopAsync(cancellationToken);
                await application.DisposeAsync();
            }

            _certificate?.Dispose();
            _certificate = null;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Starts the discovery broadcast.
    /// <para>
    /// A failure here is logged and swallowed: a computer that cannot announce itself is still
    /// perfectly usable through the QR code, so it must never be why startup fails.
    /// </para>
    /// </summary>
    private void StartDiscovery(X509Certificate2 certificate)
    {
        try
        {
            var advertised = GetAdvertisedAddress();
            _announcer = new MulticastAnnouncer(
                Endpoint,
                LocalCertificateProvider.GetSha256Fingerprint(certificate),
                Environment.MachineName,
                advertised.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    ? advertised
                    : null);
            _announcer.Start();
            CoordinatorLog.Write($"discovery: announcing {Endpoint} on the multicast group");
        }
        catch (Exception exception)
        {
            _announcer = null;
            CoordinatorLog.Write(
                $"discovery: could not start: {exception.GetType().Name}: {exception.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await InboundTransfers.DisposeAsync();
        OutboundTransfers.Dispose();
    }

    private void ConfigureKestrel(KestrelServerOptions serverOptions, X509Certificate2 certificate)
    {
        serverOptions.AddServerHeader = false;

        // One chunk per request, plus a margin. The previous bound sat exactly one chunk size above
        // the body, so anything that pushed a request a few bytes over - a larger peer-declared
        // chunk size, a protocol bump - would have surfaced as a silent 413 the client never retries.
        serverOptions.Limits.MaxRequestBodySize = ProtocolConstants.MaxChunkSize + (1024 * 1024);

        // A backgrounded phone uploading over a weak link can idle well past the 5 s default grace
        // period, and Kestrel aborts the request when the body rate drops under 240 B/s. Relax the
        // floor and stretch the grace period instead of losing the transfer to a speed heuristic.
        serverOptions.Limits.MinRequestBodyDataRate = new MinDataRate(
            bytesPerSecond: 100,
            gracePeriod: TimeSpan.FromSeconds(30));

        // The phone may sit on the approval dialog (inbound) or poll for queued files (outbound)
        // between requests, so keep idle connections around longer than the 130 s default.
        serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(3);

        // Bound the descriptors this process holds. A single peer cannot exceed this today because
        // it talks to one device at a time, but the LAN limit keeps a misbehaving peer contained.
        serverOptions.Limits.MaxConcurrentConnections = 64;

        serverOptions.Listen(_options.ListenAddress, _options.Port, listenOptions =>
            listenOptions.UseHttps(certificate));
    }

    /// <summary>
    /// Caps how many requests one peer may hold at once. Kestrel's own limit is global, so without
    /// this a single misbehaving client could occupy every slot and lock out the real phone.
    /// </summary>
    private static Func<HttpContext, Func<Task>, Task> EnforcePerAddressLimit()
    {
        var limiter = new PerAddressLimiter();
        return async (context, next) =>
        {
            var address = context.Connection.RemoteIpAddress;
            if (address is null || IPAddress.IsLoopback(address))
            {
                // The desktop's own UI talks to it over loopback; never throttle that.
                await next();
                return;
            }

            if (!limiter.TryAcquire(address))
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            try
            {
                await next();
            }
            finally
            {
                limiter.Release(address);
            }
        };
    }

    private void MapEndpoints(WebApplication application)
    {
        application.MapGet("/api/v1/health", () => Results.Ok(new
        {
            service = "LocalTransfer",
            protocolVersion = ProtocolConstants.CurrentVersion,
            status = "ready"
        }));

        application.MapPost("/api/v1/pairing", (PairingSubmission submission) =>
        {
            try
            {
                return Results.Accepted(value: Pairing.Submit(submission));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.Unauthorized();
            }
            catch (NotSupportedException exception)
            {
                return Results.Problem(exception.Message, statusCode: StatusCodes.Status426UpgradeRequired);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).RequireRateLimiting(PairingSubmitRateLimitPolicy);

        application.MapGet("/api/v1/pairing/{requestId:guid}", (Guid requestId) =>
        {
            var response = Pairing.Poll(requestId);
            return response is null ? Results.NotFound() : Results.Ok(response);
        }).RequireRateLimiting(PairingPollRateLimitPolicy);

        application.MapPost("/api/v1/transfers", (HttpRequest request, FileManifest manifest) =>
        {
            if (!TryAuthenticate(request, out var deviceId))
            {
                return Results.Unauthorized();
            }

            try
            {
                return Results.Accepted(value: InboundTransfers.SubmitOffer(deviceId, manifest));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        // One prompt for a whole batch. The handler deliberately does not return until the desktop
        // has answered: the client waits on this response instead of polling per file, which is
        // what turns N confirmations into one.
        application.MapPost("/api/v1/batches", async (
            HttpRequest request,
            BatchSubmission submission,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthenticate(request, out var deviceId))
            {
                return Results.Unauthorized();
            }

            try
            {
                var result = await InboundTransfers.SubmitBatchAsync(
                    deviceId,
                    submission.BatchId,
                    submission.Files,
                    cancellationToken);
                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        application.MapGet("/api/v1/transfers/{transferId:guid}", async (
            HttpRequest request,
            Guid transferId,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthenticate(request, out var deviceId))
            {
                return Results.Unauthorized();
            }

            var transfer = await InboundTransfers.GetAsync(transferId, deviceId, cancellationToken);
            return transfer is null ? Results.NotFound() : Results.Ok(transfer);
        });

        application.MapPut("/api/v1/transfers/{transferId:guid}/chunks/{chunkIndex:int}", async (
            HttpRequest request,
            Guid transferId,
            int chunkIndex,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthenticate(request, out var deviceId))
            {
                return Results.Unauthorized();
            }

            var chunkSha256 = request.Headers["X-Chunk-SHA256"].ToString();
            if (string.IsNullOrWhiteSpace(chunkSha256))
            {
                return Results.BadRequest(new { error = "The X-Chunk-SHA256 header is required." });
            }

            try
            {
                await InboundTransfers.WriteChunkAsync(
                    transferId,
                    deviceId,
                    chunkIndex,
                    request.Body,
                    chunkSha256,
                    cancellationToken);
                return Results.NoContent();
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or EndOfStreamException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Exception messages for file I/O contain local filesystem paths that must not reach the peer.
                return Results.Problem("The chunk could not be stored.");
            }
        });

        application.MapPost("/api/v1/transfers/{transferId:guid}/complete", async (
            HttpRequest request,
            Guid transferId,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthenticate(request, out var deviceId))
            {
                return Results.Unauthorized();
            }

            try
            {
                var finalPath = await InboundTransfers.CompleteAsync(
                    transferId,
                    deviceId,
                    cancellationToken);
                return Results.Ok(new { finalFileName = Path.GetFileName(finalPath) });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
            catch (InvalidDataException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Exception messages for file I/O contain local filesystem paths that must not reach the peer.
                return Results.Problem("The received file could not be finalized.");
            }
        });

        application.MapDelete("/api/v1/transfers/{transferId:guid}", async (
            HttpRequest request,
            Guid transferId,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthenticate(request, out var deviceId))
            {
                return Results.Unauthorized();
            }

            return await InboundTransfers.CancelAsync(transferId, deviceId, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        });

        application.MapGet("/api/v1/outbound", (HttpRequest request) =>
        {
            if (!TryAuthenticate(request, out var deviceId))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(OutboundTransfers.GetAvailable(deviceId));
        });

        application.MapGet("/api/v1/outbound/{transferId:guid}", (
            HttpRequest request,
            Guid transferId) =>
        {
            if (!TryAuthenticate(request, out var deviceId))
            {
                return Results.Unauthorized();
            }

            var transfer = OutboundTransfers.Get(transferId, deviceId);
            return transfer is null ? Results.NotFound() : Results.Ok(transfer);
        });

        application.MapGet("/api/v1/outbound/{transferId:guid}/chunks/{chunkIndex:int}", async (
            HttpRequest request,
            Guid transferId,
            int chunkIndex,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthenticate(request, out var deviceId))
            {
                return Results.Unauthorized();
            }

            try
            {
                var chunk = await OutboundTransfers.ReadChunkAsync(
                    transferId,
                    deviceId,
                    chunkIndex,
                    cancellationToken);
                request.HttpContext.Response.Headers["X-Chunk-SHA256"] = chunk.Sha256Hex;
                return Results.Bytes(chunk.Content, "application/octet-stream");
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
            catch (ArgumentOutOfRangeException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Exception messages for file I/O contain local filesystem paths that must not reach the peer.
                return Results.Problem("The source file could not be read.");
            }
        });

        application.MapPost("/api/v1/outbound/{transferId:guid}/complete", async (
            HttpRequest request,
            Guid transferId,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthenticate(request, out var deviceId))
            {
                return Results.Unauthorized();
            }

            return await OutboundTransfers.MarkCompletedAsync(transferId, deviceId, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        });

        application.MapDelete("/api/v1/outbound/{transferId:guid}", async (
            HttpRequest request,
            Guid transferId,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthenticate(request, out var deviceId))
            {
                return Results.Unauthorized();
            }

            return await OutboundTransfers.CancelAsync(transferId, deviceId, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        });
    }

    /// <summary>Permits per minute and source address for the one-time-ticket submission endpoint.</summary>
    public const int PairingSubmitPermitLimit = TransferTuning.PairingSubmitPermitLimit;

    /// <summary>
    /// Permits per minute and source address for the pairing status poll. Must stay comfortably
    /// above the client's own poll rate (60 / <see cref="TransferTuning.PairingPollInterval"/>)
    /// or a slow human answering the desktop dialog will exhaust the budget and break pairing.
    /// </summary>
    public const int PairingPollPermitLimit = TransferTuning.PairingPollPermitLimit;

    private const string PairingSubmitRateLimitPolicy = "pairing-submit";

    private const string PairingPollRateLimitPolicy = "pairing-poll";

    private bool TryAuthenticate(HttpRequest request, out Guid deviceId)
    {
        deviceId = default;
        if (!Guid.TryParse(request.Headers["X-LocalTransfer-Device"].ToString(), out deviceId))
        {
            return false;
        }

        const string bearerPrefix = "Bearer ";
        var authorization = request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var credential = authorization[bearerPrefix.Length..].Trim();
        return TrustedDevices.ValidateCredential(deviceId, credential);
    }

    private static string FormatHost(IPAddress address) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{address}]"
            : address.ToString();

    /// <summary>
    /// Counts in-flight requests per peer address. A single phone uses a handful - one upload, one
    /// status poll - so the cap only ever trips for something misbehaving.
    /// </summary>
    private sealed class PerAddressLimiter
    {
        private const int MaxPerAddress = 12;

        private readonly ConcurrentDictionary<IPAddress, int> _counts = new();

        public bool TryAcquire(IPAddress address)
        {
            while (true)
            {
                var current = _counts.TryGetValue(address, out var existing) ? existing : 0;
                if (current >= MaxPerAddress)
                {
                    return false;
                }

                if (current == 0)
                {
                    if (_counts.TryAdd(address, 1))
                    {
                        return true;
                    }

                    continue;
                }

                if (_counts.TryUpdate(address, current + 1, current))
                {
                    return true;
                }
            }
        }

        public void Release(IPAddress address)
        {
            while (_counts.TryGetValue(address, out var current))
            {
                if (current <= 1)
                {
                    if (_counts.TryRemove(new KeyValuePair<IPAddress, int>(address, current)))
                    {
                        return;
                    }
                }
                else if (_counts.TryUpdate(address, current - 1, current))
                {
                    return;
                }
            }
        }
    }

    private IPAddress GetAdvertisedAddress()
    {
        if (_options.AdvertisedAddress is not null)
        {
            return _options.AdvertisedAddress;
        }

        return _options.ListenAddress.Equals(IPAddress.Any) ||
               _options.ListenAddress.Equals(IPAddress.IPv6Any)
            ? NetworkAddressSelector.SelectIPv4Address()
            : _options.ListenAddress;
    }
}
