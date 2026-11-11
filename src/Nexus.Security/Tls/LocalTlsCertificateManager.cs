using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nexus.Core.Errors;
using Nexus.Security.Credentials;
using Nexus.Security.Tokens;

namespace Nexus.Security.Tls;

public interface ITlsCertificateProvider
{
    Task<Result<X509Certificate2>> GetOrCreateServerCertificateAsync(string hostName, CancellationToken cancellationToken = default);
    string GetSha256Thumbprint(X509Certificate2 certificate);
}

/// <summary>
/// Manages local TLS X.509 certificates stored encrypted in ISecureCredentialStore and supports TLS thumbprint pinning.
/// </summary>
public sealed class LocalTlsCertificateManager : ITlsCertificateProvider
{
    private const string CertVaultKey = "nexus.server.tls.pfx.v1";
    private const string CertPassVaultKey = "nexus.server.tls.pass.v1";

    private readonly ISecureCredentialStore _credentialStore;
    private readonly ITokenGenerator _tokenGenerator;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LocalTlsCertificateManager(
        ISecureCredentialStore credentialStore,
        ITokenGenerator tokenGenerator)
    {
        _credentialStore = credentialStore;
        _tokenGenerator = tokenGenerator;
    }

    public async Task<Result<X509Certificate2>> GetOrCreateServerCertificateAsync(
        string hostName,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existingPfx = await _credentialStore.GetSecretAsync(CertVaultKey, cancellationToken).ConfigureAwait(false);
            var existingPass = await _credentialStore.GetStringAsync(CertPassVaultKey, cancellationToken).ConfigureAwait(false);

            if (existingPfx.IsSuccess && existingPfx.Value is not null &&
                existingPass.IsSuccess && !string.IsNullOrEmpty(existingPass.Value))
            {
                var loaded = X509CertificateLoader.LoadPkcs12(
                    existingPfx.Value,
                    existingPass.Value,
                    X509KeyStorageFlags.Exportable);

                if (loaded.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(7))
                {
                    return Result<X509Certificate2>.Success(loaded);
                }

                loaded.Dispose();
            }

            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest(
                $"CN=NEXUS Local Server ({hostName})",
                ecdsa,
                HashAlgorithmName.SHA256);

            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.1")], // Server Authentication
                critical: true));

            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            san.AddIpAddress(IPAddress.IPv6Loopback);
            if (!string.IsNullOrWhiteSpace(hostName))
            {
                san.AddDnsName(hostName.Trim());
            }
            req.CertificateExtensions.Add(san.Build());

            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var notAfter = notBefore.AddYears(2);
            using var ephemeralCert = req.CreateSelfSigned(notBefore, notAfter);

            var password = _tokenGenerator.GenerateToken(24);
            var pfxBytes = ephemeralCert.Export(X509ContentType.Pfx, password);

            var savePass = await _credentialStore.SaveStringAsync(CertPassVaultKey, password, cancellationToken).ConfigureAwait(false);
            if (savePass.IsFailure)
            {
                return Result<X509Certificate2>.Failure(savePass.Error!);
            }

            var savePfx = await _credentialStore.SaveSecretAsync(CertVaultKey, pfxBytes, cancellationToken).ConfigureAwait(false);
            if (savePfx.IsFailure)
            {
                return Result<X509Certificate2>.Failure(savePfx.Error!);
            }

            var persisted = X509CertificateLoader.LoadPkcs12(pfxBytes, password, X509KeyStorageFlags.Exportable);
            return Result<X509Certificate2>.Success(persisted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Result<X509Certificate2>.Failure(NexusError.Storage("Failed to initialize local TLS certificate.", "TLS_CERT_ERROR", ex.Message, ex));
        }
        finally
        {
            _gate.Release();
        }
    }

    public string GetSha256Thumbprint(X509Certificate2 certificate)
        => Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));

    public static HttpClientHandler CreatePinnedTlsHandler(string? expectedSha256Thumbprint)
    {
        var handler = new HttpClientHandler
        {
            SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        };

        if (!string.IsNullOrWhiteSpace(expectedSha256Thumbprint))
        {
            var normalizedExpected = expectedSha256Thumbprint.Trim().ToLowerInvariant();
            handler.ServerCertificateCustomValidationCallback = (_, cert, _, sslErrors) =>
            {
                if (cert is null)
                {
                    return false;
                }

                if (sslErrors == SslPolicyErrors.None)
                {
                    return true;
                }

                var actual = cert is X509Certificate2 cert2
                    ? Convert.ToHexStringLower(cert2.GetCertHash(HashAlgorithmName.SHA256))
                    : Convert.ToHexStringLower(SHA256.HashData(cert.GetRawCertData()));
                return CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(actual),
                    System.Text.Encoding.ASCII.GetBytes(normalizedExpected));
            };
        }

        return handler;
    }
}
