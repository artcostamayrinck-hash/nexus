using Nexus.Core.Errors;

namespace Nexus.Security.Validation;

/// <summary>
/// Provides defensive input sanitization for file paths, identifiers, and endpoints.
/// </summary>
public static class InputSanitizer
{
    private static readonly HashSet<string> AllowedEndpointSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http",
        "https",
        "ws",
        "wss"
    };

    public static Result<string> SanitizeRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return Result<string>.Failure(NexusError.Validation("Path cannot be empty.", code: "PATH_EMPTY"));
        }

        var normalized = relativePath.Trim().Replace('\\', '/');
        if (normalized.Contains('\0') || Path.IsPathRooted(normalized) || normalized.StartsWith('/'))
        {
            return Result<string>.Failure(NexusError.Validation("Rooted or null-byte paths are not permitted.", code: "PATH_UNSAFE"));
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
        {
            return Result<string>.Failure(NexusError.Validation("Path traversal segments are not permitted.", code: "PATH_TRAVERSAL"));
        }

        return Result<string>.Success(Path.Combine(segments));
    }

    public static Result<Uri> ValidateEndpointUri(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return Result<Uri>.Failure(NexusError.Validation("Endpoint URI cannot be empty.", code: "ENDPOINT_EMPTY"));
        }

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri) || !AllowedEndpointSchemes.Contains(uri.Scheme))
        {
            return Result<Uri>.Failure(NexusError.Validation("Endpoint must be an absolute HTTP/HTTPS/WS/WSS URI.", code: "ENDPOINT_INVALID"));
        }

        return Result<Uri>.Success(uri);
    }
}

