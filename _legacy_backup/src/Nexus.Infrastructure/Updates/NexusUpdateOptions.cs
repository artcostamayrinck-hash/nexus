using Nexus.Contracts.Updates;

namespace Nexus.Infrastructure.Updates;

/// <summary>
/// Options governing GitHub Releases queries, HTTPS origin checks, and optional test HTTP handlers.
/// Notice that client-side options NEVER store GitHub Personal Access Tokens.
/// </summary>
public sealed class NexusUpdateOptions
{
    public string DefaultRepositoryOwner { get; set; } = "nexus-ecosystem";
    public string DefaultRepositoryName { get; set; } = "nexus";
    public string DefaultPackageId { get; set; } = "com.nexus.ecosystem";
    public string? DefaultExpectedSignerSha256 { get; set; }
    public string MinimumCompatibleServerVersion { get; set; } = "1.0.0";
    public TimeSpan HttpTimeout { get; set; } = TimeSpan.FromSeconds(45);
    public string StagingFolderName { get; set; } = Path.Combine("updates", "staging");
    public string VerifiedFolderName { get; set; } = Path.Combine("updates", "verified");

    /// <summary>
    /// Optional HTTP message handler factory for direct GitHub HTTPS calls (used in automated tests).
    /// </summary>
    public Func<HttpMessageHandler>? PublicGitHubHandlerFactory { get; set; }

    /// <summary>
    /// Additional trusted HTTPS hosts beyond standard GitHub hosts (e.g., an enterprise GitHub host).
    /// </summary>
    public HashSet<string> TrustedReleaseHosts { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com",
        "github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "codeload.github.com"
    };

    public UpdateConfiguration CreateDefaultConfiguration() => new()
    {
        RepositoryOwner = DefaultRepositoryOwner,
        RepositoryName = DefaultRepositoryName,
        Visibility = UpdateRepositoryVisibility.PublicDirect,
        IncludePrereleases = false,
        GitHubApiBaseUrl = "https://api.github.com",
        ExpectedPackageId = DefaultPackageId,
        ExpectedSignerSha256 = DefaultExpectedSignerSha256,
        CheckAutomatically = true,
        RequireUserConfirmation = true
    };
}

