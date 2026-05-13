$ErrorActionPreference = "Stop"

$broadcastResources = "C:\Program Files\NVIDIA Corporation\NVIDIA Broadcast\resources"
$targetAsar = Join-Path $broadcastResources "app.asar"
$backupAsar = Join-Path $broadcastResources "app.asar.noisetoggle-backup"
$broadcastExe = "C:\Program Files\NVIDIA Corporation\NVIDIA Broadcast\NVIDIA Broadcast.exe"

function Start-BroadcastHiddenDetached {
    param([string]$ExePath)

    if (-not (Test-Path -LiteralPath $ExePath)) {
        return
    }

    $logDir = Join-Path $env:APPDATA "NoiseToggle"
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
    $stdout = Join-Path $logDir "NvidiaBroadcastBridge.stdout.log"
    $stderr = Join-Path $logDir "NvidiaBroadcastBridge.stderr.log"

    Start-Process `
        -FilePath $ExePath `
        -ArgumentList "--launch-hidden" `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
    Start-Process powershell.exe -Verb RunAs -ArgumentList $args
    return
}

if (-not (Test-Path -LiteralPath $backupAsar)) {
    throw "No NoiseToggle backup was found at $backupAsar"
}

Get-Process "NVIDIA Broadcast" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 700
Copy-Item -LiteralPath $backupAsar -Destination $targetAsar -Force

Start-BroadcastHiddenDetached -ExePath $broadcastExe

Write-Host "NVIDIA Broadcast app.asar restored from NoiseToggle backup."
