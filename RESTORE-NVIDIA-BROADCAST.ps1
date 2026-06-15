$ErrorActionPreference = "Stop"

$broadcastDir = Join-Path $env:ProgramFiles "NVIDIA Corporation\NVIDIA Broadcast"
$broadcastResources = Join-Path $broadcastDir "resources"
$targetAsar = Join-Path $broadcastResources "app.asar"
$legacyBackup = Join-Path $broadcastResources "app.asar.noisetoggle-backup"
$broadcastExe = Join-Path $broadcastDir "NVIDIA Broadcast.exe"
$workDir = Join-Path $env:TEMP ("NoiseToggleBroadcastRestore-" + [Guid]::NewGuid().ToString("N"))
$bridgeMarkers = @(
    "/* NoiseToggle Broadcast Bridge BEGIN */",
    "/* NoiseToggle Broadcast Bridge v4 */",
    "/* NoiseToggle Broadcast Bridge v5 */"
)

function Invoke-AsarExtract {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    & npx --yes "@electron/asar" extract $Source $Destination
    if ($LASTEXITCODE -ne 0) {
        throw "asar extract failed with exit code $LASTEXITCODE."
    }
}

function Get-BroadcastVersion {
    $version = (Get-Item -LiteralPath $broadcastExe).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "Could not detect the installed NVIDIA Broadcast version."
    }
    return ($version -replace "[^0-9A-Za-z._-]", "_")
}

function Assert-MatchingCleanBackup {
    param(
        [Parameter(Mandatory = $true)][string]$Backup,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion
    )

    $extractDir = Join-Path $workDir ([Guid]::NewGuid().ToString("N"))
    Invoke-AsarExtract -Source $Backup -Destination $extractDir
    $mainPath = Join-Path $extractDir "build\electron\main.js"
    $packagePath = Join-Path $extractDir "package.json"
    if (-not (Test-Path -LiteralPath $mainPath) -or -not (Test-Path -LiteralPath $packagePath)) {
        throw "Backup $Backup is not a valid NVIDIA Broadcast Electron archive."
    }

    $content = [System.IO.File]::ReadAllText($mainPath)
    foreach ($marker in $bridgeMarkers) {
        if ($content.Contains($marker)) {
            throw "Backup $Backup contains a NoiseToggle bridge and is not clean."
        }
    }

    $package = Get-Content -Raw -LiteralPath $packagePath | ConvertFrom-Json
    if (-not $ExpectedVersion.StartsWith([string]$package.version, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Backup $Backup contains NVIDIA Broadcast $($package.version), not installed build $ExpectedVersion."
    }
}

function Start-BroadcastHiddenDetached {
    $shell = New-Object -ComObject Shell.Application
    try {
        $shell.ShellExecute($broadcastExe, "--launch-hidden", $broadcastDir, "open", 0)
    }
    finally {
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    }
}

if (-not (Test-Path -LiteralPath $targetAsar) -or -not (Test-Path -LiteralPath $broadcastExe)) {
    throw "NVIDIA Broadcast was not found at $broadcastDir."
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @(
        "-NoProfile",
        "-WindowStyle", "Hidden",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$PSCommandPath`""
    )
    Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList $arguments
    return
}

if (-not (Get-Command npx -ErrorAction SilentlyContinue)) {
    throw "Node.js/npm was not found. Install Node.js LTS, then run this script again."
}

$version = Get-BroadcastVersion
$versionedBackup = Join-Path $broadcastResources "app.asar.noisetoggle-clean-$version"
$backup = if (Test-Path -LiteralPath $versionedBackup) { $versionedBackup } else { $legacyBackup }
if (-not (Test-Path -LiteralPath $backup)) {
    throw "No clean NoiseToggle backup was found for NVIDIA Broadcast $version."
}

New-Item -ItemType Directory -Path $workDir | Out-Null
try {
    Assert-MatchingCleanBackup -Backup $backup -ExpectedVersion $version
    Get-Process "NVIDIA Broadcast" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
    Copy-Item -LiteralPath $backup -Destination $targetAsar -Force
    Start-BroadcastHiddenDetached
    Write-Host "NVIDIA Broadcast $version restored from clean backup $backup"
}
finally {
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
