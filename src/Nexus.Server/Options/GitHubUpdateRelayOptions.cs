namespace Nexus.Server.Options;

/// <summary>
/// Server-side configuration for relaying GitHub Releases (especially from private repositories)
/// to paired NEXUS Android devices without ever embedding GitHub credentials inside the APK.
/// </summary>
public sealed class GitHubUpdateRelayOptions
{
    public string RepositoryOwner { get; set; } = "nexus-ecosystem";
    public string RepositoryName { get; set; } = "nexus";
    public string GitHubApiBaseUrl { get; set; } = "https://api.github.com";
    public string TokenEnvironmentVariable { get; set; } = "NEXUS_GITHUB_RELEASE_TOKEN";
    public bool IncludePrereleases { get; set; }
    public string ExpectedPackageId { get; set; } = "com.nexus.ecosystem";
    public string? ExpectedSignerSha256 { get; set; }
    public string MinimumCompatibleServerVersion { get; set; } = "1.0.0";
    public TimeSpan MetadataCacheDuration { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Optional server-side secret resolver override (used in tests or external secret stores).
    /// NEVER expose this value to client responses.
    /// </summary>
    public Func<string?>? ServerTokenResolver { get; set; }

    /// <summary>
    /// Optional HTTP message handler factory for outgoing server-to-GitHub calls in integration tests.
    /// </summary>
    public Func<HttpMessageHandler>? OutboundGitHubHandlerFactory { get; set; }

    public string? ResolveToken()
    {
        var fromDelegate = ServerTokenResolver?.Invoke();
        if (!string.IsNullOrWhiteSpace(fromDelegate))
        {
            return fromDelegate.Trim();
        }

        if (!string.IsNullOrWhiteSpace(TokenEnvironmentVariable))
        {
            var envVal = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(envVal))
            {
                return envVal.Trim();
            }
        }

        return null;
    }
}

