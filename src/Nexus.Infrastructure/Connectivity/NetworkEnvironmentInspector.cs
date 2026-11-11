using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Nexus.Contracts.Connections;
using Nexus.Core.Abstractions;
using Nexus.Core.Connections;
using Nexus.Core.Events;

namespace Nexus.Infrastructure.Connectivity;

/// <summary>
/// Reads active OS network interfaces, classifies LAN vs Tailscale CGNAT/MagicDNS interfaces,
/// and surfaces Tailscale/VPN readiness without requiring administrative Tailscale control APIs.
/// </summary>
public sealed class SystemNetworkConnectivityAdapter : INetworkConnectivityAdapter
{
    private readonly IClock _clock;

    public SystemNetworkConnectivityAdapter(IClock clock)
    {
        _clock = clock;
    }

    public NetworkEnvironmentSnapshot CaptureSnapshot()
    {
        var lanAddresses = new List<string>();
        var tailscaleAddresses = new List<string>();
        var summaries = new List<string>();

        bool hasNetwork = NetworkInterface.GetIsNetworkAvailable();
        bool hasLan = false;
        bool isCellular = false;
        bool hasTailscaleInterface = false;
        bool hasTailscaleDown = false;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            var isTailscaleNamed = IsTailscaleInterfaceName(nic.Name, nic.Description);
            if (nic.OperationalStatus != OperationalStatus.Up)
            {
                if (isTailscaleNamed)
                {
                    hasTailscaleDown = true;
                }
                continue;
            }

            var ipProps = nic.GetIPProperties();
            var unicast = ipProps.UnicastAddresses;
            var isCellNic = nic.NetworkInterfaceType is NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2
                || nic.Description.Contains("cellular", StringComparison.OrdinalIgnoreCase)
                || nic.Name.Contains("rmnet", StringComparison.OrdinalIgnoreCase)
                || nic.Name.Contains("ccmni", StringComparison.OrdinalIgnoreCase);

            var nicHasTailscaleIp = false;
            foreach (var addr in unicast)
            {
                var ip = addr.Address;
                if (IPAddress.IsLoopback(ip))
                {
                    continue;
                }

                if (NetworkAddressClassifier.IsTailscaleAddress(ip))
                {
                    nicHasTailscaleIp = true;
                    hasTailscaleInterface = true;
                    tailscaleAddresses.Add(ip.ToString());
                }
                else if (ip.AddressFamily == AddressFamily.InterNetwork && NetworkAddressClassifier.IsPrivateLanIpv4(ip))
                {
                    if (!isCellNic)
                    {
                        hasLan = true;
                        lanAddresses.Add(ip.ToString());
                    }
                }
            }

            if (isTailscaleNamed || nicHasTailscaleIp)
            {
                hasTailscaleInterface = true;
                summaries.Add($"{nic.Name} [Tailscale/VPN UP]");
                continue;
            }

            if (isCellNic && unicast.Any(u => !IPAddress.IsLoopback(u.Address)))
            {
                isCellular = true;
                summaries.Add($"{nic.Name} [Dados Móveis]");
            }
            else if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
            {
                var typeTag = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
                summaries.Add($"{nic.Name} [{typeTag}]");
            }
        }

        var vpnStatus = hasTailscaleInterface
            ? VpnStatus.Connected
            : hasTailscaleDown ? VpnStatus.Disconnected : VpnStatus.Unavailable;

        return new NetworkEnvironmentSnapshot
        {
            IsNetworkAvailable = hasNetwork || lanAddresses.Count > 0 || tailscaleAddresses.Count > 0 || isCellular,
            HasInternetAccess = hasNetwork,
            HasLocalLanInterface = hasLan || lanAddresses.Count > 0,
            IsCellularActive = isCellular,
            VpnStatus = vpnStatus,
            VpnProvider = hasTailscaleInterface || hasTailscaleDown ? "Tailscale" : null,
            LocalIpv4Addresses = lanAddresses.Distinct(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly(),
            TailscaleAddresses = tailscaleAddresses.Distinct(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly(),
            InterfaceSummaries = summaries.Distinct(StringComparer.Ordinal).ToList().AsReadOnly(),
            CapturedAtUtc = _clock.UtcNow
        };
    }

    private static bool IsTailscaleInterfaceName(string name, string description)
    {
        return name.Contains("tailscale", StringComparison.OrdinalIgnoreCase)
            || description.Contains("tailscale", StringComparison.OrdinalIgnoreCase)
            || name.Contains("tailscale0", StringComparison.OrdinalIgnoreCase);
    }
}

public static class NetworkAddressClassifier
{
    public static ConnectionRouteKind ClassifyEndpoint(string endpointUrl)
    {
        if (!Uri.TryCreate(endpointUrl?.Trim(), UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            return ConnectionRouteKind.Unknown;
        }

        var host = uri.Host.Trim('[', ']');
        if (IsTailscaleHostOrAddress(host))
        {
            return ConnectionRouteKind.RemoteVpnTailscale;
        }

        if (IsLocalLanHostOrAddress(host))
        {
            return ConnectionRouteKind.LocalLan;
        }

        return ConnectionRouteKind.RemotePrivate;
    }

    public static bool IsTailscaleHostOrAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            return IsTailscaleAddress(ip);
        }

        return false;
    }

    public static bool IsLocalLanHostOrAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip))
            {
                return true;
            }

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                return IsPrivateLanIpv4(ip);
            }

            if (ip.IsIPv6LinkLocal)
            {
                return true;
            }
        }

        // Unqualified single hostname like "desktop-casa"
        return !host.Contains('.', StringComparison.Ordinal);
    }

    /// <summary>
    /// Tailscale uses CGNAT 100.64.0.0/10 (100.64.x.x - 100.127.x.x) and IPv6 ULA fd7a:115c:a1e0::/48.
    /// </summary>
    public static bool IsTailscaleAddress(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (ip.TryWriteBytes(bytes, out _))
            {
                return bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
            }
        }
        else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Span<byte> bytes = stackalloc byte[16];
            if (ip.TryWriteBytes(bytes, out _))
            {
                return bytes[0] == 0xFD && bytes[1] == 0x7A && bytes[2] == 0x11 && bytes[3] == 0x5C && bytes[4] == 0xA1 && bytes[5] == 0xE0;
            }
        }

        return false;
    }

    public static bool IsPrivateLanIpv4(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        Span<byte> b = stackalloc byte[4];
        if (!ip.TryWriteBytes(b, out _))
        {
            return false;
        }

        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }
}

public sealed class NetworkEnvironmentInspector : INetworkEnvironmentInspector
{
    private readonly INetworkConnectivityAdapter _adapter;
    private readonly IEventBus _eventBus;
    private readonly Lock _sync = new();
    private NetworkEnvironmentSnapshot _currentSnapshot;

    public NetworkEnvironmentInspector(INetworkConnectivityAdapter adapter, IEventBus eventBus)
    {
        _adapter = adapter;
        _eventBus = eventBus;
        _currentSnapshot = adapter.CaptureSnapshot();
    }

    public NetworkEnvironmentSnapshot CurrentSnapshot
    {
        get
        {
            lock (_sync)
            {
                return _currentSnapshot;
            }
        }
    }

    public event EventHandler<NetworkEnvironmentSnapshot>? NetworkEnvironmentChanged;

    public async Task<NetworkEnvironmentSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var next = _adapter.CaptureSnapshot();

        NetworkEnvironmentSnapshot previous;
        bool changed;
        lock (_sync)
        {
            previous = _currentSnapshot;
            changed = previous.VpnStatus != next.VpnStatus
                || previous.HasLocalLanInterface != next.HasLocalLanInterface
                || previous.IsCellularActive != next.IsCellularActive
                || previous.HasInternetAccess != next.HasInternetAccess
                || previous.IsNetworkAvailable != next.IsNetworkAvailable;
            _currentSnapshot = next;
        }

        if (previous.VpnStatus != next.VpnStatus)
        {
            await _eventBus.PublishAsync(
                new VpnStatusChangedDomainEvent(previous.VpnStatus, next.VpnStatus, next.VpnProvider),
                cancellationToken).ConfigureAwait(false);
        }

        if (changed)
        {
            NetworkEnvironmentChanged?.Invoke(this, next);
        }

        return next;
    }

    public ConnectionRouteKind ClassifyEndpoint(string endpointUrl)
        => NetworkAddressClassifier.ClassifyEndpoint(endpointUrl);

    public bool IsTailscaleHostOrAddress(string host)
        => NetworkAddressClassifier.IsTailscaleHostOrAddress(host);

    public bool IsLocalLanHostOrAddress(string host)
        => NetworkAddressClassifier.IsLocalLanHostOrAddress(host);
}

