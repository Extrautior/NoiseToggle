$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$patchScript = Join-Path $scriptDir "patch-broadcast-bridge.js"
$broadcastResources = "C:\Program Files\NVIDIA Corporation\NVIDIA Broadcast\resources"
$targetAsar = Join-Path $broadcastResources "app.asar"
$backupAsar = Join-Path $broadcastResources "app.asar.noisetoggle-backup"
$workDir = Join-Path $env:TEMP ("NoiseToggleBroadcastPatch-" + [Guid]::NewGuid().ToString("N"))
$broadcastExe = "C:\Program Files\NVIDIA Corporation\NVIDIA Broadcast\NVIDIA Broadcast.exe"

if (-not (Test-Path -LiteralPath $patchScript)) {
    throw "patch-broadcast-bridge.js must be next to this installer script."
}

if (-not (Test-Path -LiteralPath $targetAsar)) {
    throw "NVIDIA Broadcast app.asar was not found. Is NVIDIA Broadcast installed?"
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $args = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
    Start-Process powershell.exe -Verb RunAs -ArgumentList $args
    return
}

if (-not (Get-Command npx -ErrorAction SilentlyContinue)) {
    throw "Node.js/npm was not found. Install Node.js LTS, then run this script again."
}

New-Item -ItemType Directory -Path $workDir | Out-Null
try {
    Get-Process "NVIDIA Broadcast" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 700

    if (-not (Test-Path -LiteralPath $backupAsar)) {
        Copy-Item -LiteralPath $targetAsar -Destination $backupAsar -Force
    }

    $extractDir = Join-Path $workDir "app"
    $patchedAsar = Join-Path $workDir "app.asar"
    npx --yes asar extract $targetAsar $extractDir
    Copy-Item -LiteralPath $patchScript -Destination (Join-Path $workDir "patch-broadcast-bridge.js") -Force
    Push-Location $workDir
    $env:NOISETOGGLE_BROADCAST_APP_DIR = $extractDir
    node .\patch-broadcast-bridge.js
    Remove-Item Env:\NOISETOGGLE_BROADCAST_APP_DIR -ErrorAction SilentlyContinue
    Pop-Location
    npx --yes asar pack $extractDir $patchedAsar
    Copy-Item -LiteralPath $patchedAsar -Destination $targetAsar -Force

    if (Test-Path -LiteralPath $broadcastExe) {
        Start-Process -FilePath $broadcastExe -ArgumentList "--launch-hidden" -WindowStyle Hidden
    }

    Write-Host "NoiseToggle NVIDIA Broadcast bridge installed."
}
finally {
    Pop-Location -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
