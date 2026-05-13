$ErrorActionPreference = "Stop"

$broadcastResources = "C:\Program Files\NVIDIA Corporation\NVIDIA Broadcast\resources"
$targetAsar = Join-Path $broadcastResources "app.asar"
$backupAsar = Join-Path $broadcastResources "app.asar.noisetoggle-backup"
$broadcastExe = "C:\Program Files\NVIDIA Corporation\NVIDIA Broadcast\NVIDIA Broadcast.exe"

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

if (Test-Path -LiteralPath $broadcastExe) {
    Start-Process -FilePath $broadcastExe -ArgumentList "--launch-hidden" -WindowStyle Hidden
}

Write-Host "NVIDIA Broadcast app.asar restored from NoiseToggle backup."
