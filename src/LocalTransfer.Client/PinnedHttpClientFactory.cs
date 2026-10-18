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
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is null)
                {
                    return false;
                }

                using var x509 = new X509Certificate2(certificate);
                var actual = SHA256.HashData(x509.RawData);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
        };
        return new HttpClient(handler)
        {
            BaseAddress = endpointUri,

            // Defaults to the regular per-request budget. Batch approvals pass a longer one because
            // that call stays open until a human answers, which can be minutes.
            Timeout = timeout ?? TransferTuning.HttpClientTimeout
        };
    }
}
