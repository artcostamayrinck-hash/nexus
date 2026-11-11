using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Nexus.Contracts.Automation;
using Nexus.Core.Automation;
using Nexus.Core.Errors;
using Nexus.Infrastructure.Modules;

namespace Nexus.Infrastructure.Automation;

/// <summary>
/// Safely launches only explicitly approved Windows application shortcuts configured by the host user.
/// Rejects arbitrary paths outside the configured allowlist or missing executables.
/// </summary>
public sealed class WindowsApprovedAppLauncher : IApprovedAppLauncher
{
    private readonly WindowsControlOptions _options;
    private readonly ILogger<WindowsApprovedAppLauncher> _logger;

    public WindowsApprovedAppLauncher(
        WindowsControlOptions options,
        ILogger<WindowsApprovedAppLauncher> logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool IsSupportedOnCurrentOs => OperatingSystem.IsWindows() || _options.DryRunApprovedAppLaunches;

    public Task<Result<string>> LaunchApprovedAppAsync(
        ApprovedAppShortcut shortcut,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shortcut);

        var errors = shortcut.Validate().ToArray();
        if (errors.Length > 0)
        {
            return Task.FromResult(Result<string>.Failure(
                NexusError.Validation(errors[0].Message, code: errors[0].Code)));
        }

        if (_options.DryRunApprovedAppLaunches)
        {
            return Task.FromResult(Result<string>.Success(
                $"Aplicativo aprovado '{shortcut.DisplayName}' validado (modo seguro de simulação/teste)."));
        }

        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(Result<string>.Failure(
                NexusError.Forbidden("Execução de atalhos de aplicativo disponível apenas no host Windows.", "OS_NOT_SUPPORTED")));
        }

        var path = shortcut.ExecutablePath.Trim();
        if (!Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains('/'))
        {
            // Named shell command in System32/PATH must end with .exe and not contain shell operators
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.IndexOfAny(['&', '|', ';', '>', '<']) >= 0)
            {
                return Task.FromResult(Result<string>.Failure(
                    NexusError.Validation("Caminho de aplicativo inválido para execução segura.", code: "APP_PATH_INVALID")));
            }
        }
        else if (!File.Exists(path))
        {
            return Task.FromResult(Result<string>.Failure(
                NexusError.NotFound($"Executável não encontrado: '{path}'.", "APP_EXECUTABLE_NOT_FOUND")));
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                Arguments = shortcut.Arguments ?? string.Empty,
                UseShellExecute = true
            };
            Process.Start(psi);
            _logger.LogInformation("Launched approved app shortcut {ShortcutId} ({Name})", shortcut.ShortcutId, shortcut.DisplayName);
            return Task.FromResult(Result<string>.Success($"Aplicativo '{shortcut.DisplayName}' iniciado."));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed launching approved app {ShortcutId}", shortcut.ShortcutId);
            return Task.FromResult(Result<string>.Failure(
                NexusError.Unexpected($"Falha ao iniciar '{shortcut.DisplayName}': {ex.Message}", "APP_LAUNCH_FAILED")));
        }
    }
}

