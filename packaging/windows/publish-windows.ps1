<#
.SYNOPSIS
    Compila e publica os binarios de producao Windows do NEXUS Server e NEXUS Desktop.
#>
[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",

    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",

    [switch]$SelfContained,

    [string]$OutputRoot = "dist/windows",

    [string]$Version = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$resolvedOut = if ([System.IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot } else { Join-Path $repoRoot $OutputRoot }

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$props = Get-Content (Join-Path $repoRoot "Directory.Build.props") -Encoding UTF8
    $versionNode = $props.SelectSingleNode("//NexusVersion")
    $Version = if ($versionNode -and -not [string]::IsNullOrWhiteSpace($versionNode.InnerText)) { $versionNode.InnerText.Trim() } else { "1.0.0" }
}

$serverOut = Join-Path $resolvedOut "server"
$desktopOut = Join-Path $resolvedOut "desktop"

Write-Host "==> Publicando NEXUS $Version ($Configuration | $Runtime | SelfContained=$($SelfContained.IsPresent))" -ForegroundColor Cyan

if (Test-Path $serverOut) { Remove-Item $serverOut -Recurse -Force }
if (Test-Path $desktopOut) { Remove-Item $desktopOut -Recurse -Force }
New-Item -ItemType Directory -Force -Path $serverOut, $desktopOut | Out-Null

$scFlag = if ($SelfContained.IsPresent) { "true" } else { "false" }

Write-Host " -> Publicando Nexus.Server..." -ForegroundColor Gray
dotnet publish (Join-Path $repoRoot "src\Nexus.Server\Nexus.Server.csproj") `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained $scFlag `
    --output $serverOut `
    -p:NexusVersion=$Version `
    -p:PublishSingleFile=false `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar Nexus.Server (exit code $LASTEXITCODE)." }

Write-Host " -> Publicando Nexus.Desktop..." -ForegroundColor Gray
dotnet publish (Join-Path $repoRoot "src\Nexus.Desktop\Nexus.Desktop.csproj") `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained $scFlag `
    --output $desktopOut `
    -p:NexusVersion=$Version `
    -p:PublishSingleFile=false `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar Nexus.Desktop (exit code $LASTEXITCODE)." }

Copy-Item (Join-Path $repoRoot "config\nexus-server.production.example.json") (Join-Path $serverOut "appsettings.Production.example.json") -Force
Copy-Item (Join-Path $repoRoot "config\github-update.example.json") (Join-Path $serverOut "github-update.example.json") -Force
Copy-Item (Join-Path $PSScriptRoot "install-nexus.ps1") (Join-Path $resolvedOut "install-nexus.ps1") -Force
Copy-Item (Join-Path $PSScriptRoot "manage-nexus-service.ps1") (Join-Path $resolvedOut "manage-nexus-service.ps1") -Force
Copy-Item (Join-Path $PSScriptRoot "uninstall-nexus.ps1") (Join-Path $resolvedOut "uninstall-nexus.ps1") -Force

$serverExe = Join-Path $serverOut "Nexus.Server.exe"
$desktopExe = Join-Path $desktopOut "Nexus.Desktop.exe"
if (-not (Test-Path $serverExe)) { throw "Executavel Nexus.Server.exe nao encontrado em $serverOut" }
if (-not (Test-Path $desktopExe)) { throw "Executavel Nexus.Desktop.exe nao encontrado em $desktopOut" }

$serverHash = (Get-FileHash $serverExe -Algorithm SHA256).Hash
$desktopHash = (Get-FileHash $desktopExe -Algorithm SHA256).Hash

$checksumLines = @(
    "$serverHash *server/Nexus.Server.exe",
    "$desktopHash *desktop/Nexus.Desktop.exe"
)
$checksumPath = Join-Path $resolvedOut "SHA256SUMS.txt"
Set-Content -Path $checksumPath -Value $checksumLines -Encoding UTF8

$metadata = [ordered]@{
    product        = "NEXUS Personal Ecosystem"
    version        = $Version
    configuration  = $Configuration
    runtime        = $Runtime
    selfContained  = $SelfContained.IsPresent
    publishedAtUtc = (Get-Date).ToUniversalTime().ToString("O")
    artifacts      = @(
        [ordered]@{ component = "Nexus.Server"; executable = "server/Nexus.Server.exe"; sha256 = $serverHash; sizeBytes = (Get-Item $serverExe).Length },
        [ordered]@{ component = "Nexus.Desktop"; executable = "desktop/Nexus.Desktop.exe"; sha256 = $desktopHash; sizeBytes = (Get-Item $desktopExe).Length }
    )
}
$metadata | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $resolvedOut "release-metadata.json") -Encoding UTF8

Write-Host "==> Publicacao Windows concluida em: $resolvedOut" -ForegroundColor Green
Write-Host "    Nexus.Server.exe  SHA-256: $serverHash"
Write-Host "    Nexus.Desktop.exe SHA-256: $desktopHash"

