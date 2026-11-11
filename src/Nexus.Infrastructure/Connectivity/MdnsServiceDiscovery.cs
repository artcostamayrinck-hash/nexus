using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Connections;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Errors;
using Nexus.Core.Events;

namespace Nexus.Infrastructure.Connectivity;

/// <summary>
/// Abstraction over UDP multicast packet I/O for mDNS so discovery can run over real LAN sockets
/// or a deterministic in-memory transport in unit/integration tests.
/// </summary>
public interface IMdnsPacketTransport : IAsyncDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default);
    Task<ReadOnlyMemory<byte>?> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    ValueTask StartListeningAsync(CancellationToken cancellationToken = default);
}

public sealed class UdpMdnsPacketTransport : IMdnsPacketTransport
{
    public static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    public const int MulticastPort = 5353;

    private readonly object _sync = new();
    private UdpClient? _client;
    private bool _disposed;

    public ValueTask StartListeningAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureClient();
        return ValueTask.CompletedTask;
    }

    public async Task SendAsync(ReadOnlyMemory<byte> datagram, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = EnsureClient();
        var endpoint = new IPEndPoint(MulticastAddress, MulticastPort);
        await client.SendAsync(datagram, endpoint, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReadOnlyMemory<byte>?> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var client = EnsureClient();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);

        try
        {
            var result = await client.ReceiveAsync(linkedCts.Token).ConfigureAwait(false);
            return result.Buffer;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private UdpClient EnsureClient()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client is not null)
            {
                return _client;
            }

            var udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
            udp.MulticastLoopback = true;
            try
            {
                udp.JoinMulticastGroup(MulticastAddress);
            }
            catch (SocketException)
            {
                // Still allow best-effort send/receive if multicast join fails on restricted host adapters.
            }

            _client = udp;
            return _client;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            _client?.Dispose();
            _client = null;
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// RFC 6762 / RFC 6763 binary mDNS wire packet codec for `_nexus._tcp.local.` PTR/SRV/TXT/A records.
/// </summary>
public static class MdnsPacketCodec
{
    public const string NexusServiceType = "_nexus._tcp.local.";
    private const ushort ClassIn = 1;
    private const ushort TypeA = 1;
    private const ushort TypePtr = 12;
    private const ushort TypeTxt = 16;
    private const ushort TypeSrv = 33;

    public static byte[] EncodePtrQuery(string serviceType = NexusServiceType)
    {
        using var ms = new MemoryStream(128);
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header[4..6], 1); // QDCOUNT = 1
        ms.Write(header);

        WriteDnsName(ms, serviceType);
        Span<byte> qtail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(qtail[0..2], TypePtr);
        BinaryPrimitives.WriteUInt16BigEndian(qtail[2..4], ClassIn);
        ms.Write(qtail);
        return ms.ToArray();
    }

    public static bool IsQueryForService(ReadOnlySpan<byte> packet, string serviceType = NexusServiceType)
    {
        if (packet.Length < 12)
        {
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]);
        if ((flags & 0x8000) != 0)
        {
            return false; // Response, not a query
        }

        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(packet[4..6]);
        var offset = 12;
        var expected = NormalizeDomain(serviceType);

        for (var i = 0; i < qdCount && offset < packet.Length; i++)
        {
            if (!TryReadDnsName(packet, ref offset, out var name) || offset + 4 > packet.Length)
            {
                return false;
            }

            var qtype = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset, 2));
            offset += 4;
            if ((qtype == TypePtr || qtype == 255) &&
                string.Equals(NormalizeDomain(name), expected, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static byte[] EncodeAnnouncement(MdnsDiscoveredEndpoint endpoint, IPAddress? ipv4Address = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var instanceLabel = SanitizeDnsLabel(string.IsNullOrWhiteSpace(endpoint.ServiceName)
            ? "nexus-host"
            : endpoint.ServiceName);
        var hostLabel = SanitizeDnsLabel(string.IsNullOrWhiteSpace(endpoint.HostName)
            ? instanceLabel
            : endpoint.HostName.Replace(".local", string.Empty, StringComparison.OrdinalIgnoreCase));

        var instanceFqdn = $"{instanceLabel}.{NexusServiceType}";
        var hostFqdn = $"{hostLabel}.local.";

        var txtPairs = new Dictionary<string, string>(endpoint.TxtProperties, StringComparer.OrdinalIgnoreCase)
        {
            ["v"] = endpoint.ContractVersion,
            ["id"] = endpoint.ServiceDeviceId.ToString("D"),
            ["name"] = endpoint.ServiceName,
            ["url"] = endpoint.EndpointUrl,
            ["port"] = endpoint.Port.ToString()
        };

        if (!string.IsNullOrWhiteSpace(endpoint.TlsThumbprint))
        {
            txtPairs["tls"] = endpoint.TlsThumbprint;
        }

        if (!string.IsNullOrWhiteSpace(endpoint.HostKeyFingerprint))
        {
            txtPairs["fp"] = endpoint.HostKeyFingerprint;
        }

        var targetIp = ipv4Address;
        if (targetIp is null && Uri.TryCreate(endpoint.EndpointUrl, UriKind.Absolute, out var uri))
        {
            IPAddress.TryParse(uri.Host, out targetIp);
        }

        ushort answerCount = targetIp is { AddressFamily: AddressFamily.InterNetwork } ? (ushort)4 : (ushort)3;

        using var ms = new MemoryStream(512);
        Span<byte> header = stackalloc byte[12];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header[2..4], 0x8400); // Standard authoritative response
        BinaryPrimitives.WriteUInt16BigEndian(header[6..8], answerCount);
        ms.Write(header);

        // 1. PTR: _nexus._tcp.local. -> <instance>._nexus._tcp.local.
        var ptrRdata = BuildDnsNameBytes(instanceFqdn);
        WriteResourceRecord(ms, NexusServiceType, TypePtr, ClassIn, 120, ptrRdata);

        // 2. SRV: <instance>._nexus._tcp.local. -> target <host>.local.:port
        var hostNameBytes = BuildDnsNameBytes(hostFqdn);
        var srvRdata = new byte[6 + hostNameBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(srvRdata.AsSpan(0, 2), 0); // priority
        BinaryPrimitives.WriteUInt16BigEndian(srvRdata.AsSpan(2, 2), 0); // weight
        BinaryPrimitives.WriteUInt16BigEndian(srvRdata.AsSpan(4, 2), (ushort)Math.Clamp(endpoint.Port, 1, 65535));
        hostNameBytes.CopyTo(srvRdata, 6);
        WriteResourceRecord(ms, instanceFqdn, TypeSrv, ClassIn | 0x8000, 120, srvRdata);

        // 3. TXT: <instance>._nexus._tcp.local.
        using var txtMs = new MemoryStream();
        foreach (var pair in txtPairs)
        {
            var entry = Encoding.UTF8.GetBytes($"{pair.Key}={pair.Value}");
            if (entry.Length is > 0 and <= 255)
            {
                txtMs.WriteByte((byte)entry.Length);
                txtMs.Write(entry);
            }
        }

        WriteResourceRecord(ms, instanceFqdn, TypeTxt, ClassIn | 0x8000, 120, txtMs.ToArray());

        // 4. A record when IPv4 is available
        if (targetIp is { AddressFamily: AddressFamily.InterNetwork })
        {
            WriteResourceRecord(ms, hostFqdn, TypeA, ClassIn | 0x8000, 120, targetIp.GetAddressBytes());
        }

        return ms.ToArray();
    }

    public static bool TryDecodeAnnouncement(
        ReadOnlySpan<byte> packet,
        DateTimeOffset nowUtc,
        out MdnsDiscoveredEndpoint? endpoint)
    {
        endpoint = null;
        if (packet.Length < 12)
        {
            return false;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]);
        if ((flags & 0x8000) == 0)
        {
            return false; // Not a response
        }

        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(packet[4..6]);
        var anCount = BinaryPrimitives.ReadUInt16BigEndian(packet[6..8]);
        var nsCount = BinaryPrimitives.ReadUInt16BigEndian(packet[8..10]);
        var arCount = BinaryPrimitives.ReadUInt16BigEndian(packet[10..12]);

        var offset = 12;
        for (var i = 0; i < qdCount; i++)
        {
            if (!TryReadDnsName(packet, ref offset, out _) || offset + 4 > packet.Length)
            {
                return false;
            }
            offset += 4;
        }

        string? ptrInstance = null;
        string? srvTarget = null;
        int srvPort = 0;
        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var aRecords = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);

        var totalRrs = anCount + nsCount + arCount;
        for (var i = 0; i < totalRrs && offset < packet.Length; i++)
        {
            if (!TryReadDnsName(packet, ref offset, out var rrName) || offset + 10 > packet.Length)
            {
                return false;
            }

            var rrType = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset, 2));
            offset += 2; // type
            offset += 2; // class
            offset += 4; // ttl
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset, 2));
            offset += 2;

            if (offset + rdLength > packet.Length)
            {
                return false;
            }

            var rdataOffset = offset;
            if (rrType == TypePtr &&
                string.Equals(NormalizeDomain(rrName), NormalizeDomain(NexusServiceType), StringComparison.OrdinalIgnoreCase))
            {
                if (TryReadDnsName(packet, ref rdataOffset, out var pointed))
                {
                    ptrInstance = pointed;
                }
            }
            else if (rrType == TypeSrv && rdLength >= 6)
            {
                srvPort = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 4, 2));
                var targetOffset = offset + 6;
                if (TryReadDnsName(packet, ref targetOffset, out var target))
                {
                    srvTarget = target;
                }
            }
            else if (rrType == TypeTxt)
            {
                var pos = offset;
                var end = offset + rdLength;
                while (pos < end)
                {
                    var len = packet[pos++];
                    if (pos + len > end)
                    {
                        break;
                    }

                    var raw = Encoding.UTF8.GetString(packet.Slice(pos, len));
                    pos += len;
                    var eq = raw.IndexOf('=', StringComparison.Ordinal);
                    if (eq > 0)
                    {
                        txt[raw[..eq]] = raw[(eq + 1)..];
                    }
                }
            }
            else if (rrType == TypeA && rdLength == 4)
            {
                aRecords[NormalizeDomain(rrName)] = new IPAddress(packet.Slice(offset, 4));
            }

            offset += rdLength;
        }

        if (ptrInstance is null && !txt.ContainsKey("url"))
        {
            return false;
        }

        txt.TryGetValue("url", out var url);
        var host = srvTarget ?? "nexus-host.local.";
        var normalizedHost = NormalizeDomain(host);

        if (string.IsNullOrWhiteSpace(url))
        {
            var resolvedIp = aRecords.TryGetValue(normalizedHost, out var ip) ? ip.ToString() : normalizedHost;
            var scheme = txt.TryGetValue("tls", out var tlsVal) && !string.IsNullOrWhiteSpace(tlsVal) ? "https" : "http";
            var effectivePort = srvPort > 0 ? srvPort : (txt.TryGetValue("port", out var pStr) && int.TryParse(pStr, out var p) ? p : 7443);
            url = $"{scheme}://{resolvedIp}:{effectivePort}";
        }

        var deviceId = txt.TryGetValue("id", out var idStr) && Guid.TryParse(idStr, out var parsedId)
            ? parsedId
            : Guid.Empty;

        var serviceName = txt.TryGetValue("name", out var nameStr) && !string.IsNullOrWhiteSpace(nameStr)
            ? nameStr
            : ptrInstance?.Replace("." + NormalizeDomain(NexusServiceType), string.Empty, StringComparison.OrdinalIgnoreCase) ?? "NEXUS Host";

        endpoint = new MdnsDiscoveredEndpoint
        {
            ServiceDeviceId = deviceId,
            ServiceName = serviceName,
            HostName = normalizedHost,
            EndpointUrl = url,
            Port = srvPort > 0 ? srvPort : 7443,
            TlsThumbprint = txt.TryGetValue("tls", out var thumb) ? thumb : null,
            HostKeyFingerprint = txt.TryGetValue("fp", out var fp) ? fp : null,
            TxtProperties = txt,
            DiscoveredAtUtc = nowUtc
        };

        return DeviceEndpointRecord.IsValidHttpOrWsUri(endpoint.EndpointUrl);
    }

    private static void WriteResourceRecord(
        Stream stream,
        string name,
        ushort type,
        ushort dnsClass,
        uint ttlSeconds,
        ReadOnlySpan<byte> rdata)
    {
        WriteDnsName(stream, name);
        Span<byte> meta = stackalloc byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(meta[0..2], type);
        BinaryPrimitives.WriteUInt16BigEndian(meta[2..4], dnsClass);
        BinaryPrimitives.WriteUInt32BigEndian(meta[4..8], ttlSeconds);
        BinaryPrimitives.WriteUInt16BigEndian(meta[8..10], (ushort)rdata.Length);
        stream.Write(meta);
        stream.Write(rdata);
    }

    private static byte[] BuildDnsNameBytes(string fqdn)
    {
        using var ms = new MemoryStream(64);
        WriteDnsName(ms, fqdn);
        return ms.ToArray();
    }

    private static void WriteDnsName(Stream stream, string fqdn)
    {
        var labels = fqdn.Trim('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        foreach (var label in labels)
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            var len = (byte)Math.Min(bytes.Length, 63);
            stream.WriteByte(len);
            stream.Write(bytes, 0, len);
        }

        stream.WriteByte(0);
    }

    private static bool TryReadDnsName(ReadOnlySpan<byte> packet, ref int offset, out string name)
    {
        var labels = new List<string>();
        var current = offset;
        var jumped = false;
        var hops = 0;

        while (current < packet.Length && hops++ < 32)
        {
            var len = packet[current];
            if (len == 0)
            {
                if (!jumped)
                {
                    offset = current + 1;
                }

                name = string.Join('.', labels);
                return true;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (current + 1 >= packet.Length)
                {
                    name = string.Empty;
                    return false;
                }

                var pointer = ((len & 0x3F) << 8) | packet[current + 1];
                if (!jumped)
                {
                    offset = current + 2;
                    jumped = true;
                }

                current = pointer;
                continue;
            }

            current++;
            if (current + len > packet.Length)
            {
                name = string.Empty;
                return false;
            }

            labels.Add(Encoding.UTF8.GetString(packet.Slice(current, len)));
            current += len;
            if (!jumped)
            {
                offset = current;
            }
        }

        name = string.Empty;
        return false;
    }

    private static string NormalizeDomain(string domain)
        => domain.Trim().TrimEnd('.').ToLowerInvariant();

    private static string SanitizeDnsLabel(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
            {
                sb.Append(ch);
            }
            else if (ch == ' ')
            {
                sb.Append('-');
            }
        }

        var result = sb.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(result) ? "nexus-host" : result[..Math.Min(result.Length, 50)];
    }
}

/// <summary>
/// mDNS advertiser and browser implementing `_nexus._tcp.local.` service discovery.
/// Safe on restricted firewalls/sandboxes: logs and falls back cleanly without breaking manual/Tailscale routes.
/// </summary>
public sealed class MdnsServiceDiscovery : IMdnsServiceDiscovery
{
    private readonly IMdnsPacketTransport _transport;
    private readonly IClock _clock;
    private readonly IEventBus _eventBus;
    private readonly ILogger<MdnsServiceDiscovery> _logger;
    private readonly Lock _sync = new();

    private CancellationTokenSource? _advertiseCts;
    private Task? _advertiseTask;
    private MdnsDiscoveredEndpoint? _currentAnnouncement;
    private bool _disposed;

    public MdnsServiceDiscovery(
        IMdnsPacketTransport transport,
        IClock clock,
        IEventBus eventBus,
        ILogger<MdnsServiceDiscovery> logger)
    {
        _transport = transport;
        _clock = clock;
        _eventBus = eventBus;
        _logger = logger;
    }

    public bool IsAdvertising
    {
        get
        {
            lock (_sync)
            {
                return _advertiseTask is { IsCompleted: false };
            }
        }
    }

    public async Task<Result<IReadOnlyList<MdnsDiscoveredEndpoint>>> DiscoverAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var scanWindow = timeout ?? TimeSpan.FromMilliseconds(1200);
        if (scanWindow <= TimeSpan.Zero)
        {
            scanWindow = TimeSpan.FromMilliseconds(250);
        }

        var discovered = new ConcurrentDictionary<string, MdnsDiscoveredEndpoint>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await _transport.StartListeningAsync(cancellationToken).ConfigureAwait(false);
            var query = MdnsPacketCodec.EncodePtrQuery();
            await _transport.SendAsync(query, cancellationToken).ConfigureAwait(false);

            var deadline = _clock.UtcNow + scanWindow;
            while (_clock.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
            {
                var remaining = deadline - _clock.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                var packet = await _transport.ReceiveAsync(
                    remaining < TimeSpan.FromMilliseconds(350) ? remaining : TimeSpan.FromMilliseconds(350),
                    cancellationToken).ConfigureAwait(false);

                if (packet is null || packet.Value.Length == 0)
                {
                    continue;
                }

                if (MdnsPacketCodec.TryDecodeAnnouncement(packet.Value.Span, _clock.UtcNow, out var endpoint) && endpoint is not null)
                {
                    var key = $"{endpoint.ServiceDeviceId:N}|{endpoint.EndpointUrl}";
                    if (discovered.TryAdd(key, endpoint))
                    {
                        await _eventBus.PublishAsync(new EndpointDiscoveredDomainEvent(endpoint), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }

            return Result<IReadOnlyList<MdnsDiscoveredEndpoint>>.Success(discovered.Values.ToList().AsReadOnly());
        }
        catch (OperationCanceledException)
        {
            return Result<IReadOnlyList<MdnsDiscoveredEndpoint>>.Failure(NexusError.Cancelled());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mDNS LAN discovery failed gracefully; manual/Tailscale routes remain available.");
            return Result<IReadOnlyList<MdnsDiscoveredEndpoint>>.Failure(
                NexusError.Network("Descoberta local mDNS indisponível neste adaptador de rede.", "MDNS_DISCOVERY_UNAVAILABLE", ex.Message, ex));
        }
    }

    public async Task<Result> StartAdvertisingAsync(
        MdnsDiscoveredEndpoint announcement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(announcement);
        await StopAdvertisingAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _transport.StartListeningAsync(cancellationToken).ConfigureAwait(false);
            var payload = MdnsPacketCodec.EncodeAnnouncement(announcement);

            var cts = new CancellationTokenSource();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _currentAnnouncement = announcement;
                _advertiseCts = cts;
                _advertiseTask = Task.Run(() => RunResponderLoopAsync(payload, cts.Token), cts.Token);
            }

            // Send initial unsolicited announcement burst as recommended by RFC 6762 section 8.3
            await _transport.SendAsync(payload, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("mDNS service advertised: {ServiceName} -> {Endpoint}", announcement.ServiceName, announcement.EndpointUrl);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Failure(NexusError.Cancelled());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to bind mDNS multicast responder on port 5353.");
            return Result.Failure(NexusError.Network("Não foi possível iniciar o anúncio mDNS na rede local.", "MDNS_ADVERTISE_FAILED", ex.Message, ex));
        }
    }

    private async Task RunResponderLoopAsync(byte[] announcementBytes, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var packet = await _transport.ReceiveAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                if (packet is not null && MdnsPacketCodec.IsQueryForService(packet.Value.Span))
                {
                    await _transport.SendAsync(announcementBytes, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Transient mDNS responder error; continuing.");
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task StopAdvertisingAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? cts;
        Task? task;

        lock (_sync)
        {
            cts = _advertiseCts;
            task = _advertiseTask;
            _advertiseCts = null;
            _advertiseTask = null;
            _currentAnnouncement = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        if (task is not null)
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Shutdown tolerance
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        await StopAdvertisingAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}

