using System.Collections.ObjectModel;
using Nexus.Contracts.Updates;
using Nexus.Core.Updates;
using Nexus.Mobile.Services.Mvvm;

namespace Nexus.Mobile.Services.ViewModels;

/// <summary>
/// ViewModel for NEXUS Update on Android/Mobile: displays installed version & signer fingerprint,
/// allows configuring public GitHub or private authenticated server relay (never storing PATs in APK),
/// checks SemVer updates, downloads with progress, verifies SHA-256 + APK signature,
/// and invokes Android installer ONLY after explicit user confirmation.
/// </summary>
public sealed class MobileUpdateViewModel : MobileObservableObject
{
    private readonly INexusUpdateService _updateService;

    private string _installedVersionText = "-";
    private string _installedPackageId = "com.nexus.ecosystem";
    private string _installedSignerText = "Indisponível";
    private bool _canRequestPackageInstalls;
    private string _permissionBadge = "Verificando permissão Android...";

    private string _repositoryOwner = "nexus-ecosystem";
    private string _repositoryName = "nexus";
    private UpdateRepositoryVisibility _selectedVisibility = UpdateRepositoryVisibility.PublicDirect;
    private bool _includePrereleases;
    private string _expectedSignerSha256 = string.Empty;

    private string _availabilityTitle = "Atualizações NEXUS";
    private string _availabilityBadge = "Não verificado";
    private string _releaseNotesText = "Consulte o GitHub Releases (ou relay seguro do Nexus.Server) para verificar novas versões assinadas.";
    private string _compatibilitySummary = string.Empty;
    private string _verificationBadgesText = "SHA-256, Package ID e certificado de assinatura são validados antes de qualquer instalação.";
    private double _downloadProgressFraction;
    private string _downloadStatusText = "Aguardando verificação";
    private bool _hasAvailableUpdate;
    private bool _hasVerifiedPackage;
    private bool _requiresPermissionPrompt;
    private bool _userConfirmedInstall;
    private string _feedbackMessage = string.Empty;

    private NexusReleaseManifest? _latestRelease;
    private VerifiedUpdatePackage? _verifiedPackage;

    public MobileUpdateViewModel(INexusUpdateService updateService)
    {
        _updateService = updateService;
        _updateService.ProgressChanged += (_, p) => ApplyProgress(p);

        InitializeCommand = new MobileAsyncCommand(InitializeAsync);
        SaveConfigurationCommand = new MobileAsyncCommand(SaveConfigurationAsync);
        CheckUpdatesCommand = new MobileAsyncCommand(CheckUpdatesAsync);
        DownloadUpdateCommand = new MobileAsyncCommand(DownloadUpdateAsync, () => _latestRelease is not null && HasAvailableUpdate);
        InstallUpdateCommand = new MobileAsyncCommand(InstallUpdateAsync, () => _verifiedPackage is not null && _verifiedPackage.IsReadyForUserInstall);
        OpenUnknownSourcesCommand = new MobileAsyncCommand(OpenUnknownSourcesAsync);
    }

    public IReadOnlyList<UpdateRepositoryVisibility> VisibilityModes { get; } = Enum.GetValues<UpdateRepositoryVisibility>();
    public ObservableCollection<UpdateHistoryRecord> History { get; } = [];

    public MobileAsyncCommand InitializeCommand { get; }
    public MobileAsyncCommand SaveConfigurationCommand { get; }
    public MobileAsyncCommand CheckUpdatesCommand { get; }
    public MobileAsyncCommand DownloadUpdateCommand { get; }
    public MobileAsyncCommand InstallUpdateCommand { get; }
    public MobileAsyncCommand OpenUnknownSourcesCommand { get; }

    public string InstalledVersionText { get => _installedVersionText; private set => SetProperty(ref _installedVersionText, value); }
    public string InstalledPackageId { get => _installedPackageId; private set => SetProperty(ref _installedPackageId, value); }
    public string InstalledSignerText { get => _installedSignerText; private set => SetProperty(ref _installedSignerText, value); }
    public bool CanRequestPackageInstalls { get => _canRequestPackageInstalls; private set => SetProperty(ref _canRequestPackageInstalls, value); }
    public string PermissionBadge { get => _permissionBadge; private set => SetProperty(ref _permissionBadge, value); }

    public string RepositoryOwner { get => _repositoryOwner; set => SetProperty(ref _repositoryOwner, value); }
    public string RepositoryName { get => _repositoryName; set => SetProperty(ref _repositoryName, value); }
    public UpdateRepositoryVisibility SelectedVisibility { get => _selectedVisibility; set => SetProperty(ref _selectedVisibility, value); }
    public bool IncludePrereleases { get => _includePrereleases; set => SetProperty(ref _includePrereleases, value); }
    public string ExpectedSignerSha256 { get => _expectedSignerSha256; set => SetProperty(ref _expectedSignerSha256, value); }

    public string AvailabilityTitle { get => _availabilityTitle; private set => SetProperty(ref _availabilityTitle, value); }
    public string AvailabilityBadge { get => _availabilityBadge; private set => SetProperty(ref _availabilityBadge, value); }
    public string ReleaseNotesText { get => _releaseNotesText; private set => SetProperty(ref _releaseNotesText, value); }
    public string CompatibilitySummary { get => _compatibilitySummary; private set => SetProperty(ref _compatibilitySummary, value); }
    public string VerificationBadgesText { get => _verificationBadgesText; private set => SetProperty(ref _verificationBadgesText, value); }
    public double DownloadProgressFraction { get => _downloadProgressFraction; private set => SetProperty(ref _downloadProgressFraction, value); }
    public string DownloadStatusText { get => _downloadStatusText; private set => SetProperty(ref _downloadStatusText, value); }

    public bool HasAvailableUpdate
    {
        get => _hasAvailableUpdate;
        private set
        {
            if (SetProperty(ref _hasAvailableUpdate, value))
            {
                DownloadUpdateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasVerifiedPackage
    {
        get => _hasVerifiedPackage;
        private set
        {
            if (SetProperty(ref _hasVerifiedPackage, value))
            {
                InstallUpdateCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool RequiresPermissionPrompt { get => _requiresPermissionPrompt; private set => SetProperty(ref _requiresPermissionPrompt, value); }
    public bool UserConfirmedInstall { get => _userConfirmedInstall; set => SetProperty(ref _userConfirmedInstall, value); }
    public string FeedbackMessage { get => _feedbackMessage; private set => SetProperty(ref _feedbackMessage, value); }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var installed = await _updateService.GetInstalledVersionAsync(cancellationToken);
        ApplyInstalled(installed);

        var config = await _updateService.GetConfigurationAsync(cancellationToken);
        RepositoryOwner = config.RepositoryOwner;
        RepositoryName = config.RepositoryName;
        SelectedVisibility = config.Visibility;
        IncludePrereleases = config.IncludePrereleases;
        ExpectedSignerSha256 = config.ExpectedSignerSha256 ?? string.Empty;

        await ReloadHistoryAsync(cancellationToken);
        FeedbackMessage = "Módulo Nexus Update pronto. Nenhuma credencial do GitHub é armazenada no APK.";
    }

    private async Task SaveConfigurationAsync(CancellationToken cancellationToken)
    {
        var current = await _updateService.GetConfigurationAsync(cancellationToken);
        var updated = current with
        {
            RepositoryOwner = RepositoryOwner,
            RepositoryName = RepositoryName,
            Visibility = SelectedVisibility,
            IncludePrereleases = IncludePrereleases,
            ExpectedSignerSha256 = string.IsNullOrWhiteSpace(ExpectedSignerSha256) ? null : ExpectedSignerSha256.Trim(),
            RequireUserConfirmation = true
        };

        var res = await _updateService.SaveConfigurationAsync(updated, cancellationToken);
        FeedbackMessage = res.IsSuccess
            ? $"Configuração de atualização salva ({res.Value.Visibility})."
            : res.Error!.Message;
    }

    private async Task CheckUpdatesAsync(CancellationToken cancellationToken)
    {
        await SaveConfigurationAsync(cancellationToken);
        _verifiedPackage = null;
        HasVerifiedPackage = false;
        UserConfirmedInstall = false;
        RequiresPermissionPrompt = false;

        var check = await _updateService.CheckForUpdatesAsync(cancellationToken);
        await ReloadHistoryAsync(cancellationToken);

        if (check.IsFailure)
        {
            HasAvailableUpdate = false;
            _latestRelease = null;
            DownloadUpdateCommand.NotifyCanExecuteChanged();
            AvailabilityBadge = "Falha na consulta";
            FeedbackMessage = check.Error!.Message;
            return;
        }

        var rep = check.Value;
        ApplyInstalled(rep.InstalledVersion);
        _latestRelease = rep.LatestRelease;
        HasAvailableUpdate = rep.IsUpdateAvailable && rep.IsCompatibleWithCurrentSigner;
        DownloadUpdateCommand.NotifyCanExecuteChanged();

        AvailabilityTitle = rep.LatestRelease is not null
            ? $"NEXUS v{rep.LatestRelease.Version} ({rep.LatestRelease.TagName})"
            : "Sem release disponível";
        AvailabilityBadge = rep.IsUpdateAvailable ? "Atualização disponível" : "Atualizado";
        ReleaseNotesText = rep.LatestRelease?.ReleaseNotes ?? string.Empty;
        CompatibilitySummary = rep.LatestRelease is not null
            ? $"Publicado: {rep.LatestRelease.PublishedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm} · Servidor mínimo: v{rep.LatestRelease.MinimumCompatibleServerVersion} · Tamanho: {rep.LatestRelease.ApkSizeBytes / 1024.0:F0} KB"
            : string.Empty;
        FeedbackMessage = rep.Message;
    }

    private async Task DownloadUpdateAsync(CancellationToken cancellationToken)
    {
        if (_latestRelease is null)
        {
            FeedbackMessage = "Consulte as atualizações antes de baixar.";
            return;
        }

        _verifiedPackage = null;
        HasVerifiedPackage = false;
        UserConfirmedInstall = false;

        var res = await _updateService.DownloadAndVerifyUpdateAsync(_latestRelease, cancellationToken);
        await ReloadHistoryAsync(cancellationToken);

        if (res.IsFailure)
        {
            DownloadProgressFraction = 0;
            FeedbackMessage = res.Error!.Message;
            return;
        }

        _verifiedPackage = res.Value;
        HasVerifiedPackage = _verifiedPackage.IsReadyForUserInstall;
        DownloadProgressFraction = 1.0;
        VerificationBadgesText = $"✓ HTTPS · ✓ SHA-256 ({_verifiedPackage.ActualSha256Hex[..12]}...) · ✓ Pacote ({_verifiedPackage.ActualPackageId}) · ✓ Assinatura ({_verifiedPackage.ActualSignerSha256[..12]}...)";
        FeedbackMessage = "APK verificado e pronto! Marque a confirmação explícita para abrir o instalador do Android.";
    }

    private async Task InstallUpdateAsync(CancellationToken cancellationToken)
    {
        if (_verifiedPackage is null)
        {
            FeedbackMessage = "Baixe e valide o APK antes de instalar.";
            return;
        }

        var res = await _updateService.LaunchUserConfirmedInstallAsync(_verifiedPackage, UserConfirmedInstall, cancellationToken);
        if (res.IsFailure)
        {
            FeedbackMessage = res.Error!.Message;
            return;
        }

        RequiresPermissionPrompt = res.Value.RequiresUnknownSourcesPermission;
        FeedbackMessage = res.Value.Message;
    }

    private async Task OpenUnknownSourcesAsync(CancellationToken cancellationToken)
    {
        var res = await _updateService.OpenUnknownSourcesPermissionSettingsAsync(cancellationToken);
        var installed = await _updateService.GetInstalledVersionAsync(cancellationToken);
        ApplyInstalled(installed);
        FeedbackMessage = res.IsSuccess
            ? "Ajustes do Android abertos para autorizar 'Instalar apps desconhecidos'."
            : res.Error!.Message;
    }

    private void ApplyInstalled(InstalledAppVersionInfo installed)
    {
        InstalledVersionText = $"v{installed.Version} (code {installed.VersionCode})";
        InstalledPackageId = installed.PackageId;
        InstalledSignerText = string.IsNullOrWhiteSpace(installed.SignerCertificateSha256)
            ? "Assinatura gerenciada pelo Android/configuração"
            : $"{installed.SignerCertificateSha256[..Math.Min(16, installed.SignerCertificateSha256.Length)]}...";
        CanRequestPackageInstalls = installed.CanRequestPackageInstalls;
        PermissionBadge = installed.CanRequestPackageInstalls
            ? "Permissão 'Instalar apps desconhecidos': Concedida"
            : installed.SupportsNativePackageInstaller
                ? "Permissão 'Instalar apps desconhecidos': Requer aprovação no Android"
                : "Ambiente host não-Android (verificação completa via teste/relay)";
    }

    private void ApplyProgress(UpdateDownloadProgress progress)
    {
        DownloadProgressFraction = progress.PercentComplete / 100.0;
        DownloadStatusText = progress.StatusMessage;
    }

    private async Task ReloadHistoryAsync(CancellationToken cancellationToken)
    {
        var hist = await _updateService.GetHistoryAsync(25, cancellationToken);
        if (hist.IsSuccess)
        {
            History.Clear();
            foreach (var item in hist.Value)
            {
                History.Add(item);
            }
        }
    }
}

