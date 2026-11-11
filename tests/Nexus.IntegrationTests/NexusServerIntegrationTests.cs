using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Infrastructure.Security;
using Nexus.Security.Credentials;
using Nexus.Security.Identity;
using Nexus.Security.Tokens;
using Nexus.Server.Endpoints;
using Xunit;

namespace Nexus.IntegrationTests;

public sealed class NexusServerIntegrationTests : IDisposable
{
    private readonly string _serverStorageDir = Path.Combine(Path.GetTempPath(), "nexus-server-int-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new(DateTimeOffset.UtcNow);
    private readonly CustomWebFactory _factory;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public NexusServerIntegrationTests()
    {
        _factory = new CustomWebFactory(_serverStorageDir, _clock);
    }

    [Fact]
    public async Task HealthAndSystemEndpoints_ReturnSuccessAndCryptoMetadata()
    {
        using var client = _factory.CreateClient();

        var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var readyBody = await ready.Content.ReadAsStringAsync();
        Assert.Contains("sqlite_storage", readyBody, StringComparison.Ordinal);
        Assert.Contains("nexus-server-test.db", readyBody, StringComparison.Ordinal);

        var sysHealth = await client.GetAsync("/api/v1/system/health");
        Assert.Equal(HttpStatusCode.OK, sysHealth.StatusCode);

        var correlationId = Guid.CreateVersion7();
        using var infoRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
        infoRequest.Headers.Add("X-Correlation-Id", correlationId.ToString("D"));
        var infoResponse = await client.SendAsync(infoRequest);
        Assert.Equal(HttpStatusCode.OK, infoResponse.StatusCode);
        Assert.True(infoResponse.Headers.TryGetValues("X-Correlation-Id", out var echoed));
        Assert.Equal(correlationId.ToString("D"), echoed.Single());

        var systemInfo = await infoResponse.Content.ReadFromJsonAsync<NexusResponse<SystemInfoDto>>(JsonOptions);
        Assert.NotNull(systemInfo);
        Assert.True(systemInfo.IsSuccess);
        Assert.Equal(ContractVersions.Current, systemInfo.ContractVersion);
        Assert.Equal("NEXUS.Server", systemInfo.Payload?.ServiceName);
        Assert.False(string.IsNullOrWhiteSpace(systemInfo.Payload?.TlsThumbprint));
        Assert.Equal(64, systemInfo.Payload?.HostKeyFingerprint?.Length);
    }

    [Fact]
    public async Task DevicesEndpoint_RequiresAuth_RegistersValidDevice_AndRejectsInvalidContract()
    {
        using var client = _factory.CreateClient();

        var unauth = await client.GetAsync("/api/v1/devices");
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

        var host = CreateCryptoProvider("win-dev", "Windows Host", DevicePlatform.Windows, DeviceType.Desktop);
        var mobile = CreateCryptoProvider("mob-dev", "Pixel 9 Test", DevicePlatform.Android, DeviceType.Phone);
        var pairing = await PairDeviceAsync(client, host, mobile, NexusPermission.All);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Tokens.AccessToken);

        var invalid = new DeviceRegistrationContract
        {
            DeviceId = Guid.Empty,
            Name = "A",
            Platform = DevicePlatform.Unknown,
            OperatingSystemVersion = "",
            AppVersion = ""
        };
        var badResponse = await client.PostAsJsonAsync("/api/v1/devices", invalid, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);

        var valid = new DeviceRegistrationContract
        {
            DeviceId = Guid.CreateVersion7(),
            Name = "Android Pixel Test",
            Platform = DevicePlatform.Android,
            Type = DeviceType.Phone,
            OperatingSystemVersion = "Android 15",
            AppVersion = "0.3.0",
            Capabilities = DeviceCapability.Notifications | DeviceCapability.Telemetry
        };
        var okResponse = await client.PostAsJsonAsync("/api/v1/devices", valid, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, okResponse.StatusCode);

        var listPayload = await client.GetFromJsonAsync<NexusResponse<IReadOnlyList<DeviceInfo>>>("/api/v1/devices", JsonOptions);
        Assert.NotNull(listPayload);
        Assert.True(listPayload.IsSuccess);
        Assert.Contains(listPayload.Payload!, d => d.Id == valid.DeviceId && d.Name == "Android Pixel Test");

        var connState = await client.GetFromJsonAsync<NexusResponse<ConnectionStateSnapshot>>("/api/v1/connections/state", JsonOptions);
        Assert.NotNull(connState);
        Assert.True(connState.IsSuccess);
    }

    [Fact]
    public async Task SignalRHub_RejectsAnonymous_ConnectsWithBearerToken_AndAnnouncesPresence()
    {
        using var client = _factory.CreateClient();

        await using var anonymousConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/nexus"), opts =>
            {
                opts.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => anonymousConnection.StartAsync());

        var host = CreateCryptoProvider("win-hub", "Windows Hub", DevicePlatform.Windows, DeviceType.Desktop);
        var mobile = CreateCryptoProvider("mob-hub", "Android Hub", DevicePlatform.Android, DeviceType.Phone);
        var pairing = await PairDeviceAsync(client, host, mobile, NexusPermission.All);

        var pongTcs = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var authedConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/nexus"), opts =>
            {
                opts.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                opts.AccessTokenProvider = () => Task.FromResult<string?>(pairing.Tokens.AccessToken);
            })
            .Build();

        authedConnection.On<DateTimeOffset>("ReceivePong", serverTime => pongTcs.TrySetResult(serverTime));
        await authedConnection.StartAsync();

        await authedConnection.InvokeAsync("AnnouncePresenceAsync", pairing.ClientIdentity.DeviceId, pairing.ClientIdentity.CustomName);
        var pong = await pongTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pong <= DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task Pairing_FullQrWorkflow_EstablishesMutualIdentity_AndSupportsTrustedDeviceManagement()
    {
        using var client = _factory.CreateClient();
        var host = CreateCryptoProvider("win-e2e", "Windows Principal", DevicePlatform.Windows, DeviceType.Desktop);
        var mobile = CreateCryptoProvider("mob-e2e", "Galaxy S25", DevicePlatform.Android, DeviceType.Phone);

        var pairing = await PairDeviceAsync(client, host, mobile, NexusPermission.All);
        Assert.Equal(PairingSessionState.Connected, pairing.FinalSnapshot.State);
        Assert.Equal(pairing.ClientVerificationCode, pairing.FinalSnapshot.VerificationCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Tokens.AccessToken);

        // List trusted devices and check identity + last access
        var list = await client.GetFromJsonAsync<NexusResponse<IReadOnlyList<TrustedDeviceRecord>>>("/api/v1/trusted-devices", JsonOptions);
        Assert.NotNull(list);
        Assert.True(list.IsSuccess);
        var found = Assert.Single(list.Payload!, d => d.DeviceId == pairing.ClientIdentity.DeviceId);
        Assert.Equal(pairing.ClientIdentity.PublicKeyFingerprint, found.PublicKeyFingerprint);
        Assert.Equal(DeviceTrustStatus.Authorized, found.TrustStatus);

        // Advance clock and inspect device to verify LastAccessAtUtc updates on authenticated API calls
        _clock.Advance(TimeSpan.FromMinutes(2));
        var beforeRename = await client.GetFromJsonAsync<NexusResponse<TrustedDeviceRecord>>($"/api/v1/trusted-devices/{found.DeviceId:D}", JsonOptions);
        Assert.True(beforeRename!.IsSuccess);
        Assert.True(beforeRename.Payload!.LastAccessAtUtc > found.LastAccessAtUtc);

        // Rename trusted device
        using var renameReq = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/trusted-devices/{found.DeviceId:D}/name")
        {
            Content = JsonContent.Create(new RenameDeviceRequest { CustomName = "Celular Trabalho" }, options: JsonOptions)
        };
        var renameResp = await client.SendAsync(renameReq);
        Assert.Equal(HttpStatusCode.OK, renameResp.StatusCode);
        var renamed = await renameResp.Content.ReadFromJsonAsync<NexusResponse<TrustedDeviceRecord>>(JsonOptions);
        Assert.Equal("Celular Trabalho", renamed!.Payload!.CustomName);

        // Refresh tokens cleanly rotates refresh token
        var mobileProtocol = new NexusPairingProtocolClient(mobile, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);
        var refreshBuild = await mobileProtocol.BuildRefreshRequestAsync(pairing.Tokens.RefreshToken);
        Assert.True(refreshBuild.IsSuccess);
        var refreshResp = await client.PostAsJsonAsync("/api/v1/auth/refresh", refreshBuild.Value, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, refreshResp.StatusCode);
        var refreshedPair = (await refreshResp.Content.ReadFromJsonAsync<NexusResponse<DeviceAuthTokenPair>>(JsonOptions))!.Payload!;
        Assert.NotEqual(pairing.Tokens.RefreshToken, refreshedPair.RefreshToken);

        // Delete trusted device
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshedPair.AccessToken);
        var deleteResp = await client.DeleteAsync($"/api/v1/trusted-devices/{found.DeviceId:D}");
        Assert.Equal(HttpStatusCode.OK, deleteResp.StatusCode);

        // After deletion, previously valid token is immediately rejected
        var afterDelete = await client.GetAsync("/api/v1/trusted-devices");
        Assert.Equal(HttpStatusCode.Unauthorized, afterDelete.StatusCode);
    }

    [Fact]
    public async Task Pairing_ExpiredQrCode_IsRejectedAndMarkedExpired()
    {
        using var client = _factory.CreateClient();
        var host = CreateCryptoProvider("win-exp", "Windows Exp", DevicePlatform.Windows, DeviceType.Desktop);
        var mobile = CreateCryptoProvider("mob-exp", "Android Exp", DevicePlatform.Android, DeviceType.Phone);

        var hostProtocol = new NexusPairingProtocolClient(host, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);
        var mobileProtocol = new NexusPairingProtocolClient(mobile, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);

        var createReq = (await hostProtocol.BuildCreatePairingRequestAsync("http://localhost", null, lifetimeSeconds: 60)).Value;
        var createResp = await client.PostAsJsonAsync("/api/v1/pairing/sessions", createReq, JsonOptions);
        var snapshot = (await createResp.Content.ReadFromJsonAsync<NexusResponse<PairingSessionSnapshot>>(JsonOptions))!.Payload!;

        // Mobile validates QR before advancing server clock so we test server-side QR expiration enforcement too
        var qr = mobileProtocol.ParseAndVerifyQrPayload(snapshot.QrPayloadJson!).Value;
        _clock.Advance(TimeSpan.FromSeconds(65));

        var claim = (await mobileProtocol.BuildClaimRequestAsync(qr, NexusPermission.ViewStatus)).Value;
        var claimResp = await client.PostAsJsonAsync("/api/v1/pairing/claim", claim.Request, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, claimResp.StatusCode);
        var err = await claimResp.Content.ReadFromJsonAsync<NexusResponse<PairingSessionSnapshot>>(JsonOptions);
        Assert.Equal("PAIRING_EXPIRED", err!.Error!.Code);

        // Client-side parser also blocks scanning expired QR
        Assert.Equal("PAIRING_EXPIRED", mobileProtocol.ParseAndVerifyQrPayload(snapshot.QrPayloadJson!).Error!.Code);
    }

    [Fact]
    public async Task Pairing_UnauthorizedAttempts_AreRejected()
    {
        using var client = _factory.CreateClient();
        var host = CreateCryptoProvider("win-unauth", "Windows Host", DevicePlatform.Windows, DeviceType.Desktop);
        var mobile = CreateCryptoProvider("mob-unauth", "Android Mobile", DevicePlatform.Android, DeviceType.Phone);
        var rogue = CreateCryptoProvider("rogue-dev", "Rogue Attacker", DevicePlatform.Linux, DeviceType.Desktop);

        var hostProtocol = new NexusPairingProtocolClient(host, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);
        var mobileProtocol = new NexusPairingProtocolClient(mobile, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);
        var rogueProtocol = new NexusPairingProtocolClient(rogue, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);

        // 1. Forged signature on CreatePairing
        var forgedCreate = (await hostProtocol.BuildCreatePairingRequestAsync("http://localhost", null, 120)).Value with
        {
            HostSignatureBase64 = Convert.ToBase64String(new byte[64])
        };
        var forgedCreateResp = await client.PostAsJsonAsync("/api/v1/pairing/sessions", forgedCreate, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, forgedCreateResp.StatusCode);

        // Valid session creation
        var validCreate = (await hostProtocol.BuildCreatePairingRequestAsync("http://localhost", null, 120)).Value;
        var snap = (await (await client.PostAsJsonAsync("/api/v1/pairing/sessions", validCreate, JsonOptions))
            .Content.ReadFromJsonAsync<NexusResponse<PairingSessionSnapshot>>(JsonOptions))!.Payload!;
        var qr = mobileProtocol.ParseAndVerifyQrPayload(snap.QrPayloadJson!).Value;

        // 2. Wrong OneTimeSecret on claim
        var badSecretClaim = (await mobileProtocol.BuildClaimRequestAsync(qr with { OneTimeSecret = "wrong-secret" }, NexusPermission.ViewStatus)).Value;
        var badSecretResp = await client.PostAsJsonAsync("/api/v1/pairing/claim", badSecretClaim.Request, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, badSecretResp.StatusCode);

        // 3. Rogue host trying to confirm before real host
        var validClaim = (await mobileProtocol.BuildClaimRequestAsync(qr, NexusPermission.ViewStatus)).Value;
        var claimOk = await client.PostAsJsonAsync("/api/v1/pairing/claim", validClaim.Request, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, claimOk.StatusCode);

        var rogueConfirm = (await rogueProtocol.BuildConfirmRequestAsync(qr.PairingId, true, NexusPermission.All)).Value;
        var rogueConfirmResp = await client.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/confirm", rogueConfirm, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, rogueConfirmResp.StatusCode);

        // 4. Real host explicitly rejects pairing -> finalize must be denied
        var hostReject = (await hostProtocol.BuildConfirmRequestAsync(qr.PairingId, approve: false, NexusPermission.None, "Não reconheço")).Value;
        var rejectResp = await client.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/confirm", hostReject, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, rejectResp.StatusCode);
        var rejectedSnap = (await rejectResp.Content.ReadFromJsonAsync<NexusResponse<PairingSessionSnapshot>>(JsonOptions))!.Payload!;
        Assert.Equal(PairingSessionState.Rejected, rejectedSnap.State);

        var finalizeAfterReject = (await mobileProtocol.BuildFinalizeRequestAsync(qr.PairingId)).Value;
        var finalizeResp = await client.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/finalize", finalizeAfterReject, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, finalizeResp.StatusCode);

        // 5. Device paired ONLY with ViewStatus cannot manage/revoke devices (403 Forbidden)
        var mobileLimited = CreateCryptoProvider("mob-ltd", "Limited Phone", DevicePlatform.Android, DeviceType.Phone);
        var limitedPairing = await PairDeviceAsync(client, host, mobileLimited, NexusPermission.ViewStatus);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", limitedPairing.Tokens.AccessToken);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/trusted-devices")).StatusCode);
        var forbiddenRevoke = await client.PostAsJsonAsync($"/api/v1/trusted-devices/{limitedPairing.ClientIdentity.DeviceId:D}/revoke", new RevokeDeviceRequest(), JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRevoke.StatusCode);
    }

    [Fact]
    public async Task Auth_InvalidExpiredAndTamperedTokens_AreRejected()
    {
        using var client = _factory.CreateClient();
        var host = CreateCryptoProvider("win-tok", "Windows Tok", DevicePlatform.Windows, DeviceType.Desktop);
        var mobile = CreateCryptoProvider("mob-tok", "Android Tok", DevicePlatform.Android, DeviceType.Phone);
        var pairing = await PairDeviceAsync(client, host, mobile, NexusPermission.All);

        // Malformed / garbage token
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-valid-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/devices")).StatusCode);

        // Tampered token signature
        var tampered = pairing.Tokens.AccessToken[..^4] + "ZZZZ";
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tampered);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/devices")).StatusCode);

        // Valid token expires after lifetime
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/devices")).StatusCode);

        _clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/devices")).StatusCode);
    }

    [Fact]
    public async Task Trust_RevokedDevice_IsBlockedEverywhere_AndNotifiedOverSignalR()
    {
        using var client = _factory.CreateClient();
        var host = CreateCryptoProvider("win-rev", "Windows Revoker", DevicePlatform.Windows, DeviceType.Desktop);
        var targetMobile = CreateCryptoProvider("mob-target", "Lost Android Phone", DevicePlatform.Android, DeviceType.Phone);
        var adminMobile = CreateCryptoProvider("mob-admin", "Admin Tablet", DevicePlatform.Android, DeviceType.Tablet);

        var targetPairing = await PairDeviceAsync(client, host, targetMobile, NexusPermission.ViewStatus);
        var adminPairing = await PairDeviceAsync(client, host, adminMobile, NexusPermission.All);

        var revocationNoticeTcs = new TaskCompletionSource<(Guid DeviceId, string Reason)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var targetHub = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "/hubs/nexus"), opts =>
            {
                opts.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                opts.AccessTokenProvider = () => Task.FromResult<string?>(targetPairing.Tokens.AccessToken);
            })
            .Build();

        targetHub.On<Guid, string>("ReceiveRevocationNotice", (id, reason) => revocationNoticeTcs.TrySetResult((id, reason)));
        await targetHub.StartAsync();
        await targetHub.InvokeAsync("AnnouncePresenceAsync", targetPairing.ClientIdentity.DeviceId, targetPairing.ClientIdentity.CustomName);

        // Revoke target device using admin credentials
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminPairing.Tokens.AccessToken);
        var revokeResp = await client.PostAsJsonAsync(
            $"/api/v1/trusted-devices/{targetPairing.ClientIdentity.DeviceId:D}/revoke",
            new RevokeDeviceRequest { Reason = "Dispositivo perdido" },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, revokeResp.StatusCode);

        var notice = await revocationNoticeTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(targetPairing.ClientIdentity.DeviceId, notice.DeviceId);

        // Revoked access token immediately fails
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", targetPairing.Tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/devices")).StatusCode);

        // Refreshing tokens for revoked device also fails
        var targetProtocol = new NexusPairingProtocolClient(targetMobile, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);
        var refreshReq = (await targetProtocol.BuildRefreshRequestAsync(targetPairing.Tokens.RefreshToken)).Value;
        var refreshResp = await client.PostAsJsonAsync("/api/v1/auth/refresh", refreshReq, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshResp.StatusCode);
    }

    [Fact]
    public async Task Security_CredentialReuseAndReplayAttacks_AreBlocked()
    {
        using var client = _factory.CreateClient();
        var host = CreateCryptoProvider("win-rep", "Windows Replay", DevicePlatform.Windows, DeviceType.Desktop);
        var mobile = CreateCryptoProvider("mob-rep", "Android Replay", DevicePlatform.Android, DeviceType.Phone);

        var hostProtocol = new NexusPairingProtocolClient(host, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);
        var mobileProtocol = new NexusPairingProtocolClient(mobile, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);

        var createReq = (await hostProtocol.BuildCreatePairingRequestAsync("http://localhost", null, 120)).Value;

        // 1. Replaying CreatePairingRequest with identical nonce fails
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/pairing/sessions", createReq, JsonOptions)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/pairing/sessions", createReq, JsonOptions)).StatusCode);

        // Fresh pairing session
        var createReq2 = (await hostProtocol.BuildCreatePairingRequestAsync("http://localhost", null, 120)).Value;
        var snap = (await (await client.PostAsJsonAsync("/api/v1/pairing/sessions", createReq2, JsonOptions))
            .Content.ReadFromJsonAsync<NexusResponse<PairingSessionSnapshot>>(JsonOptions))!.Payload!;
        var qr = mobileProtocol.ParseAndVerifyQrPayload(snap.QrPayloadJson!).Value;

        // 2. Reusing QR OneTimeSecret for a second device claim fails
        var claim1 = (await mobileProtocol.BuildClaimRequestAsync(qr, NexusPermission.All)).Value;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/v1/pairing/claim", claim1.Request, JsonOptions)).StatusCode);

        var attacker = CreateCryptoProvider("mob-att", "Attacker", DevicePlatform.Android, DeviceType.Phone);
        var attackerProtocol = new NexusPairingProtocolClient(attacker, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);
        var reuseQrClaim = (await attackerProtocol.BuildClaimRequestAsync(qr, NexusPermission.All)).Value;
        var reuseResp = await client.PostAsJsonAsync("/api/v1/pairing/claim", reuseQrClaim.Request, JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, reuseResp.StatusCode);

        // Approve and finalize
        var confirmReq = (await hostProtocol.BuildConfirmRequestAsync(qr.PairingId, true, NexusPermission.All)).Value;
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/confirm", confirmReq, JsonOptions)).StatusCode);

        var finalize1 = (await mobileProtocol.BuildFinalizeRequestAsync(qr.PairingId)).Value;
        var finalizeResp1 = await client.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/finalize", finalize1, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, finalizeResp1.StatusCode);
        var tokens = (await finalizeResp1.Content.ReadFromJsonAsync<NexusResponse<DeviceAuthTokenPair>>(JsonOptions))!.Payload!;

        // 3. Reusing finalize pickup endpoint fails (credentials already retrieved)
        var finalize2 = (await mobileProtocol.BuildFinalizeRequestAsync(qr.PairingId)).Value;
        var finalizeResp2 = await client.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/finalize", finalize2, JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, finalizeResp2.StatusCode);

        // 4. Refresh token rotation: reusing old refresh token triggers reuse detection & revokes active tokens + trust
        var refresh1 = (await mobileProtocol.BuildRefreshRequestAsync(tokens.RefreshToken)).Value;
        var refreshResp1 = await client.PostAsJsonAsync("/api/v1/auth/refresh", refresh1, JsonOptions);
        Assert.Equal(HttpStatusCode.OK, refreshResp1.StatusCode);
        var rotatedPair = (await refreshResp1.Content.ReadFromJsonAsync<NexusResponse<DeviceAuthTokenPair>>(JsonOptions))!.Payload!;

        var refreshReuse = (await mobileProtocol.BuildRefreshRequestAsync(tokens.RefreshToken)).Value;
        var reuseRefreshResp = await client.PostAsJsonAsync("/api/v1/auth/refresh", refreshReuse, JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, reuseRefreshResp.StatusCode);

        // Even the newly rotated refresh token AND existing access token are now revoked due to detected credential reuse
        var rotatedRefreshAfterBreach = (await mobileProtocol.BuildRefreshRequestAsync(rotatedPair.RefreshToken)).Value;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/auth/refresh", rotatedRefreshAfterBreach, JsonOptions)).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/devices")).StatusCode);
    }

    [Fact]
    public async Task Auth_SignedHttpRequestReplay_And_UnauthenticatedProtectedEndpoints_AreRejected()
    {
        using var client = _factory.CreateClient();

        // Unauthenticated checks across all protected endpoints
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/connections/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/trusted-devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync($"/api/v1/trusted-devices/{Guid.CreateVersion7():D}/revoke", new RevokeDeviceRequest(), JsonOptions)).StatusCode);

        // Signed request anti-replay verification
        var host = CreateCryptoProvider("win-sig", "Windows Sig", DevicePlatform.Windows, DeviceType.Desktop);
        var mobile = CreateCryptoProvider("mob-sig", "Android Sig", DevicePlatform.Android, DeviceType.Phone);
        var pairing = await PairDeviceAsync(client, host, mobile, NexusPermission.All);

        var ts = _clock.UtcNow;
        var nonce = new SecureTokenGenerator().GenerateToken(24);
        var canonical = $"GET|/api/v1/trusted-devices|{ts.ToUnixTimeSeconds()}|{nonce}";
        var sig = (await mobile.SignStringAsync(canonical)).Value;

        using var firstReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/trusted-devices");
        firstReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Tokens.AccessToken);
        firstReq.Headers.Add("X-Nexus-Timestamp", ts.ToString("O"));
        firstReq.Headers.Add("X-Nexus-Nonce", nonce);
        firstReq.Headers.Add("X-Nexus-Signature", sig);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(firstReq)).StatusCode);

        // Replaying exact signed HTTP request must fail with 401
        using var replayReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/trusted-devices");
        replayReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Tokens.AccessToken);
        replayReq.Headers.Add("X-Nexus-Timestamp", ts.ToString("O"));
        replayReq.Headers.Add("X-Nexus-Nonce", nonce);
        replayReq.Headers.Add("X-Nexus-Signature", sig);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(replayReq)).StatusCode);
    }

    [Fact]
    public async Task TlsEnforcementMiddleware_RejectsPlainHttp_WhenRequireHttpsEnabled()
    {
        var tlsStorage = Path.Combine(Path.GetTempPath(), "nexus-server-tls-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var tlsFactory = new CustomWebFactory(tlsStorage, _clock, requireHttps: true);
            using var httpClient = tlsFactory.CreateClient();
            var plainResp = await httpClient.GetAsync("/api/v1/system/info");
            Assert.Equal((HttpStatusCode)426, plainResp.StatusCode);

            using var httpsClient = tlsFactory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost")
            });
            var httpsResp = await httpsClient.GetAsync("/api/v1/system/info");
            Assert.Equal(HttpStatusCode.OK, httpsResp.StatusCode);
        }
        finally
        {
            if (Directory.Exists(tlsStorage))
            {
                try { Directory.Delete(tlsStorage, recursive: true); } catch { }
            }
        }
    }

    private CryptographicIdentityProvider CreateCryptoProvider(
        string folderName,
        string deviceName,
        DevicePlatform platform,
        DeviceType deviceType)
    {
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(_serverStorageDir, "client-vaults", folderName)
        });
        var ctx = new DefaultLocalDeviceContext(
            Guid.Empty,
            deviceName,
            platform,
            deviceType,
            platform == DevicePlatform.Windows ? "Windows 11" : "Android 15",
            "0.3.0",
            DeviceCapability.All);

        return new CryptographicIdentityProvider(vault, ctx, new SystemTextJsonSerializer(), _clock);
    }

    private async Task<PairedFlowResult> PairDeviceAsync(
        HttpClient client,
        CryptographicIdentityProvider hostCrypto,
        CryptographicIdentityProvider clientCrypto,
        NexusPermission grantedPermissions)
    {
        var hostProtocol = new NexusPairingProtocolClient(hostCrypto, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);
        var clientProtocol = new NexusPairingProtocolClient(clientCrypto, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);

        var createReq = (await hostProtocol.BuildCreatePairingRequestAsync("http://localhost", null, 120)).Value;
        var createResp = await client.PostAsJsonAsync("/api/v1/pairing/sessions", createReq, JsonOptions);
        createResp.EnsureSuccessStatusCode();
        var createSnap = (await createResp.Content.ReadFromJsonAsync<NexusResponse<PairingSessionSnapshot>>(JsonOptions))!.Payload!;

        var qr = clientProtocol.ParseAndVerifyQrPayload(createSnap.QrPayloadJson!).Value;
        var claim = (await clientProtocol.BuildClaimRequestAsync(qr, grantedPermissions)).Value;
        var claimResp = await client.PostAsJsonAsync("/api/v1/pairing/claim", claim.Request, JsonOptions);
        claimResp.EnsureSuccessStatusCode();
        var claimSnap = (await claimResp.Content.ReadFromJsonAsync<NexusResponse<PairingSessionSnapshot>>(JsonOptions))!.Payload!;
        Assert.Equal(PairingSessionState.AwaitingConfirmation, claimSnap.State);
        Assert.Equal(claim.VerificationCode, claimSnap.VerificationCode);

        var confirmReq = (await hostProtocol.BuildConfirmRequestAsync(qr.PairingId, approve: true, grantedPermissions)).Value;
        var confirmResp = await client.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/confirm", confirmReq, JsonOptions);
        confirmResp.EnsureSuccessStatusCode();
        var confirmSnap = (await confirmResp.Content.ReadFromJsonAsync<NexusResponse<PairingSessionSnapshot>>(JsonOptions))!.Payload!;
        Assert.Equal(PairingSessionState.Connected, confirmSnap.State);

        var finalizeReq = (await clientProtocol.BuildFinalizeRequestAsync(qr.PairingId)).Value;
        var finalizeResp = await client.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/finalize", finalizeReq, JsonOptions);
        finalizeResp.EnsureSuccessStatusCode();
        var tokens = (await finalizeResp.Content.ReadFromJsonAsync<NexusResponse<DeviceAuthTokenPair>>(JsonOptions))!.Payload!;

        var clientIdentity = (await clientCrypto.GetOrCreateIdentityAsync()).Value;
        return new PairedFlowResult(clientIdentity, confirmSnap, claim.VerificationCode, tokens);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try
        {
            if (Directory.Exists(_serverStorageDir))
            {
                Directory.Delete(_serverStorageDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup lock
        }
    }

    private sealed record PairedFlowResult(
        DeviceCryptographicIdentity ClientIdentity,
        PairingSessionSnapshot FinalSnapshot,
        string ClientVerificationCode,
        DeviceAuthTokenPair Tokens);

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;
        public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
    }

    private sealed class CustomWebFactory(string storageDir, MutableClock clock, bool requireHttps = false) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nexus:Storage:RootDirectory"] = storageDir,
                    ["Nexus:Storage:DatabaseFileName"] = "nexus-server-test.db",
                    ["Nexus:Storage:DisablePooling"] = "true",
                    ["Nexus:Tls:BindKestrelHttps"] = "false",
                    ["Nexus:Tls:RequireHttps"] = requireHttps ? "true" : "false",
                    ["Nexus:Security:EnableLocalHostBridge"] = "false",
                    ["Nexus:Discovery:EnableMdns"] = "false"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(clock);
            });
        }
    }
}
