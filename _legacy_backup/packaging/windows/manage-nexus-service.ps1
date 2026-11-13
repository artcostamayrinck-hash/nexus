<#
.SYNOPSIS
    Gerencia a tarefa agendada de inicio automatico do NEXUS Server no logon Windows e regra opcional de Firewall LAN.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("install", "uninstall", "start", "stop", "status")]
    [string]$Action,

    [string]$TaskName = "NEXUS-Server-AutoStart",
    [string]$ServerDir = "$env:LOCALAPPDATA\Programs\NEXUS\server",
    [string]$DataDir = "$env:LOCALAPPDATA\NEXUS\server",
    [int]$HttpsPort = 5443,
    [switch]$OpenFirewallPort
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

switch ($Action) {
    "install" {
        $exe = Join-Path $ServerDir "Nexus.Server.exe"
        if (-not (Test-Path $exe)) { throw "Nexus.Server.exe nao encontrado em '$exe'." }

        $taskAction = New-ScheduledTaskAction -Execute $exe -Argument "--data-dir `"$DataDir`" --port $HttpsPort" -WorkingDirectory $ServerDir
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

        Register-ScheduledTask -TaskName $TaskName -Action $taskAction -Trigger $trigger -Settings $settings -Description "Inicia o NEXUS Server localmente com banco SQLite isolado." -Force | Out-Null
        Write-Host "Tarefa '$TaskName' configurada para iniciar automaticamente no logon de $env:USERNAME." -ForegroundColor Green

        if ($OpenFirewallPort.IsPresent) {
            $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            if ($isAdmin) {
                New-NetFirewallRule -DisplayName "NEXUS Server HTTPS ($HttpsPort)" -Direction Inbound -Protocol TCP -LocalPort $HttpsPort -Profile Private -Action Allow -ErrorAction SilentlyContinue | Out-Null
                Write-Host "Regra de firewall para TCP $HttpsPort (Perfil Privado/LAN) aplicada." -ForegroundColor Green
            }
            else {
                Write-Warning "Execute PowerShell como Administrador para aplicar regra no Firewall Windows para TCP $HttpsPort."
            }
        }
    }
    "uninstall" {
        if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
            Write-Host "Tarefa '$TaskName' removida." -ForegroundColor Yellow
        }
        else {
            Write-Host "Tarefa '$TaskName' nao estava registrada."
        }
    }
    "start" {
        Start-ScheduledTask -TaskName $TaskName
        Write-Host "Tarefa '$TaskName' iniciada." -ForegroundColor Green
    }
    "stop" {
        Get-Process -Name "Nexus.Server" -ErrorAction SilentlyContinue | Stop-Process -Force
        Write-Host "Processos Nexus.Server encerrados." -ForegroundColor Yellow
    }
    "status" {
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        $procs = Get-Process -Name "Nexus.Server", "Nexus.Desktop" -ErrorAction SilentlyContinue
        Write-Host "Scheduled Task ($TaskName): $(if ($task) { $task.State } else { 'Nao instalada' })" -ForegroundColor Cyan
        if ($procs) {
            $procs | Format-Table ProcessName, Id, StartTime -AutoSize
        }
        else {
            Write-Host "Nenhum processo Nexus.Server ou Nexus.Desktop em execucao."
        }
    }
}

