using System.Net.Http.Headers;
using Nexus.Contracts.Common;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Errors;
using Nexus.Security.Identity;
using Nexus.Security.Tls;
using Nexus.Security.Tokens;

namespace Nexus.Infrastructure.Security;

/// <summary>
/// Shared cryptographic request builders and TLS-pinned HTTP transport helpers used by Desktop and Mobile pairing clients.
/// </summary>
public sealed class NexusPairingProtocolClient
{
    private readonly ICryptographicIdentityProvider _identityProvider;
    private readonly ITokenGenerator _tokenGenerator;
    private readonly IJsonSerializer _serializer;
    private readonly IClock _clock;
    private readonly Func<string, string?, HttpClient>? _customHttpClientFactory;

    public NexusPairingProtocolClient(
        ICryptographicIdentityProvider identityProvider,
        ITokenGenerator tokenGenerator,
        IJsonSerializer serializer,
        IClock clock)
        : this(identityProvider, tokenGenerator, serializer, clock, customHttpClientFactory: null)
    {
    }

    public NexusPairingProtocolClient(
        ICryptographicIdentityProvider identityProvider,
        ITokenGenerator tokenGenerator,
        IJsonSerializer serializer,
        IClock clock,
        Func<string, string?, HttpClient>? customHttpClientFactory)
    {
        _identityProvider = identityProvider;
        _tokenGenerator = tokenGenerator;
        _serializer = serializer;
        _clock = clock;
        _customHttpClientFactory = customHttpClientFactory;
    }

    public HttpClient CreateHttpClient(string baseEndpoint, string? expectedTlsThumbprint, TimeSpan? timeout = null)
    {
        if (_customHttpClientFactory is not null)
        {
            return _customHttpClientFactory(baseEndpoint, expectedTlsThumbprint);
        }

        var handler = LocalTlsCertificateManager.CreatePinnedTlsHandler(expectedTlsThumbprint);
        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(baseEndpoint.Trim().TrimEnd('/') + "/", UriKind.Absolute),
            Timeout = timeout ?? TimeSpan.FromSeconds(5)
        };
    }

    public async Task<Result<CreatePairingRequest>> BuildCreatePairingRequestAsync(
        string serverEndpoint,
        string? tlsThumbprint,
        int lifetimeSeconds = 120,
        CancellationToken cancellationToken = default)
    {
        var idResult = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (idResult.IsFailure)
        {
            return Result<CreatePairingRequest>.Failure(idResult.Error!);
        }

        var id = idResult.Value;
        var timestamp = _clock.UtcNow;
        var nonce = _tokenGenerator.GenerateToken(24);
        var normalizedEndpoint = serverEndpoint.Trim().TrimEnd('/');

        var sig = await _identityProvider.SignStringAsync(
            PairingSignaturePayloads.ForCreatePairing(id.DeviceId, normalizedEndpoint, timestamp, nonce),
            cancellationToken).ConfigureAwait(false);

        if (sig.IsFailure)
        {
            return Result<CreatePairingRequest>.Failure(sig.Error!);
        }

        return Result<CreatePairingRequest>.Success(new CreatePairingRequest
        {
            HostDeviceId = id.DeviceId,
            HostDeviceName = id.CustomName,
            HostPlatform = id.Platform,
            HostDeviceType = id.DeviceType,
            HostAppVersion = id.AppVersion,
            HostPublicKeySpkiBase64 = id.PublicKeySpkiBase64,
            HostPublicKeyFingerprint = id.PublicKeyFingerprint,
            ServerEndpoint = normalizedEndpoint,
            TlsCertThumbprint = tlsThumbprint,
            LifetimeSeconds = Math.Clamp(lifetimeSeconds, 15, 600),
            TimestampUtc = timestamp,
            Nonce = nonce,
            HostSignatureBase64 = sig.Value
        });
    }

    public Result<PairingQrPayload> ParseAndVerifyQrPayload(string rawQrJson)
    {
        if (string.IsNullOrWhiteSpace(rawQrJson))
        {
            return Result<PairingQrPayload>.Failure(NexusError.Validation("QR Code vazio ou inválido.", code: "QR_EMPTY"));
        }

        PairingQrPayload? qr;
        try
        {
            qr = _serializer.Deserialize<PairingQrPayload>(rawQrJson);
        }
        catch (Exception ex)
        {
            return Result<PairingQrPayload>.Failure(new NexusError("QR_PARSE_ERROR", "Formato de QR Code NEXUS inválido.", ErrorCategory.Validation, ex.Message));
        }

        if (qr is null)
        {
            return Result<PairingQrPayload>.Failure(NexusError.Validation("Conteúdo do QR Code inválido.", code: "QR_NULL"));
        }

        var validation = ContractValidator.Validate(qr);
        if (!validation.IsValid)
        {
            return Result<PairingQrPayload>.Failure(NexusError.Validation(validation));
        }

        if (_clock.UtcNow > qr.ExpiresAtUtc)
        {
            return Result<PairingQrPayload>.Failure(NexusError.Unauthorized("Este QR Code expirou. Solicite um novo pareamento no Windows.", "PAIRING_EXPIRED"));
        }

        // Verify QR signature against server key when server issued it, otherwise direct host key
        var signingKey = !string.IsNullOrWhiteSpace(qr.ServerPublicKeySpkiBase64)
            ? qr.ServerPublicKeySpkiBase64
            : qr.HostPublicKeySpkiBase64;

        var unsigned = qr with { HostSignatureBase64 = "pending" };
        var sigVerify = _identityProvider.VerifySignature(
            signingKey,
            PairingSignaturePayloads.ForQrPayload(unsigned),
            qr.HostSignatureBase64);

        if (sigVerify.IsFailure)
        {
            return Result<PairingQrPayload>.Failure(NexusError.Unauthorized("Assinatura criptográfica do QR Code inválida.", "QR_SIGNATURE_INVALID"));
        }

        return Result<PairingQrPayload>.Success(qr);
    }

    public async Task<Result<(SubmitPairingClaimRequest Request, string VerificationCode)>> BuildClaimRequestAsync(
        PairingQrPayload qr,
        NexusPermission requestedPermissions,
        CancellationToken cancellationToken = default)
    {
        var idResult = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (idResult.IsFailure)
        {
            return Result<(SubmitPairingClaimRequest, string)>.Failure(idResult.Error!);
        }

        var id = idResult.Value;
        var timestamp = _clock.UtcNow;
        var clientNonce = _tokenGenerator.GenerateToken(24);

        var unsigned = new SubmitPairingClaimRequest
        {
            PairingId = qr.PairingId,
            OneTimeSecret = qr.OneTimeSecret,
            ChallengeNonce = qr.ChallengeNonce,
            ClientDeviceId = id.DeviceId,
            ClientDeviceName = id.CustomName,
            ClientPlatform = id.Platform,
            ClientDeviceType = id.DeviceType,
            ClientAppVersion = id.AppVersion,
            ClientOperatingSystemVersion = id.OperatingSystemVersion,
            ClientPublicKeySpkiBase64 = id.PublicKeySpkiBase64,
            ClientPublicKeyFingerprint = id.PublicKeyFingerprint,
            ClientTimestampUtc = timestamp,
            ClientNonce = clientNonce,
            ClientSignatureBase64 = "pending",
            RequestedPermissions = requestedPermissions
        };

        var sig = await _identityProvider.SignStringAsync(
            PairingSignaturePayloads.ForPairingClaim(unsigned),
            cancellationToken).ConfigureAwait(false);

        if (sig.IsFailure)
        {
            return Result<(SubmitPairingClaimRequest, string)>.Failure(sig.Error!);
        }

        var code = _identityProvider.ComputeShortVerificationCode(
            qr.HostPublicKeyFingerprint,
            id.PublicKeyFingerprint,
            qr.ChallengeNonce);

        return Result<(SubmitPairingClaimRequest, string)>.Success((unsigned with { ClientSignatureBase64 = sig.Value }, code));
    }

    public async Task<Result<ConfirmPairingRequest>> BuildConfirmRequestAsync(
        Guid pairingId,
        bool approve,
        NexusPermission grantedPermissions,
        string? rejectionReason = null,
        CancellationToken cancellationToken = default)
    {
        var idResult = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (idResult.IsFailure)
        {
            return Result<ConfirmPairingRequest>.Failure(idResult.Error!);
        }

        var id = idResult.Value;
        var timestamp = _clock.UtcNow;
        var nonce = _tokenGenerator.GenerateToken(24);

        var sig = await _identityProvider.SignStringAsync(
            PairingSignaturePayloads.ForConfirmPairing(pairingId, id.DeviceId, approve, grantedPermissions, timestamp, nonce),
            cancellationToken).ConfigureAwait(false);

        if (sig.IsFailure)
        {
            return Result<ConfirmPairingRequest>.Failure(sig.Error!);
        }

        return Result<ConfirmPairingRequest>.Success(new ConfirmPairingRequest
        {
            PairingId = pairingId,
            Approve = approve,
            GrantedPermissions = grantedPermissions,
            RejectionReason = rejectionReason,
            HostDeviceId = id.DeviceId,
            TimestampUtc = timestamp,
            Nonce = nonce,
            HostSignatureBase64 = sig.Value
        });
    }

    public async Task<Result<FinalizePairingTokenRequest>> BuildFinalizeRequestAsync(
        Guid pairingId,
        CancellationToken cancellationToken = default)
    {
        var idResult = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (idResult.IsFailure)
        {
            return Result<FinalizePairingTokenRequest>.Failure(idResult.Error!);
        }

        var id = idResult.Value;
        var timestamp = _clock.UtcNow;
        var nonce = _tokenGenerator.GenerateToken(24);

        var sig = await _identityProvider.SignStringAsync(
            PairingSignaturePayloads.ForFinalizeTokens(pairingId, id.DeviceId, timestamp, nonce),
            cancellationToken).ConfigureAwait(false);

        if (sig.IsFailure)
        {
            return Result<FinalizePairingTokenRequest>.Failure(sig.Error!);
        }

        return Result<FinalizePairingTokenRequest>.Success(new FinalizePairingTokenRequest
        {
            PairingId = pairingId,
            ClientDeviceId = id.DeviceId,
            TimestampUtc = timestamp,
            Nonce = nonce,
            ClientSignatureBase64 = sig.Value
        });
    }

    public async Task<Result<RefreshTokenRequest>> BuildRefreshRequestAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var idResult = await _identityProvider.GetOrCreateIdentityAsync(cancellationToken).ConfigureAwait(false);
        if (idResult.IsFailure)
        {
            return Result<RefreshTokenRequest>.Failure(idResult.Error!);
        }

        var id = idResult.Value;
        var timestamp = _clock.UtcNow;
        var nonce = _tokenGenerator.GenerateToken(24);

        var sig = await _identityProvider.SignStringAsync(
            PairingSignaturePayloads.ForRefreshToken(id.DeviceId, refreshToken, timestamp, nonce),
            cancellationToken).ConfigureAwait(false);

        if (sig.IsFailure)
        {
            return Result<RefreshTokenRequest>.Failure(sig.Error!);
        }

        return Result<RefreshTokenRequest>.Success(new RefreshTokenRequest
        {
            DeviceId = id.DeviceId,
            RefreshToken = refreshToken,
            TimestampUtc = timestamp,
            Nonce = nonce,
            SignatureBase64 = sig.Value
        });
    }

    public async Task<Result<TPayload>> SendAsync<TPayload>(
        HttpClient client,
        HttpMethod method,
        string relativePath,
        object? body = null,
        string? bearerToken = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(method, relativePath);
            if (!string.IsNullOrWhiteSpace(bearerToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
            }

            if (body is not null)
            {
                var json = _serializer.Serialize(body);
                request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            }

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var rawBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(rawBody))
            {
                NexusResponse<TPayload>? envelope = null;
                try
                {
                    envelope = _serializer.Deserialize<NexusResponse<TPayload>>(rawBody);
                }
                catch
                {
                    // Fall through to status code handling when response body is non-envelope text/problem
                }

                if (envelope is not null)
                {
                    if (envelope.IsSuccess && response.IsSuccessStatusCode && envelope.Payload is not null)
                    {
                        return Result<TPayload>.Success(envelope.Payload);
                    }

                    if (envelope.Error is not null)
                    {
                        var category = (int)response.StatusCode switch
                        {
                            400 => ErrorCategory.Validation,
                            401 => ErrorCategory.Unauthorized,
                            403 => ErrorCategory.Forbidden,
                            404 => ErrorCategory.NotFound,
                            409 => ErrorCategory.Conflict,
                            _ => ErrorCategory.Unexpected
                        };

                        return Result<TPayload>.Failure(new NexusError(
                            envelope.Error.Code,
                            envelope.Error.Message,
                            category,
                            envelope.Error.Detail));
                    }
                }
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                return Result<TPayload>.Failure(NexusError.Unauthorized("Autenticação inválida ou token expirado/revogado.", "HTTP_401"));
            }

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return Result<TPayload>.Failure(NexusError.Forbidden("Permissão insuficiente para esta operação.", "HTTP_403"));
            }

            return Result<TPayload>.Failure(NexusError.Unexpected(
                $"Falha HTTP {(int)response.StatusCode} em {relativePath}.",
                $"HTTP_{(int)response.StatusCode}"));
        }
        catch (OperationCanceledException)
        {
            return Result<TPayload>.Failure(NexusError.Cancelled());
        }
        catch (Exception ex)
        {
            return Result<TPayload>.Failure(NexusError.Network(
                $"Não foi possível comunicar com o servidor NEXUS: {ex.Message}",
                "SERVER_UNREACHABLE",
                ex.Message));
        }
    }

    public static string FormatFingerprint(string? fingerprint)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return "-";
        }

        var normalized = fingerprint.Trim().ToUpperInvariant();
        if (normalized.Length < 16)
        {
            return normalized;
        }

        var preview = normalized[..Math.Min(32, normalized.Length)];
        var chunks = Enumerable.Range(0, preview.Length / 4)
            .Select(i => preview.Substring(i * 4, 4));
        return string.Join("-", chunks);
    }

    public static string GetPairingStateLabel(PairingSessionState state) => state switch
    {
        PairingSessionState.Idle => "Pronto para parear",
        PairingSessionState.AwaitingScan => "Aguardando leitura",
        PairingSessionState.Validating => "Validando",
        PairingSessionState.AwaitingConfirmation => "Aguardando confirmação",
        PairingSessionState.Connected => "Conectado",
        PairingSessionState.Failed => "Falha",
        PairingSessionState.Expired => "Expirado",
        PairingSessionState.Rejected => "Rejeitado",
        _ => state.ToString()
    };
}
