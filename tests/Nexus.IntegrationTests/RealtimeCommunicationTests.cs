using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Nexus.Contracts.Commands;
using Nexus.Contracts.Common;
using Nexus.Contracts.Connections;
using Nexus.Contracts.Devices;
using Nexus.Contracts.Events;
using Nexus.Contracts.Messaging;
using Nexus.Contracts.Pairing;
using Nexus.Contracts.Security;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Devices;
using Nexus.Core.Errors;
using Nexus.Core.Events;
using Nexus.Core.Services;
using Nexus.Core.State;
using Nexus.Infrastructure.Connectivity;
using Nexus.Infrastructure.Security;
using Nexus.Security.Credentials;
using Nexus.Security.Identity;
using Nexus.Security.Tokens;
using Xunit;

namespace Nexus.IntegrationTests;

public sealed class RealtimeCommunicationTests : IDisposable
{
    private readonly string _storageDir = Path.Combine(Path.GetTempPath(), "nexus-rt-int-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new(DateTimeOffset.UtcNow);
    private readonly CustomRealtimeFactory _factory;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public RealtimeCommunicationTests()
    {
        _factory = new CustomRealtimeFactory(_storageDir, _clock);
    }

    [Fact]
    public async Task SignalR_RejectsAnonymous_AndAuthenticatedTransitionsThroughConnectingAuthenticatingConnected()
    {
        using var http = _factory.CreateClient();
        var anonConnection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/nexus", o =>
            {
                o.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
            })
            .Build();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => anonConnection.StartAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        await anonConnection.DisposeAsync();

        var host = CreateCryptoProvider("host-1", "NEXUS Host", DevicePlatform.Windows, DeviceType.Desktop);
        var mobile = CreateCryptoProvider("mob-1", "Pixel 9 Auth", DevicePlatform.Android, DeviceType.Phone);
        var paired = await PairDeviceAsync(http, host, mobile, NexusPermission.All);

        var credProvider = new InMemoryCredentialProvider(
            new RealtimeCredentialBundle(
                paired.ClientIdentity.DeviceId,
                paired.ClientIdentity.CustomName,
                paired.Tokens.AccessToken,
                paired.Tokens.AccessTokenExpiresAtUtc,
                "http://localhost",
                null));

        await using var client = CreateRealtimeClient(paired.ClientIdentity.DeviceId, credProvider);
        var observedStates = new List<ConnectionState>();
        client.StateChanged += (_, snap) => observedStates.Add(snap.State);

        var openRes = await client.OpenAuthenticatedAsync();
        Assert.True(openRes.IsSuccess, openRes.Error?.Message);
        Assert.True(client.IsConnected);
        Assert.Equal(ConnectionState.Connected, client.State);
        Assert.Equal(
            [ConnectionState.Connecting, ConnectionState.Authenticating, ConnectionState.Connected],
            observedStates);

        var pingRes = await client.SendHeartbeatAsync();
        Assert.True(pingRes.IsSuccess);
    }

    [Fact]
    public async Task SimultaneousWindowsAndAndroid_ExchangeMessagesCommandsEventsAndAcks()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("host-sim", "NEXUS Root", DevicePlatform.Windows, DeviceType.Desktop);
        var desktopCrypto = CreateCryptoProvider("win-sim", "Windows Studio", DevicePlatform.Windows, DeviceType.Desktop);
        var mobileCrypto = CreateCryptoProvider("and-sim", "Galaxy S25", DevicePlatform.Android, DeviceType.Phone);

        var desktopPaired = await PairDeviceAsync(http, host, desktopCrypto, NexusPermission.All);
        var mobilePaired = await PairDeviceAsync(http, host, mobileCrypto, NexusPermission.All);

        await using var desktopClient = CreateRealtimeClient(
            desktopPaired.ClientIdentity.DeviceId,
            new InMemoryCredentialProvider(ToBundle(desktopPaired)));
        await using var mobileClient = CreateRealtimeClient(
            mobilePaired.ClientIdentity.DeviceId,
            new InMemoryCredentialProvider(ToBundle(mobilePaired)));

        Assert.True((await desktopClient.OpenAuthenticatedAsync()).IsSuccess);
        Assert.True((await mobileClient.OpenAuthenticatedAsync()).IsSuccess);

        var presence = (await desktopClient.RefreshPresenceAsync()).Value;
        var dPres = Assert.Single(presence, p => p.DeviceId == desktopPaired.ClientIdentity.DeviceId);
        var mPres = Assert.Single(presence, p => p.DeviceId == mobilePaired.ClientIdentity.DeviceId);
        Assert.True(dPres.IsRealtimeConnected);
        Assert.True(mPres.IsRealtimeConnected);
        Assert.Equal(ConnectionState.Connected, dPres.ConnectionState);
        Assert.Equal(ConnectionState.Connected, mPres.ConnectionState);

        var mobileMsgTcs = new TaskCompletionSource<RealtimeMessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        mobileClient.MessageReceived += (_, env) => mobileMsgTcs.TrySetResult(env);

        var correlationId = Guid.CreateVersion7();
        var envelope = new RealtimeMessageEnvelope
        {
            MessageType = "nexus.clipboard.sync",
            Kind = RealtimeMessageKind.Custom,
            SourceDeviceId = desktopPaired.ClientIdentity.DeviceId,
            TargetDeviceId = mobilePaired.ClientIdentity.DeviceId,
            CorrelationId = correlationId,
            PayloadJson = "{\"text\":\"hello-nexus\"}",
            RequiresAck = true,
            TimestampUtc = _clock.UtcNow
        };

        var ackRes = await desktopClient.SendEnvelopeAsync(envelope, waitForAck: true);
        Assert.True(ackRes.IsSuccess, ackRes.Error?.Message);
        Assert.Equal(RealtimeAckStatus.Processed, ackRes.Value.Status);
        Assert.Equal(mobilePaired.ClientIdentity.DeviceId, ackRes.Value.RespondingDeviceId);
        Assert.Equal(correlationId, ackRes.Value.CorrelationId);

        var deliveredEnv = await mobileMsgTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("{\"text\":\"hello-nexus\"}", deliveredEnv.PayloadJson);

        // Bidirectional Command + Result + Integration Event
        var desktopCmdTcs = new TaskCompletionSource<DeviceCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mobileResultTcs = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        desktopClient.CommandReceived += (_, cmd) => desktopCmdTcs.TrySetResult(cmd);
        mobileClient.CommandResultReceived += (_, r) => mobileResultTcs.TrySetResult(r);

        var command = new DeviceCommand
        {
            SourceDeviceId = mobilePaired.ClientIdentity.DeviceId,
            TargetDeviceId = desktopPaired.ClientIdentity.DeviceId,
            CommandName = "windows.open_url",
            Arguments = new Dictionary<string, string> { ["url"] = "https://nexus.local" },
            IssuedAtUtc = _clock.UtcNow
        };

        var cmdAck = await mobileClient.SendCommandAsync(command, waitForAck: true);
        Assert.True(cmdAck.IsSuccess, cmdAck.Error?.Message);
        var recvCmd = await desktopCmdTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(command.CommandId, recvCmd.CommandId);

        var execResult = new CommandExecutionResult
        {
            CommandId = command.CommandId,
            TargetDeviceId = command.TargetDeviceId,
            State = CommandExecutionState.Succeeded,
            Output = "{\"opened\":true}"
        };
        Assert.True((await desktopClient.SendCommandResultAsync(execResult, mobilePaired.ClientIdentity.DeviceId, command.CorrelationId)).IsSuccess);
        var recvResult = await mobileResultTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CommandExecutionState.Succeeded, recvResult.State);

        var mobileEvtTcs = new TaskCompletionSource<RealtimeMessageEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        mobileClient.MessageReceived += (_, env) =>
        {
            if (env.Kind == RealtimeMessageKind.Event)
            {
                mobileEvtTcs.TrySetResult(env);
            }
        };

        var evtAck = await desktopClient.PublishIntegrationEventAsync(new IntegrationEvent
        {
            EventType = NexusEventTypes.SyncCompleted,
            SourceDeviceId = desktopPaired.ClientIdentity.DeviceId,
            PayloadJson = "{\"items\":3}",
            OccurredAtUtc = _clock.UtcNow
        }, mobilePaired.ClientIdentity.DeviceId);
        Assert.True(evtAck.IsSuccess);
        var recvEvt = await mobileEvtTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(NexusEventTypes.SyncCompleted, recvEvt.MessageType);
    }

    [Fact]
    public async Task DisconnectedDevice_ReportedAsOfflineInPresence_AndSendIsRejected()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("host-off", "Host", DevicePlatform.Windows, DeviceType.Desktop);
        var winCrypto = CreateCryptoProvider("win-off", "Win Online", DevicePlatform.Windows, DeviceType.Desktop);
        var offlineMobileCrypto = CreateCryptoProvider("mob-off", "Pixel Registered Offline", DevicePlatform.Android, DeviceType.Phone);

        var winPaired = await PairDeviceAsync(http, host, winCrypto, NexusPermission.All);
        var offlinePaired = await PairDeviceAsync(http, host, offlineMobileCrypto, NexusPermission.All);

        await using var winClient = CreateRealtimeClient(
            winPaired.ClientIdentity.DeviceId,
            new InMemoryCredentialProvider(ToBundle(winPaired)));
        Assert.True((await winClient.OpenAuthenticatedAsync()).IsSuccess);

        // Registered/paired device has NOT opened SignalR: presence MUST mark it disconnected/offline.
        var presence = (await winClient.RefreshPresenceAsync()).Value;
        var offlinePresence = Assert.Single(presence, p => p.DeviceId == offlinePaired.ClientIdentity.DeviceId);
        Assert.Equal(DeviceTrustStatus.Authorized, offlinePresence.TrustStatus);
        Assert.False(offlinePresence.IsRealtimeConnected);
        Assert.Equal(ConnectionState.Disconnected, offlinePresence.ConnectionState);
        Assert.Equal("Offline", offlinePresence.RouteSummary);

        var sendMsgRes = await winClient.SendEnvelopeAsync(new RealtimeMessageEnvelope
        {
            MessageType = "nexus.test.offline",
            SourceDeviceId = winPaired.ClientIdentity.DeviceId,
            TargetDeviceId = offlinePaired.ClientIdentity.DeviceId,
            PayloadJson = "{\"ping\":1}",
            TimestampUtc = _clock.UtcNow
        });

        Assert.True(sendMsgRes.IsFailure);
        Assert.Equal("TARGET_DEVICE_OFFLINE", sendMsgRes.Error!.Code);

        var sendCmdRes = await winClient.SendCommandAsync(new DeviceCommand
        {
            SourceDeviceId = winPaired.ClientIdentity.DeviceId,
            TargetDeviceId = offlinePaired.ClientIdentity.DeviceId,
            CommandName = "android.wake",
            IssuedAtUtc = _clock.UtcNow
        });

        Assert.True(sendCmdRes.IsFailure);
        Assert.Equal("TARGET_DEVICE_OFFLINE", sendCmdRes.Error!.Code);
    }

    [Fact]
    public async Task InvalidDuplicateAndOutOfOrderMessages_AreFilteredAndReorderedThroughHub()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("host-ord", "Host", DevicePlatform.Windows, DeviceType.Desktop);
        var winCrypto = CreateCryptoProvider("win-ord", "Win Sender", DevicePlatform.Windows, DeviceType.Desktop);
        var mobCrypto = CreateCryptoProvider("mob-ord", "Mobile Receiver", DevicePlatform.Android, DeviceType.Phone);

        var winPaired = await PairDeviceAsync(http, host, winCrypto, NexusPermission.All);
        var mobPaired = await PairDeviceAsync(http, host, mobCrypto, NexusPermission.All);

        await using var winClient = CreateRealtimeClient(winPaired.ClientIdentity.DeviceId, new InMemoryCredentialProvider(ToBundle(winPaired)));
        await using var mobClient = CreateRealtimeClient(mobPaired.ClientIdentity.DeviceId, new InMemoryCredentialProvider(ToBundle(mobPaired)));

        Assert.True((await winClient.OpenAuthenticatedAsync()).IsSuccess);
        Assert.True((await mobClient.OpenAuthenticatedAsync()).IsSuccess);

        // 1. Invalid envelope rejected before network/dispatch
        var invalidRes = await winClient.SendEnvelopeAsync(new RealtimeMessageEnvelope
        {
            MessageType = "X",
            SourceDeviceId = winPaired.ClientIdentity.DeviceId,
            TargetDeviceId = Guid.Empty,
            SequenceNumber = -5,
            PayloadJson = "{}"
        });
        Assert.True(invalidRes.IsFailure);
        Assert.Equal(ErrorCategory.Validation, invalidRes.Error!.Category);

        // 2. Out-of-order [2, 1] + Duplicate [2]: receiver MUST get strictly [1, 2] once
        var receivedSequences = new List<long>();
        var twoReceivedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mobClient.MessageReceived += (_, env) =>
        {
            lock (receivedSequences)
            {
                receivedSequences.Add(env.SequenceNumber);
                if (receivedSequences.Count >= 2)
                {
                    twoReceivedTcs.TrySetResult();
                }
            }
        };

        var msg1Id = Guid.CreateVersion7();
        var msg2Id = Guid.CreateVersion7();

        var env2 = new RealtimeMessageEnvelope
        {
            MessageId = msg2Id,
            MessageType = "nexus.ordered.step",
            SourceDeviceId = winPaired.ClientIdentity.DeviceId,
            TargetDeviceId = mobPaired.ClientIdentity.DeviceId,
            SequenceNumber = 2,
            PayloadJson = "{\"step\":2}",
            RequiresAck = false,
            TimestampUtc = _clock.UtcNow
        };
        var env1 = new RealtimeMessageEnvelope
        {
            MessageId = msg1Id,
            MessageType = "nexus.ordered.step",
            SourceDeviceId = winPaired.ClientIdentity.DeviceId,
            TargetDeviceId = mobPaired.ClientIdentity.DeviceId,
            SequenceNumber = 1,
            PayloadJson = "{\"step\":1}",
            RequiresAck = false,
            TimestampUtc = _clock.UtcNow
        };

        var ack2 = await winClient.SendEnvelopeAsync(env2, waitForAck: false);
        Assert.True(ack2.IsSuccess);
        Assert.Equal(RealtimeAckStatus.OutOfOrderBuffered, ack2.Value.Status);

        var dupAck2 = await winClient.SendEnvelopeAsync(env2, waitForAck: false);
        Assert.True(dupAck2.IsSuccess);
        Assert.Equal(RealtimeAckStatus.DuplicateIgnored, dupAck2.Value.Status);

        var ack1 = await winClient.SendEnvelopeAsync(env1, waitForAck: false);
        Assert.True(ack1.IsSuccess);

        await twoReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100); // Ensure duplicate was not also delivered after drain

        lock (receivedSequences)
        {
            Assert.Equal(new long[] { 1, 2 }, receivedSequences);
        }
    }

    [Fact]
    public async Task CommandCancellation_CancelsTrackedOperationInTargetDevice()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("host-can", "Host", DevicePlatform.Windows, DeviceType.Desktop);
        var winCrypto = CreateCryptoProvider("win-can", "Win", DevicePlatform.Windows, DeviceType.Desktop);
        var mobCrypto = CreateCryptoProvider("mob-can", "Mob", DevicePlatform.Android, DeviceType.Phone);

        var winPaired = await PairDeviceAsync(http, host, winCrypto, NexusPermission.All);
        var mobPaired = await PairDeviceAsync(http, host, mobCrypto, NexusPermission.All);

        await using var winClient = CreateRealtimeClient(winPaired.ClientIdentity.DeviceId, new InMemoryCredentialProvider(ToBundle(winPaired)));
        await using var mobClient = CreateRealtimeClient(mobPaired.ClientIdentity.DeviceId, new InMemoryCredentialProvider(ToBundle(mobPaired)));

        Assert.True((await winClient.OpenAuthenticatedAsync()).IsSuccess);
        Assert.True((await mobClient.OpenAuthenticatedAsync()).IsSuccess);

        var tokenCancelledTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var desktopResultTcs = new TaskCompletionSource<CommandExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        winClient.CommandResultReceived += (_, r) => desktopResultTcs.TrySetResult(r);

        mobClient.CommandReceived += (_, cmd) =>
        {
            var token = mobClient.TrackIncomingCommand(cmd.CommandId, TimeSpan.FromSeconds(20));
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                    tokenCancelledTcs.TrySetResult(false);
                }
                catch (OperationCanceledException)
                {
                    tokenCancelledTcs.TrySetResult(true);
                    await mobClient.SendCommandResultAsync(new CommandExecutionResult
                    {
                        CommandId = cmd.CommandId,
                        TargetDeviceId = cmd.TargetDeviceId,
                        State = CommandExecutionState.Cancelled,
                        Error = new ContractError("CANCELLED", "Cancelado remotamente pelo usuário")
                    }, cmd.SourceDeviceId, cmd.CorrelationId);
                }
            });
        };

        var cmd = new DeviceCommand
        {
            SourceDeviceId = winPaired.ClientIdentity.DeviceId,
            TargetDeviceId = mobPaired.ClientIdentity.DeviceId,
            CommandName = "long.backup",
            IssuedAtUtc = _clock.UtcNow
        };

        Assert.True((await winClient.SendCommandAsync(cmd, waitForAck: true)).IsSuccess);
        Assert.True((await winClient.CancelCommandAsync(cmd.CommandId, mobPaired.ClientIdentity.DeviceId, "Cancelar agora")).IsSuccess);

        Assert.True(await tokenCancelledTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        var res = await desktopResultTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(CommandExecutionState.Cancelled, res.State);
    }

    [Fact]
    public async Task UnstableNetwork_TemporaryOutage_TransitionsToReconnectingAndAutoRecovers()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("host-flk", "Host", DevicePlatform.Windows, DeviceType.Desktop);
        var mobCrypto = CreateCryptoProvider("mob-flk", "Mob Unstable", DevicePlatform.Android, DeviceType.Phone);
        var mobPaired = await PairDeviceAsync(http, host, mobCrypto, NexusPermission.All);

        var networkController = new OutageController(_factory.Server);
        var states = new ConcurrentQueue<ConnectionState>();
        var reconnectingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawInitialConnected = false;

        var options = new RealtimeClientOptions
        {
            AutoReconnect = true,
            ReconnectBaseDelay = TimeSpan.FromMilliseconds(80),
            ReconnectMaxDelay = TimeSpan.FromMilliseconds(250),
            SignalRMessageHandlerFactory = (_, _) => networkController.CreateHandler()
        };

        await using var client = CreateRealtimeClient(
            mobPaired.ClientIdentity.DeviceId,
            new InMemoryCredentialProvider(ToBundle(mobPaired)),
            options);

        client.StateChanged += (_, s) =>
        {
            states.Enqueue(s.State);
            if (s.State == ConnectionState.Connected && !sawInitialConnected)
            {
                sawInitialConnected = true;
            }
            else if (s.State == ConnectionState.Reconnecting)
            {
                reconnectingTcs.TrySetResult();
            }
            else if (s.State == ConnectionState.Connected && reconnectingTcs.Task.IsCompleted)
            {
                recoveredTcs.TrySetResult();
            }
        };

        Assert.True((await client.OpenAuthenticatedAsync()).IsSuccess);

        // Simulate temporary internet loss
        networkController.SetOutage(true);
        _ = client.SendHeartbeatAsync(); // Force active transport error if poll hasn't turned over yet

        await reconnectingTcs.Task.WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(ConnectionState.Reconnecting, client.State);

        // Restore connectivity and expect automatic recovery to Connected
        networkController.SetOutage(false);
        await recoveredTcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.True(client.IsConnected);
        Assert.Equal(ConnectionState.Connected, client.State);

        var postRecoveryPing = await client.SendHeartbeatAsync();
        Assert.True(postRecoveryPing.IsSuccess);
    }

    [Fact]
    public async Task ExpiredAuthentication_RefreshesValidlyOrFaultsWhenUnrefreshableOrRevoked()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("host-exp", "Host", DevicePlatform.Windows, DeviceType.Desktop);
        var mob1 = CreateCryptoProvider("mob-exp-ok", "Mob Refresh", DevicePlatform.Android, DeviceType.Phone);
        var mob2 = CreateCryptoProvider("mob-exp-bad", "Mob Expired", DevicePlatform.Android, DeviceType.Phone);

        var pairedOk = await PairDeviceAsync(http, host, mob1, NexusPermission.All);
        var pairedBad = await PairDeviceAsync(http, host, mob2, NexusPermission.All);

        // Advance server + test clock past 15-min AccessToken TTL while RefreshToken (30 days) remains valid
        _clock.Advance(TimeSpan.FromMinutes(30));

        var refreshableProvider = new RefreshingTestCredentialProvider(
            pairedOk.ClientIdentity.DeviceId,
            pairedOk.ClientIdentity.CustomName,
            pairedOk.Tokens,
            "http://localhost",
            async () =>
            {
                var proto = new NexusPairingProtocolClient(mob1, new SecureTokenGenerator(), new SystemTextJsonSerializer(), _clock);
                var reqRes = await proto.BuildRefreshRequestAsync(pairedOk.Tokens.RefreshToken);
                var resp = await http.PostAsJsonAsync("/api/v1/auth/refresh", reqRes.Value, JsonOptions);
                resp.EnsureSuccessStatusCode();
                var env = await resp.Content.ReadFromJsonAsync<NexusResponse<DeviceAuthTokenPair>>(JsonOptions);
                return env!.Payload!;
            });

        await using var clientRefreshed = CreateRealtimeClient(pairedOk.ClientIdentity.DeviceId, refreshableProvider);
        var refreshedStates = new List<ConnectionState>();
        clientRefreshed.StateChanged += (_, s) => refreshedStates.Add(s.State);

        var openOk = await clientRefreshed.OpenAuthenticatedAsync();
        Assert.True(openOk.IsSuccess, openOk.Error?.Message);
        Assert.True(refreshableProvider.RefreshCallCount >= 1);
        Assert.Contains(ConnectionState.Authenticating, refreshedStates);
        Assert.Equal(ConnectionState.Connected, clientRefreshed.State);

        // Case B: expired & non-refreshable credentials MUST fail into Faulted and NEVER Connected
        var nonRefreshingProvider = new InMemoryCredentialProvider(
            new RealtimeCredentialBundle(
                pairedBad.ClientIdentity.DeviceId,
                pairedBad.ClientIdentity.CustomName,
                pairedBad.Tokens.AccessToken,
                pairedBad.Tokens.AccessTokenExpiresAtUtc, // already expired relative to _clock!
                "http://localhost",
                null),
            failRefresh: true);

        await using var clientFailed = CreateRealtimeClient(pairedBad.ClientIdentity.DeviceId, nonRefreshingProvider);
        var failedStates = new List<ConnectionState>();
        clientFailed.StateChanged += (_, s) => failedStates.Add(s.State);

        var openBad = await clientFailed.OpenAuthenticatedAsync();
        Assert.True(openBad.IsFailure);
        Assert.False(clientFailed.IsConnected);
        Assert.Equal(ConnectionState.Faulted, clientFailed.State);
        Assert.DoesNotContain(ConnectionState.Connected, failedStates);
    }

    [Fact]
    public async Task HttpBulkTransferChannel_UploadsLargePayloadAndVerifiesSha256Receipt()
    {
        using var http = _factory.CreateClient();
        var host = CreateCryptoProvider("host-blk", "Host", DevicePlatform.Windows, DeviceType.Desktop);
        var mob = CreateCryptoProvider("mob-blk", "Mobile Bulk", DevicePlatform.Android, DeviceType.Phone);
        var paired = await PairDeviceAsync(http, host, mob, NexusPermission.All);

        var protocolClient = new NexusPairingProtocolClient(
            mob,
            new SecureTokenGenerator(),
            new SystemTextJsonSerializer(),
            _clock,
            customHttpClientFactory: (_, _) => _factory.CreateClient());

        var bulkClient = new HttpBulkTransferClient(
            protocolClient,
            new InMemoryCredentialProvider(ToBundle(paired)),
            NullLogger<HttpBulkTransferClient>.Instance);

        var largePayload = new byte[256 * 1024];
        Random.Shared.NextBytes(largePayload);
        var expectedSha = Convert.ToHexString(SHA256.HashData(largePayload));

        var hostIdentity = (await host.GetOrCreateIdentityAsync()).Value;
        var receipt = await bulkClient.UploadLargePayloadAsync(
            largePayload,
            "application/octet-stream",
            hostIdentity.DeviceId);

        Assert.True(receipt.IsSuccess, receipt.Error?.Message);
        Assert.Equal(largePayload.Length, receipt.Value.ByteLength);
        Assert.Equal(expectedSha, receipt.Value.Sha256Hex);
        Assert.Equal(paired.ClientIdentity.DeviceId, receipt.Value.SourceDeviceId);
        Assert.Equal(hostIdentity.DeviceId, receipt.Value.TargetDeviceId);
    }

    private SignalRRealtimeClient CreateRealtimeClient(
        Guid localDeviceId,
        IRealtimeCredentialProvider credentialProvider,
        RealtimeClientOptions? options = null)
    {
        var clock = _clock;
        var eventBus = new InProcessEventBus();
        var serializer = new SystemTextJsonSerializer();
        var ctx = new DefaultLocalDeviceContext(
            localDeviceId,
            "Test Client",
            DevicePlatform.Windows,
            DeviceType.Desktop,
            "Windows 11",
            "0.5.0",
            DeviceCapability.All);
        var stateMgr = new InMemoryStateManager<NexusRuntimeState>(new NexusRuntimeState
        {
            LocalDevice = ctx.ToDeviceInfo(),
            UpdatedAtUtc = clock.UtcNow
        });
        var connMgr = new ConnectionStateService(ctx, clock, eventBus, stateMgr);
        var pipeline = new RealtimeMessagePipeline(clock);

        var effectiveOptions = options ?? new RealtimeClientOptions
        {
            AutoReconnect = false,
            DefaultAckTimeout = TimeSpan.FromSeconds(5),
            SignalRMessageHandlerFactory = (_, _) => _factory.Server.CreateHandler()
        };

        return new SignalRRealtimeClient(
            credentialProvider,
            connMgr,
            pipeline,
            eventBus,
            serializer,
            clock,
            effectiveOptions,
            NullLogger<SignalRRealtimeClient>.Instance);
    }

    private CryptographicIdentityProvider CreateCryptoProvider(
        string folderName,
        string deviceName,
        DevicePlatform platform,
        DeviceType deviceType)
    {
        var vault = new PlatformSecureCredentialStore(new SecureCredentialStoreOptions
        {
            VaultDirectory = Path.Combine(_storageDir, "rt-vaults", folderName)
        });
        var ctx = new DefaultLocalDeviceContext(
            Guid.Empty,
            deviceName,
            platform,
            deviceType,
            platform == DevicePlatform.Windows ? "Windows 11" : "Android 15",
            "0.5.0",
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

        var confirmReq = (await hostProtocol.BuildConfirmRequestAsync(qr.PairingId, approve: true, grantedPermissions)).Value;
        var confirmResp = await client.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/confirm", confirmReq, JsonOptions);
        confirmResp.EnsureSuccessStatusCode();

        var finalizeReq = (await clientProtocol.BuildFinalizeRequestAsync(qr.PairingId)).Value;
        var finalizeResp = await client.PostAsJsonAsync($"/api/v1/pairing/sessions/{qr.PairingId:D}/finalize", finalizeReq, JsonOptions);
        finalizeResp.EnsureSuccessStatusCode();
        var tokens = (await finalizeResp.Content.ReadFromJsonAsync<NexusResponse<DeviceAuthTokenPair>>(JsonOptions))!.Payload!;

        var clientIdentity = (await clientCrypto.GetOrCreateIdentityAsync()).Value;
        return new PairedFlowResult(clientIdentity, tokens);
    }

    private static RealtimeCredentialBundle ToBundle(PairedFlowResult paired) => new(
        paired.ClientIdentity.DeviceId,
        paired.ClientIdentity.CustomName,
        paired.Tokens.AccessToken,
        paired.Tokens.AccessTokenExpiresAtUtc,
        "http://localhost",
        null);

    public void Dispose()
    {
        _factory.Dispose();
        if (Directory.Exists(_storageDir))
        {
            try { Directory.Delete(_storageDir, recursive: true); } catch { }
        }
    }

    private sealed record PairedFlowResult(DeviceCryptographicIdentity ClientIdentity, DeviceAuthTokenPair Tokens);

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;
        public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
    }

    private sealed class InMemoryCredentialProvider(RealtimeCredentialBundle bundle, bool failRefresh = false) : IRealtimeCredentialProvider
    {
        public Task<Result<RealtimeCredentialBundle>> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeCredentialBundle>.Success(bundle));

        public Task<Result<RealtimeCredentialBundle>> RefreshCredentialsAsync(CancellationToken cancellationToken = default)
            => failRefresh
                ? Task.FromResult(Result<RealtimeCredentialBundle>.Failure(NexusError.Unauthorized("Refresh disabled or expired.", code: "REALTIME_AUTH_EXPIRED")))
                : Task.FromResult(Result<RealtimeCredentialBundle>.Success(bundle));
    }

    private sealed class RefreshingTestCredentialProvider(
        Guid deviceId,
        string deviceName,
        DeviceAuthTokenPair initialTokens,
        string endpoint,
        Func<Task<DeviceAuthTokenPair>> refresher) : IRealtimeCredentialProvider
    {
        private DeviceAuthTokenPair _current = initialTokens;
        public int RefreshCallCount { get; private set; }

        public Task<Result<RealtimeCredentialBundle>> GetCredentialsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Result<RealtimeCredentialBundle>.Success(new RealtimeCredentialBundle(
                deviceId, deviceName, _current.AccessToken, _current.AccessTokenExpiresAtUtc, endpoint, null)));

        public async Task<Result<RealtimeCredentialBundle>> RefreshCredentialsAsync(CancellationToken cancellationToken = default)
        {
            RefreshCallCount++;
            _current = await refresher().ConfigureAwait(false);
            return Result<RealtimeCredentialBundle>.Success(new RealtimeCredentialBundle(
                deviceId, deviceName, _current.AccessToken, _current.AccessTokenExpiresAtUtc, endpoint, null));
        }
    }

    private sealed class OutageController(Microsoft.AspNetCore.TestHost.TestServer server)
    {
        private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inFlight = new();
        private volatile bool _outage;

        public HttpMessageHandler CreateHandler() => new OutageDelegatingHandler(this, server.CreateHandler());

        public void SetOutage(bool outage)
        {
            _outage = outage;
            if (outage)
            {
                foreach (var key in _inFlight.Keys.ToArray())
                {
                    if (_inFlight.TryRemove(key, out var cts))
                    {
                        try { cts.Cancel(); } catch { }
                        cts.Dispose();
                    }
                }
            }
        }

        private sealed class OutageDelegatingHandler(OutageController owner, HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (owner._outage)
                {
                    throw new HttpRequestException("Simulated network outage.");
                }

                var id = Guid.NewGuid();
                var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                owner._inFlight[id] = linked;
                try
                {
                    var response = await base.SendAsync(request, linked.Token).ConfigureAwait(false);
                    if (response.Content.Headers.ContentType?.MediaType?.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        var originalStream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                        response.Content = new OutageStreamContent(owner, originalStream, response.Content.Headers, linked);
                    }
                    else
                    {
                        owner._inFlight.TryRemove(id, out _);
                        linked.Dispose();
                    }

                    return response;
                }
                catch (OperationCanceledException) when (owner._outage && !cancellationToken.IsCancellationRequested)
                {
                    owner._inFlight.TryRemove(id, out _);
                    linked.Dispose();
                    throw new HttpRequestException("Simulated stream drop during network outage.");
                }
                catch
                {
                    owner._inFlight.TryRemove(id, out _);
                    linked.Dispose();
                    throw;
                }
            }
        }

        private sealed class OutageStreamContent : HttpContent
        {
            private readonly OutageController _owner;
            private readonly Stream _inner;
            private readonly CancellationTokenSource _linked;

            public OutageStreamContent(
                OutageController owner,
                Stream inner,
                HttpContentHeaders sourceHeaders,
                CancellationTokenSource linked)
            {
                _owner = owner;
                _inner = inner;
                _linked = linked;
                foreach (var h in sourceHeaders)
                {
                    Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            protected override Task<Stream> CreateContentReadStreamAsync()
                => Task.FromResult<Stream>(new OutageAwareStream(_owner, _inner, _linked.Token));

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
                => _inner.CopyToAsync(stream, _linked.Token);

            protected override bool TryComputeLength(out long length)
            {
                length = -1;
                return false;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _linked.Dispose();
                    _inner.Dispose();
                }
                base.Dispose(disposing);
            }
        }

        private sealed class OutageAwareStream(OutageController owner, Stream inner, CancellationToken token) : Stream
        {
            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => 0; set => throw new NotSupportedException(); }
            public override void Flush() => inner.Flush();
            public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (owner._outage)
                {
                    throw new IOException("Simulated temporary internet loss on SSE stream.");
                }

                using var combined = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                try
                {
                    return await inner.ReadAsync(buffer, combined.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (owner._outage && !cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("Simulated temporary internet loss on SSE stream.");
                }
            }
        }
    }

    private sealed class CustomRealtimeFactory(string storageDir, MutableClock clock) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Nexus:Storage:RootDirectory"] = storageDir,
                    ["Nexus:Storage:DatabaseFileName"] = "nexus-server-rt.db",
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
            });
        }
    }
}
