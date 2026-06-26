<#
.SYNOPSIS
  Install / remove AgOpenWeb as a Windows Service (the headless guidance host).

.DESCRIPTION
  The Windows counterpart to the Linux systemd daemon (deploy/linux): runs the
  self-contained AgOpenWeb host display-less under the Service Control Manager, auto-
  starting on boot, with the UI served as a browser/tablet client at http://<host>:5174.
  Distinct from double-clicking AgOpenWeb.Desktop.exe (the in-window all-in-one launcher).

  Run this from the extracted bundle, in an ELEVATED PowerShell (Run as administrator).

.PARAMETER Action
  install (default) | uninstall | start | stop | status

.PARAMETER ServiceName
  Service name (default: AgOpenWeb).

.PARAMETER NoFirewall
  Skip adding the inbound firewall rule for TCP 5174 (LAN client access).

.EXAMPLE
  .\install-service.ps1                 # install + start (auto-start on boot)
.EXAMPLE
  .\install-service.ps1 -Action uninstall
#>
[CmdletBinding()]
param(
    [ValidateSet('install', 'uninstall', 'start', 'stop', 'status')]
    [string]$Action = 'install',
    [string]$ServiceName = 'AgOpenWeb',
    [switch]$NoFirewall
)

$ErrorActionPreference = 'Stop'
$DisplayName = 'AgOpenWeb Guidance Host'
$Description = 'Headless AgOpenWeb guidance host. Serves the web UI at http://<host>:5174 for browser/tablet clients. Runs the same exe with --headless.'
$Port = 5174
$FirewallRule = 'AgOpenWeb 5174'
$Exe = Join-Path $PSScriptRoot 'AgOpenWeb.Desktop.exe'

function Assert-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "This script must be run from an ELEVATED PowerShell (Run as administrator)."
    }
}

function Get-Svc { Get-Service -Name $ServiceName -ErrorAction SilentlyContinue }

function Install-Svc {
    if (-not (Test-Path $Exe)) {
        throw "AgOpenWeb.Desktop.exe not found next to this script ($Exe). Run install-service.ps1 from inside the extracted bundle."
    }
    # Recreate cleanly so an upgrade picks up a new exe path / args.
    if (Get-Svc) {
        Write-Host "==> Existing service found - stopping + removing for a clean (re)install..."
        Stop-Svc
        & sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Milliseconds 500
    }

    Write-Host "==> Registering service '$ServiceName' -> `"$Exe`" --headless"
    # BinaryPathName carries the exe + args; SCM launches it with --headless so Program.cs
    # routes to the headless host and UseWindowsService() binds the SCM lifetime.
    New-Service -Name $ServiceName `
                -BinaryPathName "`"$Exe`" --headless" `
                -DisplayName $DisplayName `
                -Description $Description `
                -StartupType Automatic | Out-Null

    # Auto-restart on crash (reset the failure count daily; restart after 5s, three times).
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

    if (-not $NoFirewall) {
        if (-not (Get-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue)) {
            Write-Host "==> Adding inbound firewall rule for TCP $Port (LAN clients)..."
            New-NetFirewallRule -DisplayName $FirewallRule -Direction Inbound -Action Allow `
                                -Protocol TCP -LocalPort $Port -Profile Any | Out-Null
        }
    }

    Write-Host "==> Starting service..."
    Start-Service -Name $ServiceName
    Show-Status
    Write-Host ""
    Write-Host "AgOpenWeb is running as a service (auto-starts on boot)."
    Write-Host "  UI:    http://localhost:$Port  (or http://<this-pc-ip>:$Port from a tablet)"
    Write-Host "  Data:  service runs as LocalSystem -> data lives under"
    Write-Host "         C:\Windows\System32\config\systemprofile\Documents\AgOpenWeb"
    Write-Host "  Stop/remove: .\install-service.ps1 -Action uninstall"
}

function Uninstall-Svc {
    if (Get-Svc) {
        Write-Host "==> Stopping + removing service '$ServiceName'..."
        Stop-Svc
        & sc.exe delete $ServiceName | Out-Null
    } else {
        Write-Host "==> Service '$ServiceName' not installed (nothing to remove)."
    }
    if (Get-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue) {
        Write-Host "==> Removing firewall rule..."
        Remove-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue
    }
    Write-Host "==> Done. (Field data under the systemprofile Documents folder was left untouched.)"
}

function Stop-Svc {
    $svc = Get-Svc
    if ($svc -and $svc.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
}

function Show-Status {
    $svc = Get-Svc
    if ($svc) { $svc | Format-Table -AutoSize Name, Status, StartType, DisplayName }
    else { Write-Host "Service '$ServiceName' is not installed." }
}

switch ($Action) {
    'install'   { Assert-Admin; Install-Svc }
    'uninstall' { Assert-Admin; Uninstall-Svc }
    'start'     { Assert-Admin; Start-Service -Name $ServiceName; Show-Status }
    'stop'      { Assert-Admin; Stop-Svc; Show-Status }
    'status'    { Show-Status }
}
