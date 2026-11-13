using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Contracts.Transfers;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Services;
using Nexus.Core.State;
using Nexus.Core.Storage;
using Nexus.Core.Transfers;
using Nexus.Infrastructure.Connectivity;
using Nexus.Infrastructure.Persistence;
using Nexus.Infrastructure.Repositories;
using Nexus.Infrastructure.Security;
using Nexus.Infrastructure.Transfers;
using Nexus.Security.Credentials;
using Nexus.Security.Identity;
using Nexus.Security.Tokens;
using Xunit;

namespace Nexus.IntegrationTests;

public sealed class TransferIntegrationTests : IDisposable
{
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), "nexus-transfer-int-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 11, 11, 19, 0, 0, TimeSpan.Zero));
    private readonly TransferWebFactory _factory;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public TransferIntegrationTests()
    {
        _factory = new TransferWebFactory(_storageDir, _clock);
    }

    [Fact]
    public async Task BidirectionalFileTransfer_WindowsAndAndroid_CompletesViaHttpStreamingWithSignalRNotifications()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("srv-t1", "NEXUS Host");

        await using var winNode = await CreateConnectedTransferNodeAsync(http, host, "win1", "Windows Desktop", DevicePlatform.Windows, NexusPermission.All);
        await using var androidNode = await CreateConnectedTransferNodeAsync(http, host, "and1", "Pixel 9", DevicePlatform.Android, NexusPermission.All);

        var signalRProgressEvents = new ConcurrentBag<FileTransferProgressEvent>();
        winNode.Realtime.TransferProgressReceived += (_, e) => signalRProgressEvents.Add(e);

        // 1) Windows -> Android (.txt)
        var winSourceFile = Path.Combine(winNode.WorkDir, "briefing-nexus.txt");
        var textPayload = "Plano estratégico Fase 07 - NEXUS Transfer bidirecional.\nSegurança TLS + SHA-256."u8.ToArray();
        await File.WriteAllBytesAsync(winSourceFile, textPayload);

        var winSend = await winNode.Manager.EnqueueSendFileAsync(androidNode.Identity.DeviceId, "Pixel 9", winSourceFile);
        Assert.True(winSend.IsSuccess, winSend.Error?.Message);

        await WaitUntilAsync(async () =>
        {
            var androidAll = (await androidNode.Manager.GetAllTransfersAsync()).Value;
            return androidAll.Any(t => t.TransferId == winSend.Value.TransferId && t.State == TransferState.Completed);
        });

        await WaitUntilAsync(async () =>
        {
            var winAll = (await winNode.Manager.GetAllTransfersAsync()).Value;
            return winAll.Any(t => t.TransferId == winSend.Value.TransferId && t.State == TransferState.Completed);
        });

        await WaitUntilAsync(() => Task.FromResult(
            signalRProgressEvents.Any(e => e.TransferId == winSend.Value.TransferId && e.Stage == TransferStage.Completed)));

        var androidReceived = (await androidNode.Manager.GetAllTransfersAsync()).Value.Single(t => t.TransferId == winSend.Value.TransferId);
        Assert.False(androidReceived.AutoExecute);
        Assert.NotNull(androidReceived.LocalFilePath);
        Assert.True(File.Exists(androidReceived.LocalFilePath!));
        Assert.Equal(textPayload, await File.ReadAllBytesAsync(androidReceived.LocalFilePath!));
        Assert.Contains(signalRProgressEvents, e => e.TransferId == winSend.Value.TransferId && e.Stage == TransferStage.Completed);

        // 2) Android -> Windows (.png binary format)
        var androidSourceFile = Path.Combine(androidNode.WorkDir, "photo.png");
        var binaryPayload = new byte[120_000];
        Random.Shared.NextBytes(binaryPayload);
        await File.WriteAllBytesAsync(androidSourceFile, binaryPayload);

        var androidSend = await androidNode.Manager.EnqueueSendFileAsync(winNode.Identity.DeviceId, "Windows Desktop", androidSourceFile);
        Assert.True(androidSend.IsSuccess, androidSend.Error?.Message);

        await WaitUntilAsync(async () =>
        {
            var winAll = (await winNode.Manager.GetAllTransfersAsync()).Value;
            return winAll.Any(t => t.TransferId == androidSend.Value.TransferId && t.State == TransferState.Completed);
        });

        var winReceived = (await winNode.Manager.GetAllTransfersAsync()).Value.Single(t => t.TransferId == androidSend.Value.TransferId);
        Assert.False(winReceived.AutoExecute);
        Assert.Equal(binaryPayload, await File.ReadAllBytesAsync(winReceived.LocalFilePath!));
    }

    [Fact]
    public async Task LargeMultiChunkAndConcurrentFiles_CompleteWithExactSha256()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("srv-t2", "NEXUS Host");

        await using var winNode = await CreateConnectedTransferNodeAsync(http, host, "win2", "Windows", DevicePlatform.Windows, NexusPermission.All);
        await using var androidNode = await CreateConnectedTransferNodeAsync(http, host, "and2", "Android", DevicePlatform.Android, NexusPermission.All);

        await winNode.Manager.UpdateConfigurationAsync(new TransferQueueOptions
        {
            MaxConcurrentTransfers = 2,
            DefaultChunkSizeBytes = 64 * 1024,
            MinimumFreeSpaceSafetyMarginBytes = 0
        });
        await androidNode.Manager.UpdateConfigurationAsync(new TransferQueueOptions
        {
            MaxConcurrentTransfers = 2,
            DefaultChunkSizeBytes = 64 * 1024,
            MinimumFreeSpaceSafetyMarginBytes = 0
        });

        // 1 large multi-chunk file (~650 KB = >10 chunks) + 3 smaller concurrent files
        var sourcePaths = new List<string>();
        var expectedHashes = new Dictionary<string, string>();
        var sizes = new[] { 650_000, 95_000, 140_000, 18_500 };
        for (var i = 0; i < sizes.Length; i++)
        {
            var name = $"asset-{i}.bin";
            var path = Path.Combine(winNode.WorkDir, name);
            var bytes = new byte[sizes[i]];
            Random.Shared.NextBytes(bytes);
            await File.WriteAllBytesAsync(path, bytes);
            sourcePaths.Add(path);
            expectedHashes[name] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        }

        var enqueueRes = await winNode.Manager.EnqueueSendFilesAsync(androidNode.Identity.DeviceId, "Android", sourcePaths);
        Assert.True(enqueueRes.IsSuccess, enqueueRes.Error?.Message);
        Assert.Equal(4, enqueueRes.Value.Count);

        await WaitUntilAsync(async () =>
        {
            var received = (await androidNode.Manager.GetAllTransfersAsync()).Value;
            return received.Count(t => t.State == TransferState.Completed) == 4;
        }, timeoutMs: 12_000);

        var completed = (await androidNode.Manager.GetAllTransfersAsync()).Value;
        foreach (var item in completed)
        {
            Assert.Equal(TransferState.Completed, item.State);
            var actualHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(item.LocalFilePath!)));
            Assert.Equal(expectedHashes[item.SafeFileName], actualHash);
        }
    }

    [Fact]
    public async Task InterruptedUploadAndDownload_ResumeFromExactOffsetAndHttpRange206()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("srv-t3", "NEXUS Host");

        await using var winNode = await CreateConnectedTransferNodeAsync(http, host, "win3", "Windows", DevicePlatform.Windows, NexusPermission.All);
        await using var androidNode = await CreateConnectedTransferNodeAsync(http, host, "and3", "Android", DevicePlatform.Android, NexusPermission.All);

        // Prevent Android from auto-downloading immediately during manual interrupt setup
        await androidNode.Manager.UpdateConfigurationAsync(new TransferQueueOptions
        {
            AutoDownloadIncoming = false,
            MinimumFreeSpaceSafetyMarginBytes = 0
        });

        var payload = new byte[256 * 1024]; // 256 KB
        Random.Shared.NextBytes(payload);
        var sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        var transferId = Guid.NewGuid();

        // Part 1: Interrupted chunked upload (send first 64 KB chunk only, then drop connection)
        var initResp = await winNode.Transport.InitiateTransferAsync(new InitiateFileTransferRequest
        {
            TransferId = transferId,
            SourceDeviceId = winNode.Identity.DeviceId,
            SourceDeviceName = "Windows",
            TargetDeviceId = androidNode.Identity.DeviceId,
            FileName = "resumed-archive.zip",
            ContentType = "application/zip",
            TotalBytes = payload.Length,
            Sha256Checksum = sha,
            ChunkSizeBytes = 64 * 1024
        });
        Assert.True(initResp.IsSuccess);
        Assert.Equal(0, initResp.Value.AcceptedOffsetBytes);

        using (var firstChunk = new MemoryStream(payload, 0, 64 * 1024))
        {
            var chunk1 = await winNode.Transport.UploadChunkAsync(transferId, 0, firstChunk, 64 * 1024);
            Assert.True(chunk1.IsSuccess);
            Assert.Equal(64 * 1024, chunk1.Value.UploadedBytes);
        }

        // Re-initiate after simulated drop: server resumes cleanly at offset 65536
        var resumeInit = await winNode.Transport.InitiateTransferAsync(new InitiateFileTransferRequest
        {
            TransferId = transferId,
            SourceDeviceId = winNode.Identity.DeviceId,
            SourceDeviceName = "Windows",
            TargetDeviceId = androidNode.Identity.DeviceId,
            FileName = "resumed-archive.zip",
            ContentType = "application/zip",
            TotalBytes = payload.Length,
            Sha256Checksum = sha,
            ChunkSizeBytes = 64 * 1024
        });
        Assert.True(resumeInit.IsSuccess);
        Assert.Equal(64 * 1024, resumeInit.Value.AcceptedOffsetBytes);

        using (var restStream = new MemoryStream(payload, 64 * 1024, payload.Length - 64 * 1024))
        {
            var chunk2 = await winNode.Transport.UploadChunkAsync(transferId, 64 * 1024, restStream, payload.Length - 64 * 1024);
            Assert.True(chunk2.IsSuccess);
            Assert.Equal(payload.Length, chunk2.Value.UploadedBytes);
        }

        var finalized = await winNode.Transport.FinalizeUploadAsync(transferId);
        Assert.True(finalized.IsSuccess);
        Assert.Equal(TransferStage.ReadyForDownload, finalized.Value.Stage);

        // Part 2: Verify HTTP 206 Partial Content Range response directly on download endpoint
        using (var rangeReq = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/transfers/{transferId:D}/download"))
        {
            rangeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", androidNode.AccessToken);
            rangeReq.Headers.Range = new RangeHeaderValue(100_000, null);
            using var rangeResp = await http.SendAsync(rangeReq);
            Assert.Equal(HttpStatusCode.PartialContent, rangeResp.StatusCode);
            var tailBytes = await rangeResp.Content.ReadAsByteArrayAsync();
            Assert.Equal(payload.Length - 100_000, tailBytes.Length);
            Assert.Equal(payload[100_000..], tailBytes);
        }

        // Part 3: Seed partial .part on Android (first 100 KB), pause, then resume via TransferManagerService
        var partPath = androidNode.Storage.ResolveSafeStagingPath(transferId, "resumed-archive.zip", isPartFile: true).Value;
        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
        await File.WriteAllBytesAsync(partPath, payload[..100_000]);

        await androidNode.Manager.SyncIncomingTransfersAsync();
        var resumedDownload = await androidNode.Manager.ResumeTransferAsync(transferId);
        Assert.True(resumedDownload.IsSuccess);

        await WaitUntilAsync(async () =>
        {
            var all = (await androidNode.Manager.GetAllTransfersAsync()).Value;
            return all.Any(t => t.TransferId == transferId && t.State == TransferState.Completed);
        });

        var downloaded = (await androidNode.Manager.GetAllTransfersAsync()).Value.Single(t => t.TransferId == transferId);
        Assert.Equal(payload, await File.ReadAllBytesAsync(downloaded.LocalFilePath!));
    }

    [Fact]
    public async Task DuplicatePolicies_RenameAndSkip_WorkEndToEnd()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("srv-t4", "NEXUS Host");

        await using var winNode = await CreateConnectedTransferNodeAsync(http, host, "win4", "Windows", DevicePlatform.Windows, NexusPermission.All);
        await using var androidNode = await CreateConnectedTransferNodeAsync(http, host, "and4", "Android", DevicePlatform.Android, NexusPermission.All);

        var filePath = Path.Combine(winNode.WorkDir, "contract.pdf");
        await File.WriteAllBytesAsync(filePath, "versao-1"u8.ToArray());

        var first = await winNode.Manager.EnqueueSendFileAsync(androidNode.Identity.DeviceId, "Android", filePath, TransferDuplicatePolicy.Rename);
        Assert.True(first.IsSuccess);
        await WaitUntilAsync(async () => (await androidNode.Manager.GetAllTransfersAsync()).Value.Count(t => t.State == TransferState.Completed) == 1);

        // Send second file with same name -> Rename -> contract (1).pdf
        await File.WriteAllBytesAsync(filePath, "versao-2"u8.ToArray());
        var second = await winNode.Manager.EnqueueSendFileAsync(androidNode.Identity.DeviceId, "Android", filePath, TransferDuplicatePolicy.Rename);
        Assert.True(second.IsSuccess);
        await WaitUntilAsync(async () => (await androidNode.Manager.GetAllTransfersAsync()).Value.Count(t => t.State == TransferState.Completed) == 2);

        var receivedNames = (await androidNode.Manager.GetAllTransfersAsync()).Value
            .Select(t => t.SafeFileName)
            .OrderBy(n => n)
            .ToArray();
        Assert.Contains("contract.pdf", receivedNames);
        Assert.Contains("contract (1).pdf", receivedNames);

        // Send third copy with Skip policy -> existing files preserved unchanged
        await File.WriteAllBytesAsync(filePath, "versao-3-ignored"u8.ToArray());
        var third = await winNode.Manager.EnqueueSendFileAsync(androidNode.Identity.DeviceId, "Android", filePath, TransferDuplicatePolicy.Skip);
        Assert.True(third.IsSuccess);
        await WaitUntilAsync(async () => (await androidNode.Manager.GetAllTransfersAsync()).Value.Count(t => t.State == TransferState.Completed) == 3);

        Assert.Equal("versao-1"u8.ToArray(), await File.ReadAllBytesAsync(Path.Combine(androidNode.Storage.ReceivedDirectory, "contract.pdf")));
    }

    [Fact]
    public async Task InsufficientServerDiskSpace_Returns507AndFailsTransferCleanly()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("srv-t5", "NEXUS Host");

        await using var winNode = await CreateConnectedTransferNodeAsync(http, host, "win5", "Windows", DevicePlatform.Windows, NexusPermission.All);
        await using var androidNode = await CreateConnectedTransferNodeAsync(http, host, "and5", "Android", DevicePlatform.Android, NexusPermission.All);

        var serverPolicy = _factory.Services.GetRequiredService<TransferStoragePolicyOptions>();
        serverPolicy.AvailableFreeSpaceProvider = _ => 128; // Only 128 bytes available on server

        try
        {
            var src = Path.Combine(winNode.WorkDir, "too-large.bin");
            await File.WriteAllBytesAsync(src, new byte[4096]);

            var send = await winNode.Manager.EnqueueSendFileAsync(androidNode.Identity.DeviceId, "Android", src);
            Assert.True(send.IsSuccess);

            await WaitUntilAsync(async () =>
            {
                var item = (await winNode.Manager.GetAllTransfersAsync()).Value.Single(t => t.TransferId == send.Value.TransferId);
                return item.State == TransferState.Failed;
            });

            var failed = (await winNode.Manager.GetAllTransfersAsync()).Value.Single(t => t.TransferId == send.Value.TransferId);
            Assert.Equal(TransferFailureReason.InsufficientDiskSpace, failed.FailureReason);
        }
        finally
        {
            serverPolicy.AvailableFreeSpaceProvider = null;
        }
    }

    [Fact]
    public async Task PermissionsPathTraversalRevocationAndRemoteVpnEndpoint_AreEnforcedCorrectly()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("srv-t6", "NEXUS Host");

        await using var winNode = await CreateConnectedTransferNodeAsync(http, host, "win6", "Windows", DevicePlatform.Windows, NexusPermission.All);
        await using var restrictedAndroid = await CreateConnectedTransferNodeAsync(
            http,
            host,
            "and-no-perm",
            "Restricted Phone",
            DevicePlatform.Android,
            NexusPermission.ViewStatus | NexusPermission.Notifications); // Missing FileTransfer permission!

        // 1) Restricted peer denied (403 Forbidden) on direct HTTP transfer initiate
        using (var forbiddenReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers/initiate"))
        {
            forbiddenReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", restrictedAndroid.AccessToken);
            forbiddenReq.Content = JsonContent.Create(new InitiateFileTransferRequest
            {
                TransferId = Guid.NewGuid(),
                SourceDeviceId = restrictedAndroid.Identity.DeviceId,
                TargetDeviceId = winNode.Identity.DeviceId,
                FileName = "secret.txt",
                TotalBytes = 10,
                Sha256Checksum = new string('a', 64)
            });

            using var resp = await http.SendAsync(forbiddenReq);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        // 2) Path traversal filename rejected cleanly with 400 Bad Request
        using (var traversalReq = new HttpRequestMessage(HttpMethod.Post, "/api/v1/transfers/initiate"))
        {
            traversalReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", winNode.AccessToken);
            traversalReq.Content = JsonContent.Create(new InitiateFileTransferRequest
            {
                TransferId = Guid.NewGuid(),
                SourceDeviceId = winNode.Identity.DeviceId,
                TargetDeviceId = winNode.Identity.DeviceId,
                FileName = "../../windows/system32/cmd.exe",
                TotalBytes = 10,
                Sha256Checksum = new string('a', 64)
            });

            using var resp = await http.SendAsync(traversalReq);
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        }

        // 3) Remote Tailscale/HTTPS endpoint configuration transfers seamlessly
        await using var remoteAndroid = await CreateConnectedTransferNodeAsync(
            http,
            host,
            "and-vpn",
            "Android VPN",
            DevicePlatform.Android,
            NexusPermission.All,
            endpointOverride: "https://100.64.0.42:8443");

        var remoteSrc = Path.Combine(winNode.WorkDir, "vpn-notes.md");
        var content = "# Transferência Remota via Tailscale VPN HTTPS"u8.ToArray();
        await File.WriteAllBytesAsync(remoteSrc, content);

        var sendVpn = await winNode.Manager.EnqueueSendFileAsync(remoteAndroid.Identity.DeviceId, "Android VPN", remoteSrc);
        Assert.True(sendVpn.IsSuccess);

        await WaitUntilAsync(async () =>
        {
            var items = (await remoteAndroid.Manager.GetAllTransfersAsync()).Value;
            return items.Any(t => t.TransferId == sendVpn.Value.TransferId && t.State == TransferState.Completed);
        });

        // 4) Revoking device blocks subsequent transfer endpoint access
        using (var revokeReq = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/trusted-devices/{remoteAndroid.Identity.DeviceId:D}/revoke"))
        {
            revokeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", winNode.AccessToken);
            revokeReq.Content = JsonContent.Create(new RevokeDeviceRequest
            {
                Reason = "Teste de revogação Fase 07"
            });
            using var revokeResp = await http.SendAsync(revokeReq);
            Assert.True(revokeResp.IsSuccessStatusCode);
        }

        using (var postRevokeReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/transfers/incoming"))
        {
            postRevokeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", remoteAndroid.AccessToken);
            using var resp = await http.SendAsync(postRevokeReq);
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        }
    }

    private async Task<ConnectedTransferNode> CreateConnectedTransferNodeAsync(
        HttpClient http,
        CryptographicIdentityProvider hostCrypto,
        string key,
        string deviceName,
        DevicePlatform platform,
        NexusPermission permissions,
        string endpointOverride = "http://localhost")
    {
        var dir = Path.Combine(_storageDir, "nodes", key);
        var workDir = Path.Combine(dir, "work");
        Directory.CreateDirectory(workDir);

        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(dir, "vault")
        });
        var localCtx = new DefaultLocalDeviceContext(
            Guid.Empty,
            deviceName,
            platform,
            platform == DevicePlatform.Windows ? DeviceType.Desktop : DeviceType.Phone,
            platform == DevicePlatform.Windows ? "Windows 11" : "Android 15",
            "0.7.0",
            DeviceCapability.All);

        var serializer = new SystemTextJsonSerializer();
        var clientCrypto = new CryptographicIdentityProvider(vault, localCtx, serializer, _clock);
        var hostProtocol = new NexusPairingProtocolClient(hostCrypto, new SecureTokenGenerator(), serializer, _clock);
        var clientProtocol = new NexusPairingProtocolClient(clientCrypto, new SecureTokenGenerator(), serializer, _clock);

        var createReq = (await hostProtocol.BuildCreatePairingRequestAsync(endpointOverride, null, 120)).Value;
        var createResp = await http.PostAsJsonAsync("/api/v1/pairing/sessions", createReq, JsonOptions);
        createResp.EnsureSuccessStatusCode();
        var createSnap = (await createResp.Content.ReadFromJsonAsync<NexusResponse<PairingSessionSnapshot>>(JsonOptions))!.Payload!;

        var qr = clientProtocol.ParseAndVerifyQrPayload(createSnap.QrPayloadJson!).Value;
        var claim = (await clientProtocol.BuildClaimRequestAsync(qr, permissions)).Value;
        (await http.PostAsJsonAsync("/api/v1/pairing/claim", claim.Request, JsonOptions)).EnsureSuccessStatusCode();

        var confirmReq = (await hostProtocol.BuildConfirmRequestAsync(qr.PairingId, approve: true, permissions)).Value;
        (await http.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/confirm", confirmReq, JsonOptions)).EnsureSuccessStatusCode();

        var finalizeReq = (await clientProtocol.BuildFinalizeRequestAsync(qr.PairingId)).Value;
        var finalizeResp = await http.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/finalize", finalizeReq, JsonOptions);
        finalizeResp.EnsureSuccessStatusCode();
        var tokens = (await finalizeResp.Content.ReadFromJsonAsync<NexusResponse<DeviceAuthTokenPair>>(JsonOptions))!.Payload!;

        var identity = (await clientCrypto.GetOrCreateIdentityAsync()).Value;
        var credProvider = new InMemoryCredentialProvider(new RealtimeCredentialBundle(
            identity.DeviceId,
            identity.CustomName,
            tokens.AccessToken,
            tokens.AccessTokenExpiresAtUtc,
            endpointOverride,
            null));

        var storageOptions = new NexusStorageOptions
        {
            RootDirectory = dir,
            DatabaseFileName = "nexus-transfer-node.db",
            EnablePooling = false
        };
        var connFactory = new SqliteConnectionFactory(storageOptions);
        var failureHandler = new StorageFailureHandler(NullLogger<StorageFailureHandler>.Instance);
        var stateMgr = new InMemoryStateManager<NexusRuntimeState>(new NexusRuntimeState
        {
            LocalDevice = localCtx.ToDeviceInfo(),
            UpdatedAtUtc = _clock.UtcNow
        });

        var initializer = new SqliteStorageInitializer(connFactory, failureHandler, stateMgr, _clock, NullLogger<SqliteStorageInitializer>.Instance);
        Assert.True((await initializer.InitializeAsync()).IsSuccess);

        var rtOptions = new RealtimeClientOptions
        {
            AutoReconnect = false,
            DefaultAckTimeout = TimeSpan.FromSeconds(5),
            SignalRMessageHandlerFactory = (_, _) => _factory.Server.CreateHandler()
        };
        var eventBus = new InProcessEventBus();
        var connMgr = new ConnectionStateService(localCtx, _clock, eventBus, stateMgr);
        var realtime = new SignalRRealtimeClient(
            credProvider,
            connMgr,
            new RealtimeMessagePipeline(_clock),
            eventBus,
            serializer,
            _clock,
            rtOptions,
            NullLogger<SignalRRealtimeClient>.Instance);

        var repo = new SqliteTransferRepository(connFactory, failureHandler);
        var storage = new SafeTransferStorageProvider(storageOptions);
        var transport = new HttpTransferTransport(clientProtocol, credProvider, rtOptions, NullLogger<HttpTransferTransport>.Instance);
        var manager = new TransferManagerService(
            repo,
            storage,
            transport,
            realtime,
            credProvider,
            eventBus,
            _clock,
            NullLogger<TransferManagerService>.Instance);

        var openRes = await realtime.OpenAuthenticatedAsync();
        Assert.True(openRes.IsSuccess, openRes.Error?.Message);
        await manager.UpdateConfigurationAsync(new TransferQueueOptions
        {
            MaxConcurrentTransfers = 2,
            DefaultChunkSizeBytes = 64 * 1024,
            MinimumFreeSpaceSafetyMarginBytes = 0
        });
        await manager.InitializeAsync();

        return new ConnectedTransferNode(
            workDir,
            identity,
            tokens.AccessToken,
            realtime,
            storage,
            transport,
            manager);
    }

    private CryptographicIdentityProvider CreateCryptoProvider(string folderName, string deviceName)
    {
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(_storageDir, "vaults", folderName)
        });
        var ctx = new DefaultLocalDeviceContext(
            Guid.Empty,
            deviceName,
            DevicePlatform.Windows,
            DeviceType.Desktop,
            "Windows 11",
            "0.7.0",
            DeviceCapability.All);
        return new CryptographicIdentityProvider(vault, ctx, new SystemTextJsonSerializer(), _clock);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 8000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }
            await Task.Delay(60).ConfigureAwait(false);
        }

        Assert.True(await condition().ConfigureAwait(false), $"Condition timed out after {timeoutMs}ms.");
    }

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_storageDir))
        {
            try { Directory.Delete(_storageDir, recursive: true); } catch { }
        }
    }

    private sealed class ConnectedTransferNode(
        string workDir,
        DeviceCryptographicIdentity identity,
        string accessToken,
        SignalRRealtimeClient realtime,
        SafeTransferStorageProvider storage,
        HttpTransferTransport transport,
        TransferManagerService manager) : IAsyncDisposable
    {
        public string WorkDir { get; } = workDir;
        public DeviceCryptographicIdentity Identity { get; } = identity;
        public string AccessToken { get; } = accessToken;
        public SignalRRealtimeClient Realtime { get; } = realtime;
        public SafeTransferStorageProvider Storage { get; } = storage;
        public HttpTransferTransport Transport { get; } = transport;
        public TransferManagerService Manager { get; } = manager;

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync().ConfigureAwait(false);
            await Realtime.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class InMemoryCredentialProvider(RealtimeCredentialBundle bundle) : IRealtimeCredentialProvider
    {
        public Task<Result<RealtimeCredentialBundle>> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeCredentialBundle>.Success(bundle));

        public Task<Result<RealtimeCredentialBundle>> RefreshCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeCredentialBundle>.Failure(NexusError.Unauthorized("Refresh disabled", code: "REALTIME_AUTH_EXPIRED")));
    }

    private sealed class TransferWebFactory(string storageDir, MutableClock clock) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nexus:Storage:RootDirectory"] = storageDir,
                    ["Nexus:Storage:DatabaseFileName"] = "nexus-server-transfer.db",
                    ["Nexus:Storage:DisablePooling"] = "true",
                    ["Nexus:Tls:BindKestrelHttps"] = "false",
                    ["Nexus:Tls:RequireHttps"] = "false",
                    ["Nexus:Security:EnableLocalHostBridge"] = "false",
                    ["Nexus:Discovery:EnableMdns"] = "false"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IClock>();
                services.AddSingleton<IClock>(clock);
                services.RemoveAll<TransferQueueOptions>();
                services.AddSingleton(new TransferQueueOptions
                {
                    MaxConcurrentTransfers = 2,
                    DefaultChunkSizeBytes = 64 * 1024,
                    MinimumFreeSpaceSafetyMarginBytes = 0
                });
            });
        }
    }
}
