using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LocalTransfer.Client;

internal static class PinnedHttpClientFactory
{
    public static HttpClient Create(string endpoint, string expectedSha256)
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
            Timeout = TimeSpan.FromMinutes(2)
        };
    }
}
