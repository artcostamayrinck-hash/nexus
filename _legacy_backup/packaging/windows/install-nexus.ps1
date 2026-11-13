<#
.SYNOPSIS
    Instala os binarios publicados do NEXUS Server e NEXUS Desktop no perfil do usuario Windows,
    cria atalhos no Menu Iniciar e opcionalmente configura inicio automatico do servidor.
#>
[CmdletBinding()]
param(
    [string]$PackageDir = "",
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\NEXUS",
    [string]$DataDir = "$env:LOCALAPPDATA\NEXUS\server",
    [switch]$AutoStartServer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($PackageDir)) {
    if (Test-Path (Join-Path $PSScriptRoot "server\Nexus.Server.exe")) {
        $PackageDir = $PSScriptRoot
    }
    else {
        $PackageDir = (Resolve-Path (Join-Path $PSScriptRoot "..\..\dist\windows")).Path
    }
}

$sourceServer = Join-Path $PackageDir "server"
$sourceDesktop = Join-Path $PackageDir "desktop"
if (-not (Test-Path (Join-Path $sourceServer "Nexus.Server.exe")) -or
    -not (Test-Path (Join-Path $sourceDesktop "Nexus.Desktop.exe"))) {
    throw "Pacote Windows nao encontrado em '$PackageDir'. Execute primeiro publish-windows.ps1."
}

$targetServer = Join-Path $InstallDir "server"
$targetDesktop = Join-Path $InstallDir "desktop"

# Encerra processos antigos para evitar lock de DLLs durante atualizacao local
Get-Process -Name "Nexus.Server", "Nexus.Desktop" -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Encerrando processo ativo '$($_.ProcessName)' (PID $($_.Id))..." -ForegroundColor Yellow
    Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 400

New-Item -ItemType Directory -Force -Path $targetServer, $targetDesktop, $DataDir | Out-Null
Copy-Item (Join-Path $sourceServer "*") $targetServer -Recurse -Force
Copy-Item (Join-Path $sourceDesktop "*") $targetDesktop -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot "*.ps1") $InstallDir -Force -ErrorAction SilentlyContinue

# Preserva appsettings.Production.json existente caso o usuario ja tenha configurado relay/portas
$prodConfigPath = Join-Path $targetServer "appsettings.Production.json"
$exampleConfigPath = Join-Path $targetServer "appsettings.Production.example.json"
if (-not (Test-Path $prodConfigPath) -and (Test-Path $exampleConfigPath)) {
    Copy-Item $exampleConfigPath $prodConfigPath
    Write-Host "Criado appsettings.Production.json inicial em $targetServer (revise antes de expor relay remoto)." -ForegroundColor Cyan
}

# Cria atalhos no Menu Iniciar Windows
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\NEXUS"
New-Item -ItemType Directory -Force -Path $startMenuDir | Out-Null

$shell = New-Object -ComObject WScript.Shell

$desktopShortcut = $shell.CreateShortcut((Join-Path $startMenuDir "NEXUS Desktop.lnk"))
$desktopShortcut.TargetPath = Join-Path $targetDesktop "Nexus.Desktop.exe"
$desktopShortcut.WorkingDirectory = $targetDesktop
$desktopShortcut.Description = "NEXUS Ecossistema Pessoal (Android + Windows)"
$desktopShortcut.Save()

$serverShortcut = $shell.CreateShortcut((Join-Path $startMenuDir "NEXUS Server.lnk"))
$serverShortcut.TargetPath = Join-Path $targetServer "Nexus.Server.exe"
$serverShortcut.Arguments = "--data-dir `"$DataDir`""
$serverShortcut.WorkingDirectory = $targetServer
$serverShortcut.Description = "NEXUS Server (TLS + SignalR + File Transfer)"
$serverShortcut.Save()

Write-Host "Atalhos criados em: $startMenuDir" -ForegroundColor Green

if ($AutoStartServer.IsPresent) {
    & (Join-Path $PSScriptRoot "manage-nexus-service.ps1") -Action install -ServerDir $targetServer -DataDir $DataDir
}

Write-Host "NEXUS instalado com sucesso em $InstallDir (dados preservados em $DataDir)." -ForegroundColor Green

