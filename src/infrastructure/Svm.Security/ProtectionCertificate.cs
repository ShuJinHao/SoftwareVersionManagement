using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Svm.Services.Contracts.Framework;

namespace Svm.Security;

public static class ProtectionCertificate
{
    public static X509Certificate2 Load(string path, string password)
    {
        try
        {
            var certificate = new X509Certificate2(path, password,
                OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet);
            using var key = certificate.GetRSAPrivateKey();
            if (!certificate.HasPrivateKey || key is null || key.KeySize < 2048 || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            { certificate.Dispose(); throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
            return certificate;
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException or NotSupportedException)
        { throw new RequestRejectedException(RequestFailure.ConfigurationInvalid); }
    }
}
