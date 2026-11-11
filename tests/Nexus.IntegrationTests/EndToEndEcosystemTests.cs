using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Contracts.Automation;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Modules;
using Nexus.Contracts.Notifications;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Contracts.Sync;
using Nexus.Contracts.Transfers;
using Nexus.Contracts.Updates;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Modules;
using Nexus.Core.Services;
using Nexus.Core.State;
using Nexus.Core.Storage;
using Nexus.Core.Transfers;
using Nexus.Core.Updates;
using Nexus.Infrastructure.Automation;
using Nexus.Infrastructure.Connectivity;
using Nexus.Infrastructure.DependencyInjection;
using Nexus.Infrastructure.Modules;
using Nexus.Infrastructure.Persistence;
using Nexus.Infrastructure.Repositories;
using Nexus.Infrastructure.Security;
using Nexus.Infrastructure.Settings;
using Nexus.Infrastructure.Sync;
using Nexus.Infrastructure.Transfers;
using Nexus.Infrastructure.Updates;
using Nexus.Security.Credentials;
using Nexus.Security.Identity;
using Nexus.Security.Tokens;
using Nexus.Security.Updates;
using Nexus.Server.Endpoints;
using Nexus.Server.Options;
using Xunit;

namespace Nexus.IntegrationTests;

public sealed class EndToEndEcosystemTests : IDisposable
{
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), "nexus-f11-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new(new DateTimeOffset(2026, 11, 11, 21, 0, 0, TimeSpan.Zero));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task SecurityConsolidation_QrSecretScrubbed_ImmediateRevocation_PermissionsAndPathTraversalBlocked()
    {
        using var factory = new EcosystemWebFactory(_storageDir, _clock, new GitHubUpdateRelayOptions());
        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var hostCrypto = CreateCryptoProvider("sec-host", "NEXUS Host", DevicePlatform.Windows, DeviceType.Desktop);
        var observerNode = await CreateEcosystemNodeAsync(
            factory,
            http,
            hostCrypto,
            "sec-observer",
            "Windows Observer",
            DevicePlatform.Windows,
            DeviceType.Desktop,
            NexusPermission.All);

        var observedSnapshots = new ConcurrentBag<PairingSessionSnapshot>();
        await using var monitorHub = new HubConnectionBuilder()
            .WithUrl("https://localhost/hubs/nexus", o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(observerNode.Credentials.AccessToken);
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();
        monitorHub.On<PairingSessionSnapshot>("ReceivePairingSnapshot", snap => observedSnapshots.Add(snap));
        await monitorHub.StartAsync();

        // 1) QR secret emitted ONLY on session creation, scrubbed on GET /sessions/{id} and SignalR broadcasts
        var limitedCrypto = CreateCryptoProvider("sec-limited", "Limited Phone", DevicePlatform.Android, DeviceType.Phone);
        using var hostProtocol = new NexusPairingProtocolClient(hostCrypto, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);
        using var limitedProtocol = new NexusPairingProtocolClient(limitedCrypto, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);

        var createReq = (await hostProtocol.BuildCreatePairingRequestAsync("https://localhost", null, 120)).Value;
        var createResp = await http.PostAsJsonAsync("/api/v1/pairing/sessions", createReq, JsonOptions);
        createResp.EnsureSuccessStatusCode();
        var createSnap = (await createResp.Content.ReadFromJsonAsync<NexusResponse<PairingSessionSnapshot>>(JsonOptions))!.Payload!;
        Assert.False(string.IsNullOrWhiteSpace(createSnap.QrPayloadJson));

        var getAfterCreate = (await http.GetFromJsonAsync<NexusResponse<PairingSessionSnapshot>>(
            $"/api/v1/pairing/sessions/{createSnap.PairingId:D}",
            JsonOptions))!.Payload!;
        Assert.Null(getAfterCreate.QrPayloadJson);

        var qr = limitedProtocol.ParseAndVerifyQrPayload(createSnap.QrPayloadJson!).Value;
        var limitedPerms = NexusPermission.ViewStatus | NexusPermission.Clipboard;
        var claim = (await limitedProtocol.BuildClaimRequestAsync(qr, limitedPerms)).Value;
        var claimResp = await http.PostAsJsonAsync("/api/v1/pairing/claim", claim.Request, JsonOptions);
        claimResp.EnsureSuccessStatusCode();

        var confirmReq = (await hostProtocol.BuildConfirmRequestAsync(qr.PairingId, approve: true, limitedPerms)).Value;
        var confirmResp = await http.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/confirm", confirmReq, JsonOptions);
        confirmResp.EnsureSuccessStatusCode();

        var finalizeReq = (await limitedProtocol.BuildFinalizeRequestAsync(qr.PairingId)).Value;
        var finalizeResp = await http.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/finalize", finalizeReq, JsonOptions);
        finalizeResp.EnsureSuccessStatusCode();
        var limitedTokens = (await finalizeResp.Content.ReadFromJsonAsync<NexusResponse<DeviceAuthTokenPair>>(JsonOptions))!.Payload!;
        var limitedId = (await limitedCrypto.GetOrCreateIdentityAsync()).Value.DeviceId;

        await WaitUntilAsync(() => Task.FromResult(observedSnapshots.Count >= 2));
        Assert.All(observedSnapshots, snap => Assert.Null(snap.QrPayloadJson));

        var getAfterFinalize = (await http.GetFromJsonAsync<NexusResponse<PairingSessionSnapshot>>(
            $"/api/v1/pairing/sessions/{createSnap.PairingId:D}",
            JsonOptions))!.Payload!;
        Assert.Null(getAfterFinalize.QrPayloadJson);

        // 2) Fine-grained permission enforcement (403 Forbidden for ungranted modules)
        using var limitedHttp = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        limitedHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", limitedTokens.AccessToken);

        var controlReq = new RemoteControlCommandRequest
        {
            SourceDeviceId = limitedId,
            Action = RemoteControlActionType.MouseLeftClick
        };
        var deniedControl = await limitedHttp.PostAsJsonAsync("/api/v1/modules/control/execute", controlReq, JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, deniedControl.StatusCode);

        var deniedNotify = await limitedHttp.PostAsJsonAsync("/api/v1/modules/notify/push", new PushMobileNotificationRequest
        {
            SourceDeviceId = limitedId,
            AppName = "WhatsApp",
            Title = "Teste",
            Body = "Msg"
        }, JsonOptions);
        Assert.Equal(HttpStatusCode.Forbidden, deniedNotify.StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await limitedHttp.GetAsync("/api/v1/automations/rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await limitedHttp.GetAsync("/api/v1/sync/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await limitedHttp.DeleteAsync($"/api/v1/trusted-devices/{observerNode.Identity.DeviceId:D}")).StatusCode);

        var transferInitReq = new InitiateFileTransferRequest
        {
            TransferId = Guid.CreateVersion7(),
            SourceDeviceId = limitedId,
            SourceDeviceName = "Limited Phone",
            TargetDeviceId = observerNode.Identity.DeviceId,
            FileName = "doc.txt",
            TotalBytes = 128,
            Sha256Checksum = new string('a', 64)
        };
        Assert.Equal(HttpStatusCode.Forbidden, (await limitedHttp.PostAsJsonAsync("/api/v1/transfers/initiate", transferInitReq, JsonOptions)).StatusCode);

        // 3) Path traversal rejected with 400 TRANSFER_FILENAME_UNSAFE
        using var fullHttp = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        fullHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", observerNode.Credentials.AccessToken);
        var traversalReq = transferInitReq with
        {
            SourceDeviceId = observerNode.Identity.DeviceId,
            TargetDeviceId = limitedId,
            FileName = "..\\..\\evil\\payload.exe"
        };
        var traversalResp = await fullHttp.PostAsJsonAsync("/api/v1/transfers/initiate", traversalReq, JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, traversalResp.StatusCode);
        var traversalBody = await traversalResp.Content.ReadAsStringAsync();
        Assert.Contains("TRANSFER_FILENAME_UNSAFE", traversalBody, StringComparison.Ordinal);

        // 4) Immediate revocation blocks subsequent REST & SignalR hub connections with 401
        var revokeResp = await fullHttp.PostAsync($"/api/v1/trusted-devices/{limitedId:D}/revoke", null);
        revokeResp.EnsureSuccessStatusCode();

        var afterRevokeRest = await limitedHttp.GetAsync("/api/v1/modules/status");
        Assert.Equal(HttpStatusCode.Unauthorized, afterRevokeRest.StatusCode);

        await using var revokedHub = new HubConnectionBuilder()
            .WithUrl("https://localhost/hubs/nexus", o =>
            {
                o.AccessTokenProvider = () => Task.FromResult<string?>(limitedTokens.AccessToken);
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();
        var hubEx = await Assert.ThrowsAsync<HttpRequestException>(() => revokedHub.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, hubEx.StatusCode);

        await observerNode.DisposeAsync();
    }

    [Fact]
    public async Task FullEcosystem_EndToEnd_All14Phase11Scenarios_WindowsAndAndroid()
    {
        using var signingCert = CreateSigningCert("CN=NEXUS Ecosystem Release");
        var signerSha = Convert.ToHexStringLower(SHA256.HashData(signingCert.RawData));
        var signedApk = CreateSignedApk("com.nexus.ecosystem", 10400, signingCert);
        var apkSha = Convert.ToHexStringLower(SHA256.HashData(signedApk));
        const string serverPat = "ghp_server_only_relay_token_f11";

        using var gitHubMock = new DelegatingStubHandler(req =>
        {
            var uri = req.RequestUri!;
            if (uri.Host == "api.github.com")
            {
                if (req.Headers.Authorization?.Parameter != serverPat)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

                if (uri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new
                        {
                            tag_name = "v1.4.0",
                            name = "NEXUS v1.4.0",
                            body = "Consolidação Fase 11",
                            draft = false,
                            prerelease = false,
                            published_at = _clock.UtcNow,
                            assets = new[]
                            {
                                new
                                {
                                    name = "com.nexus.ecosystem-1.4.0.apk",
                                    size = (long)signedApk.Length,
                                    browser_download_url = "https://github.com/nexus-org/nexus/releases/download/v1.4.0/app.apk",
                                    url = "https://api.github.com/repos/nexus-org/nexus/releases/assets/2001"
                                },
                                new
                                {
                                    name = "nexus-update-manifest.json",
                                    size = 512L,
                                    browser_download_url = "https://github.com/nexus-org/nexus/releases/download/v1.4.0/nexus-update-manifest.json",
                                    url = "https://api.github.com/repos/nexus-org/nexus/releases/assets/2002"
                                }
                            }
                        }, options: JsonOptions)
                    };
                }

                if (uri.AbsolutePath.EndsWith("/releases/assets/2002", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new NexusReleaseManifest
                        {
                            Version = "1.4.0",
                            AndroidVersionCode = 10400,
                            PackageId = "com.nexus.ecosystem",
                            TagName = "v1.4.0",
                            ApkFileName = "com.nexus.ecosystem-1.4.0.apk",
                            ApkSha256 = apkSha,
                            SignerCertificateSha256 = signerSha,
                            ApkSizeBytes = signedApk.Length,
                            PublishedAtUtc = _clock.UtcNow,
                            MinimumCompatibleServerVersion = "1.0.0",
                            ReleaseNotes = "Consolidação Fase 11",
                            DownloadUrl = "https://github.com/nexus-org/nexus/releases/download/v1.4.0/app.apk"
                        }, options: JsonOptions)
                    };
                }

                if (uri.AbsolutePath.EndsWith("/releases/assets/2001", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(signedApk)
                    };
                }
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var factory = new EcosystemWebFactory(
            _storageDir,
            _clock,
            new GitHubUpdateRelayOptions
            {
                RepositoryOwner = "nexus-org",
                RepositoryName = "nexus",
                ExpectedPackageId = "com.nexus.ecosystem",
                ExpectedSignerSha256 = signerSha,
                ServerTokenResolver = () => serverPat,
                OutboundGitHubHandlerFactory = () => gitHubMock
            });

        using var http = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        var hostCrypto = CreateCryptoProvider("e2e-host", "NEXUS Host", DevicePlatform.Windows, DeviceType.Desktop);

        // [1] Pairing Android and Windows
        await using var winNode = await CreateEcosystemNodeAsync(
            factory, http, hostCrypto, "e2e-win", "NEXUS Windows Studio", DevicePlatform.Windows, DeviceType.Desktop, NexusPermission.All);
        await using var androidNode = await CreateEcosystemNodeAsync(
            factory, http, hostCrypto, "e2e-and", "Pixel 9 Android", DevicePlatform.Android, DeviceType.Phone, NexusPermission.All);

        Assert.Equal(ConnectionState.Connected, winNode.Realtime.State);
        Assert.Equal(ConnectionState.Connected, androidNode.Realtime.State);

        // [2 & 3] Local connection & Tailscale VPN failover probed against real Nexus.Server /api/v1/system/info
        using var sysReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/system/info");
        var sysInfo = (await http.GetFromJsonAsync<NexusResponse<SystemInfoDto>>("/api/v1/system/info", JsonOptions))!.Payload!;

        var reachability = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://192.168.1.50:5443"] = true,
            ["https://100.88.1.50:5443"] = true
        };
        var netAdapter = new SimulatedNetworkAdapter
        {
            Snapshot = new NetworkEnvironmentSnapshot
            {
                IsNetworkAvailable = true,
                HasInternetAccess = true,
                HasLocalLanInterface = true,
                LocalIpv4Addresses = ["192.168.1.25"],
                VpnStatus = VpnStatus.Connected,
                VpnProvider = "Tailscale",
                TailscaleAddresses = ["100.88.2.99"]
            }
        };

        await using (var orchHarness = await CreateOrchestratorHarnessAsync(factory, netAdapter, reachability))
        {
            await orchHarness.Orchestrator.RegisterEndpointAsync(new RegisterEndpointRequest
            {
                DeviceId = sysInfo.HostDevice.Id,
                DeviceName = sysInfo.HostDevice.Name,
                Label = "Wi-Fi Local",
                RouteKind = ConnectionRouteKind.LocalLan,
                EndpointUrl = "https://192.168.1.50:5443",
                ExpectedHostKeyFingerprint = sysInfo.HostKeyFingerprint,
                Origin = EndpointOrigin.MdnsDiscovered
            });
            await orchHarness.Orchestrator.RegisterEndpointAsync(new RegisterEndpointRequest
            {
                DeviceId = sysInfo.HostDevice.Id,
                DeviceName = sysInfo.HostDevice.Name,
                Label = "Tailscale VPN",
                RouteKind = ConnectionRouteKind.RemoteVpnTailscale,
                EndpointUrl = "https://100.88.1.50:5443",
                ExpectedHostKeyFingerprint = sysInfo.HostKeyFingerprint,
                Origin = EndpointOrigin.TailscalePrivate
            });

            var localSelected = await orchHarness.Orchestrator.SelectAndConnectBestRouteAsync();
            Assert.True(localSelected.IsSuccess, localSelected.Error?.Message);
            Assert.Equal(ConnectionRouteKind.LocalLan, localSelected.Value.RouteKind);

            // Switch to cellular/remote Tailscale VPN when LAN drops
            reachability["https://192.168.1.50:5443"] = false;
            netAdapter.Snapshot = netAdapter.Snapshot with
            {
                HasLocalLanInterface = false,
                IsCellularActive = true,
                LocalIpv4Addresses = []
            };

            var vpnFailover = await orchHarness.Orchestrator.HandleNetworkChangedAsync();
            Assert.True(vpnFailover.IsSuccess, vpnFailover.Error?.Message);
            Assert.Equal(ConnectionRouteKind.RemoteVpnTailscale, vpnFailover.Value.RouteKind);
        }

        // [4] Reconnection after network loss
        await androidNode.Realtime.CloseAsync();
        Assert.False(androidNode.Realtime.IsConnected);
        var reopened = await androidNode.Realtime.OpenAuthenticatedAsync();
        Assert.True(reopened.IsSuccess, reopened.Error?.Message);
        var presence = (await winNode.Realtime.RefreshPresenceAsync()).Value;
        Assert.Contains(presence, p => p.DeviceId == androidNode.Identity.DeviceId && p.IsRealtimeConnected);

        // [5] Notes sync
        var createdNote = await winNode.NotesService.CreateNoteAsync("Checklist Final", "Entrega Fase 11 validada.");
        Assert.True(createdNote.IsSuccess, createdNote.Error?.Message);
        await WaitUntilAsync(async () =>
        {
            var found = await androidNode.NotesService.ListNotesAsync("Fase 11");
            return found.IsSuccess && found.Value.Count == 1;
        });

        // [6] Clipboard sync
        var androidClipboardTcs = new TaskCompletionSource<ClipboardEntryContract>(TaskCreationOptions.RunContinuationsAsynchronously);
        androidNode.ClipboardService.RemoteClipboardApplied += (_, c) => androidClipboardTcs.TrySetResult(c);
        var clipSent = await winNode.ClipboardService.CaptureAndSendTextAsync("NEXUS-OTP-774411", manualTrigger: true);
        Assert.True(clipSent.IsSuccess, clipSent.Error?.Message);
        var clipArrived = await androidClipboardTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("NEXUS-OTP-774411", clipArrived.ContentText);

        // [7] Bidirectional HTTP File Transfer (Android -> Windows multi-chunk binary with SHA-256 verification)
        var payloadBytes = new byte[150 * 1024];
        RandomNumberGenerator.Fill(payloadBytes);
        var expectedFileSha = Convert.ToHexStringLower(SHA256.HashData(payloadBytes));
        var srcPath = Path.Combine(androidNode.WorkDir, "e2e-telemetry-dump.bin");
        await File.WriteAllBytesAsync(srcPath, payloadBytes);

        var enqueueRes = await androidNode.TransferManager.EnqueueSendFileAsync(
            winNode.Identity.DeviceId,
            winNode.Identity.CustomName,
            srcPath);
        Assert.True(enqueueRes.IsSuccess, enqueueRes.Error?.Message);

        await WaitUntilAsync(async () =>
        {
            var winAll = (await winNode.TransferManager.GetAllTransfersAsync()).Value;
            return winAll.Any(t => t.TransferId == enqueueRes.Value.TransferId && t.State == TransferState.Completed);
        });
        var completedTransfer = (await winNode.TransferManager.GetAllTransfersAsync()).Value
            .Single(t => t.TransferId == enqueueRes.Value.TransferId);
        Assert.Equal(expectedFileSha, completedTransfer.Sha256Checksum);
        Assert.True(File.Exists(completedTransfer.LocalFilePath));

        // [8] Remote Control (mouse click + power confirmation gate)
        var clickExec = await androidNode.ModulesClient.SendControlCommandAsync(new RemoteControlCommandRequest
        {
            SourceDeviceId = androidNode.Identity.DeviceId,
            Action = RemoteControlActionType.MouseLeftClick
        });
        Assert.True(clickExec.IsSuccess, clickExec.Error?.Message);
        Assert.True(clickExec.Value.Executed);

        var powerNeedsConfirm = await androidNode.ModulesClient.SendControlCommandAsync(new RemoteControlCommandRequest
        {
            SourceDeviceId = androidNode.Identity.DeviceId,
            Action = RemoteControlActionType.PowerLock,
            ConfirmedByUser = false
        });
        Assert.True(powerNeedsConfirm.IsFailure);
        Assert.Equal("CONFIRMATION_REQUIRED", powerNeedsConfirm.Error!.Code);

        // [9] Notifications (filter + SignalR delivery to Windows)
        var notifyTcs = new TaskCompletionSource<MobileNotificationRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        winNode.Realtime.MobileNotificationReceived += (_, n) => notifyTcs.TrySetResult(n);
        var serverModSettings = factory.Services.GetRequiredService<IModuleSettingsService>();
        await serverModSettings.SaveNotifySettingsAsync(new NotifyModuleSettings
        {
            IsEnabled = true,
            FilterMode = NotificationFilterMode.AllowList,
            AllowedApps = ["WhatsApp"]
        });

        var pushedNotify = await androidNode.ModulesClient.PushMobileNotificationAsync(new PushMobileNotificationRequest
        {
            SourceDeviceId = androidNode.Identity.DeviceId,
            AppName = "WhatsApp",
            PackageName = "com.whatsapp",
            Title = "Carlos",
            Body = "Teste E2E NEXUS"
        });
        Assert.True(pushedNotify.IsSuccess && pushedNotify.Value.Forwarded);
        var arrivedNotify = await notifyTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Carlos", arrivedNotify.Title);

        // [10] Host Monitor
        var monitorRes = await androidNode.ModulesClient.GetRemoteMonitorSnapshotAsync();
        Assert.True(monitorRes.IsSuccess, monitorRes.Error?.Message);
        Assert.True(monitorRes.Value.Cpu.LogicalCores > 0);
        Assert.True(monitorRes.Value.Memory.TotalMb.IsAvailable && monitorRes.Value.Memory.TotalMb.Value > 0);

        // [11] Media Control
        var mediaTcs = new TaskCompletionSource<MediaPlaybackStateContract>(TaskCreationOptions.RunContinuationsAsynchronously);
        winNode.Realtime.MediaStateReceived += (_, m) => mediaTcs.TrySetResult(m);
        var mediaRes = await androidNode.ModulesClient.SendMediaCommandAsync(new MediaControlCommandRequest
        {
            SourceDeviceId = androidNode.Identity.DeviceId,
            Action = MediaControlAction.PlayPause
        });
        Assert.True(mediaRes.IsSuccess, mediaRes.Error?.Message);
        var mediaState = await mediaTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MediaPlaybackStatus.Playing, mediaState.PlaybackStatus);

        // [12] Nexus Panel
        var panelTcs = new TaskCompletionSource<PanelProfile>(TaskCreationOptions.RunContinuationsAsynchronously);
        winNode.Realtime.PanelProfileUpdatedReceived += (_, p) => panelTcs.TrySetResult(p);
        var profiles = (await androidNode.PanelClient.GetRemoteProfilesAsync()).Value;
        var updatedProfile = profiles[0] with
        {
            Theme = PanelThemePreset.SlateGlass,
            Accent = PanelAccentColor.Violet,
            GridColumns = 3
        };
        var savedPanel = await androidNode.PanelClient.SaveRemoteProfileAsync(updatedProfile);
        Assert.True(savedPanel.IsSuccess, savedPanel.Error?.Message);
        var relayedPanel = await panelTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, relayedPanel.GridColumns);

        // [13] Nexus Automate
        var execTcs = new TaskCompletionSource<AutomationExecutionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        winNode.Realtime.AutomationExecutionReceived += (_, rec) => execTcs.TrySetResult(rec);
        var autoRule = new AutomationRule
        {
            RuleId = Guid.NewGuid(),
            Name = "E2E Conectar -> Perfil",
            Trigger = AutomationTriggerType.ConnectedToComputer,
            Actions =
            [
                new AutomationAction
                {
                    ActionType = AutomationActionType.ActivatePanelProfile,
                    TargetProfileId = updatedProfile.ProfileId,
                    Label = "Perfil E2E"
                }
            ]
        };
        Assert.True((await androidNode.PanelClient.SaveRemoteRuleAsync(autoRule)).IsSuccess);
        var triggerRes = await androidNode.PanelClient.DispatchRemoteTriggerAsync(new AutomationTriggerDispatchRequest
        {
            RuleId = autoRule.RuleId,
            Trigger = AutomationTriggerType.ConnectedToComputer
        });
        Assert.True(triggerRes.IsSuccess);
        Assert.Equal(AutomationExecutionOutcome.Succeeded, Assert.Single(triggerRes.Value).Outcome);
        var relayedExec = await execTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(autoRule.RuleId, relayedExec.RuleId);

        // [14] Update Check & Verified APK Download via Private Server Relay
        await androidNode.UpdateService.SaveConfigurationAsync(new UpdateConfiguration
        {
            RepositoryOwner = "nexus-org",
            RepositoryName = "nexus",
            Visibility = UpdateRepositoryVisibility.PrivateServerRelay,
            ExpectedSignerSha256 = signerSha
        });
        var updateCheck = await androidNode.UpdateService.CheckForUpdatesAsync();
        Assert.True(updateCheck.IsSuccess, updateCheck.Error?.Message);
        Assert.True(updateCheck.Value.IsUpdateAvailable);
        Assert.True(updateCheck.Value.LatestRelease!.ServedThroughServerRelay);

        var verifiedPkg = await androidNode.UpdateService.DownloadAndVerifyUpdateAsync(updateCheck.Value.LatestRelease);
        Assert.True(verifiedPkg.IsSuccess, verifiedPkg.Error?.Message);
        Assert.True(verifiedPkg.Value.IsReadyForUserInstall);
        Assert.Equal(apkSha, verifiedPkg.Value.ActualSha256Hex);
        Assert.Equal(signerSha, verifiedPkg.Value.ActualSignerSha256);
    }

    private async Task<EcosystemNode> CreateEcosystemNodeAsync(
        EcosystemWebFactory factory,
        HttpClient http,
        CryptographicIdentityProvider hostCrypto,
        string folderName,
        string deviceName,
        DevicePlatform platform,
        DeviceType deviceType,
        NexusPermission permissions)
    {
        var dir = Path.Combine(_storageDir, "nodes", folderName);
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
            deviceType,
            platform == DevicePlatform.Windows ? "Windows 11" : "Android 15",
            "1.0.0",
            DeviceCapability.All);
        var serializer = new SystemTextJsonSerializer();
        var clientCrypto = new CryptographicIdentityProvider(vault, localCtx, serializer, _clock);
        var hostProtocol = new NexusPairingProtocolClient(hostCrypto, new SecureTokenGenerator(), serializer, _clock);
        var clientProtocol = new NexusPairingProtocolClient(clientCrypto, new SecureTokenGenerator(), serializer, _clock);

        const string endpoint = "https://localhost";
        var createReq = (await hostProtocol.BuildCreatePairingRequestAsync(endpoint, null, 120)).Value;
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
        var creds = new RealtimeCredentialBundle(
            identity.DeviceId,
            identity.CustomName,
            tokens.AccessToken,
            tokens.AccessTokenExpiresAtUtc,
            endpoint,
            null);
        var credProvider = new StaticCredentialProvider(creds);

        var storageOptions = new NexusStorageOptions
        {
            RootDirectory = dir,
            DatabaseFileName = "nexus-e2e-node.db",
            EnablePooling = false
        };
        var connFactory = new SqliteConnectionFactory(storageOptions);
        var failureHandler = new StorageFailureHandler(NullLogger<StorageFailureHandler>.Instance);
        var stateMgr = new InMemoryStateManager<NexusRuntimeState>(new NexusRuntimeState
        {
            LocalDevice = localCtx.ToDeviceInfo(),
            UpdatedAtUtc = _clock.UtcNow
        });
        Assert.True((await new SqliteStorageInitializer(connFactory, failureHandler, stateMgr, _clock, NullLogger<SqliteStorageInitializer>.Instance).InitializeAsync()).IsSuccess);

        var rtOptions = new RealtimeClientOptions
        {
            AutoReconnect = false,
            DefaultAckTimeout = TimeSpan.FromSeconds(5),
            SignalRMessageHandlerFactory = (_, _) => factory.Server.CreateHandler()
        };
        var eventBus = new InProcessEventBus();
        var connMgr = new ConnectionStateService(localCtx, _clock, eventBus, stateMgr);
        var realtime = new SignalRRealtimeClient(
            credProvider, connMgr, new RealtimeMessagePipeline(_clock), eventBus, serializer, _clock, rtOptions, NullLogger<SignalRRealtimeClient>.Instance);
        Assert.True((await realtime.OpenAuthenticatedAsync()).IsSuccess);

        var queueRepo = new SqliteSyncQueueRepository(connFactory, failureHandler);
        var clipboardRepo = new SqliteClipboardRepository(connFactory, failureHandler);
        var notesRepo = new SqliteNotesRepository(connFactory, failureHandler);
        var settingsStore = new SqliteSettingsStore(connFactory, failureHandler, serializer, _clock);
        var privacyService = new SyncPrivacySettingsService(settingsStore, _clock, NullLogger<SyncPrivacySettingsService>.Instance);
        var clipboardAdapter = new DefaultSystemClipboardAdapter();
        var syncTransport = new ServerBasicSyncTransport(clientProtocol, credProvider, realtime, rtOptions, NullLogger<ServerBasicSyncTransport>.Instance);
        var clipboardService = new ClipboardSyncCoordinator(clipboardAdapter, clipboardRepo, queueRepo, privacyService, syncTransport, clientCrypto, _clock, NullLogger<ClipboardSyncCoordinator>.Instance);
        var notesService = new NotesSyncCoordinator(notesRepo, queueRepo, privacyService, syncTransport, clientCrypto, _clock, NullLogger<NotesSyncCoordinator>.Instance);
        var syncOrchestrator = new BasicSyncOrchestrator(clipboardService, notesService, clipboardRepo, notesRepo, queueRepo, privacyService, syncTransport, realtime, eventBus, _clock, NullLogger<BasicSyncOrchestrator>.Instance);
        await syncOrchestrator.InitializeAsync();

        var transferRepo = new SqliteTransferRepository(connFactory, failureHandler);
        var transferStorage = new SafeTransferStorageProvider(storageOptions);
        var transferTransport = new HttpTransferTransport(clientProtocol, credProvider, rtOptions, NullLogger<HttpTransferTransport>.Instance);
        var transferManager = new TransferManagerService(transferRepo, transferStorage, transferTransport, realtime, credProvider, eventBus, _clock, NullLogger<TransferManagerService>.Instance);
        await transferManager.UpdateConfigurationAsync(new TransferQueueOptions
        {
            MaxConcurrentTransfers = 2,
            DefaultChunkSizeBytes = 64 * 1024,
            MinimumFreeSpaceSafetyMarginBytes = 0
        });
        await transferManager.InitializeAsync();

        var modulesClient = new HttpIntegrationModulesClient(clientProtocol, credProvider, rtOptions, NullLogger<HttpIntegrationModulesClient>.Instance);
        var panelClient = new HttpPanelAndAutomationClient(clientProtocol, credProvider, rtOptions, NullLogger<HttpPanelAndAutomationClient>.Instance);

        var updateOpts = new NexusUpdateOptions();
        var releaseClient = new GitHubReleaseClient(updateOpts, credProvider, rtOptions, clientProtocol, NullLogger<GitHubReleaseClient>.Instance);
        var installed = new InstalledAppVersionInfo
        {
            PackageId = "com.nexus.ecosystem",
            Version = "1.0.0",
            VersionCode = 10000,
            SignerCertificateSha256 = null,
            SupportsNativePackageInstaller = true,
            CanRequestPackageInstalls = true
        };
        var updateService = new NexusUpdateService(
            settingsStore,
            updateOpts,
            storageOptions,
            new StaticAppInspector(installed),
            releaseClient,
            new ApkPackageVerifier(new ManagedApkArchiveReader(), _clock),
            new NoOpInstaller(),
            new SqliteUpdateHistoryRepository(connFactory, failureHandler),
            eventBus,
            _clock,
            NullLogger<NexusUpdateService>.Instance);

        return new EcosystemNode(
            workDir,
            identity,
            creds,
            hostProtocol,
            clientProtocol,
            realtime,
            syncOrchestrator,
            clipboardService,
            notesService,
            transferManager,
            modulesClient,
            panelClient,
            updateService);
    }

    private async Task<OrchestratorHarness> CreateOrchestratorHarnessAsync(
        EcosystemWebFactory factory,
        INetworkConnectivityAdapter adapter,
        ConcurrentDictionary<string, bool> reachability)
    {
        var services = new ServiceCollection();
        services.AddNexusInfrastructure(
            configureStorage: s =>
            {
                s.RootDirectory = Path.Combine(_storageDir, "orch-" + Guid.NewGuid().ToString("N"));
                s.EnablePooling = false;
            },
            configureLogging: l => l.EnableConsole = false);
        services.AddSingleton(adapter);
        services.AddSingleton(new ConnectivityOptions { EnableBackgroundMonitor = false });
        services.AddSingleton(sp => new NexusPairingProtocolClient(
            sp.GetRequiredService<ICryptographicIdentityProvider>(),
            sp.GetRequiredService<ITokenGenerator>(),
            sp.GetRequiredService<IJsonSerializer>(),
            sp.GetRequiredService<IClock>(),
            customHttpClientFactory: (baseEndpoint, _) =>
            {
                var normalized = baseEndpoint.Trim().TrimEnd('/');
                HttpMessageHandler handler = reachability.TryGetValue(normalized, out var isUp) && isUp
                    ? factory.Server.CreateHandler()
                    : new FailingNetworkHandler();
                return new HttpClient(handler, disposeHandler: true)
                {
                    BaseAddress = new Uri(normalized + "/", UriKind.Absolute)
                };
            }));

        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IStorageInitializer>().InitializeAsync();
        var orchestrator = provider.GetRequiredService<INexusRouteOrchestrator>();
        await orchestrator.InitializeAsync();
        return new OrchestratorHarness(provider, orchestrator);
    }

    private CryptographicIdentityProvider CreateCryptoProvider(string folderName, string deviceName, DevicePlatform platform, DeviceType deviceType)
    {
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(_storageDir, "vaults", folderName)
        });
        var ctx = new DefaultLocalDeviceContext(Guid.Empty, deviceName, platform, deviceType, "Windows 11", "1.0.0", DeviceCapability.All);
        return new CryptographicIdentityProvider(vault, ctx, new SystemTextJsonSerializer(), _clock);
    }

    private static X509Certificate2 CreateSigningCert(string subject)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));
    }

    private static byte[] CreateSignedApk(string packageId, long versionCode, X509Certificate2 cert)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifestEntry = zip.CreateEntry("AndroidManifest.xml");
            using var w = new StreamWriter(manifestEntry.Open(), new UTF8Encoding(false));
            w.Write($"""<manifest xmlns:android="http://schemas.android.com/apk/res/android" package="{packageId}" android:versionCode="{versionCode}" />""");
        }
        return ApkSigningBlockCodec.AttachApkV2SigningBlock(ms.ToArray(), cert);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 6000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition().ConfigureAwait(false))
            {
                return;
            }
            await Task.Delay(50).ConfigureAwait(false);
        }

        Assert.True(await condition().ConfigureAwait(false), $"Timed out waiting for condition ({timeoutMs}ms).");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDir))
        {
            try { Directory.Delete(_storageDir, recursive: true); } catch { }
        }
    }

    private sealed class EcosystemNode(
        string workDir,
        DeviceCryptographicIdentity identity,
        RealtimeCredentialBundle credentials,
        NexusPairingProtocolClient hostProtocol,
        NexusPairingProtocolClient clientProtocol,
        SignalRRealtimeClient realtime,
        BasicSyncOrchestrator syncOrchestrator,
        ClipboardSyncCoordinator clipboardService,
        NotesSyncCoordinator notesService,
        TransferManagerService transferManager,
        HttpIntegrationModulesClient modulesClient,
        HttpPanelAndAutomationClient panelClient,
        NexusUpdateService updateService) : IAsyncDisposable
    {
        public string WorkDir { get; } = workDir;
        public DeviceCryptographicIdentity Identity { get; } = identity;
        public RealtimeCredentialBundle Credentials { get; } = credentials;
        public SignalRRealtimeClient Realtime { get; } = realtime;
        public ClipboardSyncCoordinator ClipboardService { get; } = clipboardService;
        public NotesSyncCoordinator NotesService { get; } = notesService;
        public TransferManagerService TransferManager { get; } = transferManager;
        public HttpIntegrationModulesClient ModulesClient { get; } = modulesClient;
        public HttpPanelAndAutomationClient PanelClient { get; } = panelClient;
        public NexusUpdateService UpdateService { get; } = updateService;

        public async ValueTask DisposeAsync()
        {
            await TransferManager.DisposeAsync().ConfigureAwait(false);
            syncOrchestrator.Dispose();
            await Realtime.DisposeAsync().ConfigureAwait(false);
            clientProtocol.Dispose();
            hostProtocol.Dispose();
        }
    }

    private sealed class OrchestratorHarness(ServiceProvider provider, INexusRouteOrchestrator orchestrator) : IAsyncDisposable
    {
        public INexusRouteOrchestrator Orchestrator { get; } = orchestrator;
        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class DelegatingStubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }

    private sealed class FailingNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("Simulated unreachable interface");
    }

    private sealed class SimulatedNetworkAdapter : INetworkConnectivityAdapter
    {
        public NetworkEnvironmentSnapshot Snapshot { get; set; } = new()
        {
            IsNetworkAvailable = true,
            HasInternetAccess = true,
            HasLocalLanInterface = true,
            VpnStatus = VpnStatus.Connected
        };

        public NetworkEnvironmentSnapshot CaptureSnapshot() => Snapshot;
    }

    private sealed class StaticCredentialProvider(RealtimeCredentialBundle bundle) : IRealtimeCredentialProvider
    {
        public Task<Result<RealtimeCredentialBundle>> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeCredentialBundle>.Success(bundle));

        public Task<Result<RealtimeCredentialBundle>> RefreshCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeCredentialBundle>.Failure(NexusError.Unauthorized("Refresh unavailable")));
    }

    private sealed class StaticAppInspector(InstalledAppVersionInfo info) : IAppPackageInspector
    {
        public Task<InstalledAppVersionInfo> GetInstalledAppInfoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(info);
    }

    private sealed class NoOpInstaller : IAndroidUpdateInstaller
    {
        public Task<bool> CanRequestPackageInstallsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<Result> OpenUnknownSourcesSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());
        public Task<Result<UpdateInstallLaunchResult>> LaunchSystemPackageInstallerAsync(
            VerifiedUpdatePackage verifiedPackage,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result<UpdateInstallLaunchResult>.Success(new UpdateInstallLaunchResult
            {
                Launched = true,
                LocalApkPath = verifiedPackage.LocalApkPath,
                Message = "OK"
            }));
    }

    private sealed class EcosystemWebFactory(
        string storageDir,
        MutableClock clock,
        GitHubUpdateRelayOptions relayOptions) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nexus:Storage:RootDirectory"] = storageDir,
                    ["Nexus:Storage:DatabaseFileName"] = "nexus-e2e-server.db",
                    ["Nexus:Storage:DisablePooling"] = "true",
                    ["Nexus:Storage:EnablePooling"] = "false",
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
                services.RemoveAll<GitHubUpdateRelayOptions>();
                services.AddSingleton(relayOptions);
                services.RemoveAll<WindowsControlOptions>();
                services.AddSingleton(new WindowsControlOptions
                {
                    DryRunPowerActions = true,
                    SimulateInputInHeadlessTests = true,
                    DryRunApprovedAppLaunches = true
                });
            });
        }
    }
}
