using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TwoWChat.Core;

public static class ServerIdentity
{
    public static X509Certificate2 Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=2WChat room", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        var eku = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        // Schannel requires a user key container; an EphemeralKeySet PFX is not
        // usable by SslStream server authentication on all Windows versions.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
    }

    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    public static string FormatFingerprint(string value) => string.Join(":", Enumerable.Range(0, value.Length / 2).Select(i => value.Substring(i * 2, 2)));
}
