using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using LocalTransfer.Contracts.Protocol;
using LocalTransfer.Contracts.Pairing;
using LocalTransfer.Contracts.Transfers;
using LocalTransfer.Coordinator.Devices;
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

    public CoordinatorHost(
        CoordinatorOptions? options = null,
        Func<X509Certificate2>? certificateFactory = null)
    {
        _options = options ?? new CoordinatorOptions();
        _certificateFactory = certificateFactory ?? (() => new LocalCertificateProvider().GetOrCreate());
        Directory.CreateDirectory(_options.DataDirectory);
        Directory.CreateDirectory(_options.ReceiveDirectory);
        TrustedDevices = new TrustedDeviceStore(_options.DataDirectory);
        Pairing = new PairingCoordinator(TrustedDevices);
        InboundTransfers = new InboundTransferCoordinator(_options.ReceiveDirectory);
        OutboundTransfers = new OutboundTransferCoordinator();
    }

    public PairingCoordinator Pairing { get; }

    public TrustedDeviceStore TrustedDevices { get; }

    public InboundTransferCoordinator InboundTransfers { get; }

    public OutboundTransferCoordinator OutboundTransfers { get; }

    public bool IsRunning => _application is not null;

    public string Endpoint => $"https://{FormatHost(GetAdvertisedAddress())}:{_options.Port}";

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
                options.AddPolicy(PairingRateLimitPolicy, context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 30,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0
                        }));
            });

            var application = builder.Build();
            application.UseRateLimiter();
            MapEndpoints(application);

            try
            {
                await application.StartAsync(cancellationToken);
                _application = application;
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

        PairingTicket ticket = Pairing.CreateTicket(lifetime ?? TimeSpan.FromMinutes(2));
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

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await InboundTransfers.DisposeAsync();
        OutboundTransfers.Dispose();
    }

    private void ConfigureKestrel(KestrelServerOptions serverOptions, X509Certificate2 certificate)
    {
        serverOptions.AddServerHeader = false;
        serverOptions.Limits.MaxRequestBodySize = ProtocolConstants.DefaultChunkSize + (64 * 1024);
        serverOptions.Listen(_options.ListenAddress, _options.Port, listenOptions =>
            listenOptions.UseHttps(certificate));
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
        }).RequireRateLimiting(PairingRateLimitPolicy);

        application.MapGet("/api/v1/pairing/{requestId:guid}", (Guid requestId) =>
        {
            var response = Pairing.Poll(requestId);
            return response is null ? Results.NotFound() : Results.Ok(response);
        }).RequireRateLimiting(PairingRateLimitPolicy);

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

    private const string PairingRateLimitPolicy = "pairing";

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
