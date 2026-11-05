using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LocalTransfer.Contracts.Protocol;

namespace LocalTransfer.Client;

internal static class PinnedHttpClientFactory
{
    public static HttpClient Create(
        string endpoint,
        string expectedSha256,
        TimeSpan? timeout = null)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            endpointUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The coordinator endpoint must be an absolute HTTPS URI.", nameof(endpoint));
        }

        if (expectedSha256.Length != 64 || !expectedSha256.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("A SHA-256 certificate fingerprint is required.", nameof(expectedSha256));
        }

        var expected = Convert.FromHexString(expectedSha256);

#if IOS
        // On iOS the HttpClientHandler stack bottoms out in NSUrlSessionHandler: a self-signed
        // certificate dies inside the system trust evaluation before our callback would even be
        // consulted (dotnet/maui#11225), and NSURLSession traffic is additionally subject to ATS.
        // SocketsHttpHandler is fully supported on .NET iOS, talks over plain sockets, and leaves
        // the entire trust decision to the callback below - giving iOS exactly the same
        // pin-by-fingerprint semantics Android and the desktop already have.
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    CertificateMatches(certificate, expected)
            }
        };
#else
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                CertificateMatches(certificate, expected)
        };
#endif

        return new HttpClient(handler)
        {
            BaseAddress = endpointUri,

            // Defaults to the regular per-request budget. Batch approvals pass a longer one because
            // that call stays open until a human answers, which can be minutes.
            Timeout = timeout ?? TransferTuning.HttpClientTimeout
        };
    }

    /// <summary>
    /// The only certificate that may ever be trusted is the one whose SHA-256 equals the
    /// fingerprint pinned at pairing time; every other reason to "trust" a cert is rejected.
    /// </summary>
    private static bool CertificateMatches(X509Certificate? certificate, byte[] expected)
    {
        if (certificate is null)
        {
            return false;
        }

        using var x509 = new X509Certificate2(certificate);
        var actual = SHA256.HashData(x509.RawData);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
