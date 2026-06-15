$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$patchScript = Join-Path $scriptDir "patch-broadcast-bridge.js"
$broadcastDir = Join-Path $env:ProgramFiles "NVIDIA Corporation\NVIDIA Broadcast"
$broadcastResources = Join-Path $broadcastDir "resources"
$targetAsar = Join-Path $broadcastResources "app.asar"
$broadcastExe = Join-Path $broadcastDir "NVIDIA Broadcast.exe"
$workDir = Join-Path $env:TEMP ("NoiseToggleBroadcastPatch-" + [Guid]::NewGuid().ToString("N"))
$beginMarker = "/* NoiseToggle Broadcast Bridge BEGIN */"
$legacyMarkers = @(
    "/* NoiseToggle Broadcast Bridge v4 */",
    "/* NoiseToggle Broadcast Bridge v5 */"
)

function Invoke-Asar {
    param(
        [Parameter(Mandatory = $true)][string]$Action,
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    & npx --yes "@electron/asar" $Action $Source $Destination
    if ($LASTEXITCODE -ne 0) {
        throw "asar $Action failed with exit code $LASTEXITCODE."
    }
}

function Get-BroadcastVersion {
    $version = (Get-Item -LiteralPath $broadcastExe).VersionInfo.ProductVersion
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "Could not detect the installed NVIDIA Broadcast version."
    }
    return ($version -replace "[^0-9A-Za-z._-]", "_")
}

function Test-BridgePresent {
    param([Parameter(Mandatory = $true)][string]$MainPath)

    $content = [System.IO.File]::ReadAllText($MainPath)
    if ($content.Contains($beginMarker)) {
        return $true
    }
    foreach ($marker in $legacyMarkers) {
        if ($content.Contains($marker)) {
            return $true
        }
    }
    return $false
}

function Assert-CleanExtract {
    param(
        [Parameter(Mandatory = $true)][string]$ExtractDir,
        [Parameter(Mandatory = $true)][string]$ExpectedVersion
    )

    $mainPath = Join-Path $ExtractDir "build\electron\main.js"
    $packagePath = Join-Path $ExtractDir "package.json"
    if (-not (Test-Path -LiteralPath $mainPath) -or -not (Test-Path -LiteralPath $packagePath)) {
        throw "The NVIDIA Broadcast archive does not contain the expected Electron application."
    }
    if (Test-BridgePresent -MainPath $mainPath) {
        throw "Refusing to use a patched NVIDIA Broadcast archive as a clean backup."
    }

    $package = Get-Content -Raw -LiteralPath $packagePath | ConvertFrom-Json
    if (-not $ExpectedVersion.StartsWith([string]$package.version, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The clean backup contains NVIDIA Broadcast $($package.version), not installed build $ExpectedVersion."
    }
}

function Start-BroadcastHiddenDetached {
    if (-not (Test-Path -LiteralPath $broadcastExe)) {
        return
    }

    $shell = New-Object -ComObject Shell.Application
    try {
        $shell.ShellExecute($broadcastExe, "--launch-hidden", $broadcastDir, "open", 0)
    }
    finally {
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    }
}

if (-not (Test-Path -LiteralPath $patchScript)) {
    throw "patch-broadcast-bridge.js must be next to this installer script."
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
$cleanBackup = Join-Path $broadcastResources "app.asar.noisetoggle-clean-$version"
$backupMetadata = "$cleanBackup.json"
$targetExtract = Join-Path $workDir "target"
$cleanExtract = Join-Path $workDir "clean"
$patchExtract = Join-Path $workDir "patch"
$patchedAsar = Join-Path $workDir "app.asar"

New-Item -ItemType Directory -Path $workDir | Out-Null
try {
    Get-Process "NVIDIA Broadcast" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900

    Invoke-Asar -Action "extract" -Source $targetAsar -Destination $targetExtract
    $targetMain = Join-Path $targetExtract "build\electron\main.js"
    $targetIsPatched = Test-BridgePresent -MainPath $targetMain

    if ($targetIsPatched) {
        if (-not (Test-Path -LiteralPath $cleanBackup)) {
            throw "The installed app.asar is already patched and no clean backup exists for NVIDIA Broadcast $version. Reinstall or repair NVIDIA Broadcast, then run this installer again."
        }
        Invoke-Asar -Action "extract" -Source $cleanBackup -Destination $cleanExtract
        Assert-CleanExtract -ExtractDir $cleanExtract -ExpectedVersion $version
        Copy-Item -LiteralPath $cleanExtract -Destination $patchExtract -Recurse
    }
    else {
        Assert-CleanExtract -ExtractDir $targetExtract -ExpectedVersion $version
        $targetHash = (Get-FileHash -LiteralPath $targetAsar -Algorithm SHA256).Hash
        if (Test-Path -LiteralPath $cleanBackup) {
            $backupHash = (Get-FileHash -LiteralPath $cleanBackup -Algorithm SHA256).Hash
            if ($backupHash -ne $targetHash) {
                throw "A different clean backup already exists for NVIDIA Broadcast $version. Refusing to overwrite it."
            }
        }
        else {
            Copy-Item -LiteralPath $targetAsar -Destination $cleanBackup
            @{
                BroadcastVersion = $version
                Sha256 = $targetHash
                CreatedUtc = [DateTime]::UtcNow.ToString("O")
                Source = "Unpatched installed app.asar"
            } | ConvertTo-Json | Set-Content -LiteralPath $backupMetadata -Encoding UTF8
        }
        Copy-Item -LiteralPath $targetExtract -Destination $patchExtract -Recurse
    }

    $env:NOISETOGGLE_BROADCAST_APP_DIR = $patchExtract
    try {
        & node $patchScript
        if ($LASTEXITCODE -ne 0) {
            throw "NoiseToggle bridge patch failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Remove-Item Env:\NOISETOGGLE_BROADCAST_APP_DIR -ErrorAction SilentlyContinue
    }

    $patchedMain = Join-Path $patchExtract "build\electron\main.js"
    $patchedContent = [System.IO.File]::ReadAllText($patchedMain)
    if (([regex]::Matches($patchedContent, [regex]::Escape($beginMarker))).Count -ne 1) {
        throw "The patched NVIDIA Broadcast archive does not contain exactly one NoiseToggle bridge."
    }

    Invoke-Asar -Action "pack" -Source $patchExtract -Destination $patchedAsar
    Copy-Item -LiteralPath $patchedAsar -Destination $targetAsar -Force

    Start-BroadcastHiddenDetached
    Write-Host "NoiseToggle NVIDIA Broadcast bridge v6 installed for NVIDIA Broadcast $version."
    Write-Host "Clean backup preserved at $cleanBackup"
}
finally {
    Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
}
