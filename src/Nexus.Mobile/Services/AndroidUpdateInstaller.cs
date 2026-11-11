using Android.Content;
using Android.Provider;
using Nexus.Contracts.Updates;
using Nexus.Core.Errors;
using Nexus.Core.Updates;

namespace Nexus.Mobile.Services;

/// <summary>
/// Launches the user-interactive Android APK installation flow via <c>FileProvider</c>.
/// Respects <c>REQUEST_INSTALL_PACKAGES</c> and never installs silently.
/// </summary>
public sealed class AndroidUpdateInstaller : IAndroidUpdateInstaller
{
    public Task<bool> CanRequestPackageInstallsAsync(CancellationToken cancellationToken = default)
    {
        var context = global::Android.App.Application.Context;
        var canInstall = !OperatingSystem.IsAndroidVersionAtLeast(26) || context.PackageManager!.CanRequestPackageInstalls();
        return Task.FromResult(canInstall);
    }

    public Task<Result> OpenUnknownSourcesSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            return Task.FromResult(Result.Success());
        }

        var context = global::Android.App.Application.Context;
        var packageUri = global::Android.Net.Uri.Parse($"package:{context.PackageName}");
        var intent = new Intent(Settings.ActionManageUnknownAppSources, packageUri);
        intent.AddFlags(ActivityFlags.NewTask);
        context.StartActivity(intent);
        return Task.FromResult(Result.Success());
    }

    public Task<Result<UpdateInstallLaunchResult>> LaunchSystemPackageInstallerAsync(
        VerifiedUpdatePackage verifiedPackage,
        CancellationToken cancellationToken = default)
    {
        var context = global::Android.App.Application.Context;
        if (OperatingSystem.IsAndroidVersionAtLeast(26) && !context.PackageManager!.CanRequestPackageInstalls())
        {
            return Task.FromResult(Result<UpdateInstallLaunchResult>.Success(new UpdateInstallLaunchResult
            {
                Launched = false,
                RequiresUnknownSourcesPermission = true,
                LocalApkPath = verifiedPackage.LocalApkPath,
                FailureReason = UpdateFailureReason.InstallPermissionDenied,
                Message = "Autorize 'Instalar apps desconhecidos' nas configurações do Android para prosseguir."
            }));
        }

        var javaFile = new Java.IO.File(verifiedPackage.LocalApkPath);
        var authority = $"{context.PackageName}.fileprovider";
        var contentUri = global::AndroidX.Core.Content.FileProvider.GetUriForFile(context, authority, javaFile);

        var action = OperatingSystem.IsAndroidVersionAtLeast(29)
            ? Intent.ActionView
            : GetLegacyInstallAction();

        var intent = new Intent(action);
        intent.SetDataAndType(contentUri, "application/vnd.android.package-archive");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        context.StartActivity(intent);

        return Task.FromResult(Result<UpdateInstallLaunchResult>.Success(new UpdateInstallLaunchResult
        {
            Launched = true,
            RequiresUnknownSourcesPermission = false,
            LocalApkPath = verifiedPackage.LocalApkPath,
            FailureReason = UpdateFailureReason.None,
            Message = "Instalador oficial do Android aberto. Confirme a instalação na tela do sistema."
        }));
    }

    private static string GetLegacyInstallAction()
    {
#pragma warning disable CA1422
        return Intent.ActionInstallPackage;
#pragma warning restore CA1422
    }
}

