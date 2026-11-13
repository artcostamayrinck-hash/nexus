<#
.SYNOPSIS
    Remove o NEXUS instalado no Windows preservando por padrao o banco SQLite e credenciais do usuario.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\NEXUS",
    [string]$DataDir = "$env:LOCALAPPDATA\NEXUS",
    [switch]$PurgeUserData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Get-Process -Name "Nexus.Server", "Nexus.Desktop" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400

if (Get-ScheduledTask -TaskName "NEXUS-Server-AutoStart" -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName "NEXUS-Server-AutoStart" -Confirm:$false
}

$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\NEXUS"
if (Test-Path $startMenuDir) { Remove-Item $startMenuDir -Recurse -Force }
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }

if ($PurgeUserData.IsPresent -and (Test-Path $DataDir)) {
    Remove-Item $DataDir -Recurse -Force
    Write-Host "Dados do usuario em '$DataDir' removidos conforme solicitado." -ForegroundColor Yellow
}
else {
    Write-Host "Bancos SQLite e identidade criptografica preservados em '$DataDir'." -ForegroundColor Cyan
}

Write-Host "Desinstalacao do NEXUS concluida." -ForegroundColor Green

