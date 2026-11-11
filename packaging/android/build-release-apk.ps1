<#
.SYNOPSIS
    Compila e assina o APK Android de Release sem armazenar keystores ou senhas no repositorio.
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [int]$VersionCode = 0,
    [string]$OutputDir = "dist/android",
    [string]$KeystorePath = $env:NEXUS_ANDROID_KEYSTORE_PATH,
    [string]$KeyAlias = $env:NEXUS_ANDROID_KEY_ALIAS,
    [string]$KeyPassword = $env:NEXUS_ANDROID_KEY_PASS,
    [string]$StorePassword = $env:NEXUS_ANDROID_STORE_PASS
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$workloads = (dotnet workload list | Out-String)
if ($workloads -notmatch "maui-android") {
    Write-Warning "Workload 'maui-android' nao esta instalado neste ambiente local."
    Write-Host "Opcoes para gerar o APK Android assinado:" -ForegroundColor Cyan
    Write-Host "  1) Localmente (Windows/Linux com Android SDK): dotnet workload install maui-android"
    Write-Host "  2) Remotamente via GitHub Actions: workflow '.github/workflows/android-release.yml'"
    exit 2
}

[xml]$props = Get-Content (Join-Path $repoRoot "Directory.Build.props") -Encoding UTF8
if ([string]::IsNullOrWhiteSpace($Version)) {
    $versionNode = $props.SelectSingleNode("//NexusVersion")
    $Version = if ($versionNode -and -not [string]::IsNullOrWhiteSpace($versionNode.InnerText)) { $versionNode.InnerText.Trim() } else { "1.0.0" }
}
if ($VersionCode -le 0) {
    $vcNode = $props.SelectSingleNode("//NexusAndroidVersionCode")
    if ($vcNode -and [int]::TryParse($vcNode.InnerText.Trim(), [ref]$VersionCode) -and $VersionCode -gt 0) { }
    else { $VersionCode = 10000 }
}

$resolvedOut = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $repoRoot $OutputDir }
New-Item -ItemType Directory -Force -Path $resolvedOut | Out-Null

$mobileProj = Join-Path $repoRoot "src\Nexus.Mobile\Nexus.Mobile.csproj"
$publishArgs = @(
    "publish", $mobileProj,
    "--configuration", "Release",
    "-f", "net10.0-android",
    "-p:NexusVersion=$Version",
    "-p:NexusAndroidVersionCode=$VersionCode",
    "-o", $resolvedOut
)

$hasSigning = -not [string]::IsNullOrWhiteSpace($KeystorePath) -and
              -not [string]::IsNullOrWhiteSpace($KeyAlias) -and
              -not [string]::IsNullOrWhiteSpace($KeyPassword) -and
              -not [string]::IsNullOrWhiteSpace($StorePassword)

if ($hasSigning) {
    if (-not (Test-Path $KeystorePath)) { throw "Arquivo de keystore nao encontrado: $KeystorePath" }
    $publishArgs += @(
        "-p:AndroidKeyStore=true",
        "-p:AndroidSigningKeyStore=$KeystorePath",
        "-p:AndroidSigningKeyAlias=$KeyAlias",
        "-p:AndroidSigningKeyPass=$KeyPassword",
        "-p:AndroidSigningStorePass=$StorePassword"
    )
    Write-Host "==> Compilando e assinando APK Android NEXUS v$Version ($VersionCode)..." -ForegroundColor Cyan
}
else {
    Write-Warning "Variaveis de assinatura nao definidas; gerando APK Release apenas para testes internos."
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Falha ao gerar APK Android (exit code $LASTEXITCODE)." }

$apk = Get-ChildItem -Path $resolvedOut -Filter "*-Signed.apk" | Select-Object -First 1
if (-not $apk) { $apk = Get-ChildItem -Path $resolvedOut -Filter "*.apk" | Select-Object -First 1 }
if (-not $apk) { throw "Nenhum arquivo .apk gerado em $resolvedOut." }

$apkHash = (Get-FileHash $apk.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "APK gerado: $($apk.FullName) ($($apk.Length) bytes | SHA-256: $apkHash)" -ForegroundColor Green

