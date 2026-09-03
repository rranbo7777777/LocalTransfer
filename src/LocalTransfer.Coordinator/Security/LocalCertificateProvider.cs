using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LocalTransfer.Coordinator.Security;

public sealed class LocalCertificateProvider
{
    private const string CertificateSubject = "CN=LocalTransfer Coordinator";

    public X509Certificate2 GetOrCreate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, CertificateSubject, validOnly: false)
            .OfType<X509Certificate2>()
            .Where(certificate => certificate.HasPrivateKey)
            .Where(certificate => certificate.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(30))
            .OrderByDescending(certificate => certificate.NotAfter)
            .FirstOrDefault();
        if (existing is not null)
        {
            return existing;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            CertificateSubject,
            key,
            HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            critical: true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var subjectAlternativeName = new SubjectAlternativeNameBuilder();
        subjectAlternativeName.AddDnsName("localhost");
        subjectAlternativeName.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(subjectAlternativeName.Build());

        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(2));
        var persisted = new X509Certificate2(
            generated.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        store.Add(persisted);
        return persisted;
    }

    public static string GetSha256Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));
}
