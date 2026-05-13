param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProject = Join-Path $root "NoiseToggle\NoiseToggle.csproj"
$installerProject = Join-Path $root "NoiseToggle.Installer\NoiseToggle.Installer.csproj"
$appPublish = Join-Path $root "publish\app-$Runtime"
$installerPayload = Join-Path $root "NoiseToggle.Installer\Payload\NoiseTogglePayload.zip"
$installerPublish = Join-Path $root "publish\installer-$Runtime"
$setupOut = Join-Path $root "publish\NoiseToggleSetup.exe"

if (Test-Path -LiteralPath $appPublish) {
    Remove-Item -LiteralPath $appPublish -Recurse -Force
}

dotnet publish $appProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=false `
    -o $appPublish

if (Test-Path -LiteralPath $installerPayload) {
    Remove-Item -LiteralPath $installerPayload -Force
}

Compress-Archive -Path (Join-Path $appPublish "*") -DestinationPath $installerPayload -Force

if (Test-Path -LiteralPath $installerPublish) {
    Remove-Item -LiteralPath $installerPublish -Recurse -Force
}

dotnet publish $installerProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $installerPublish

Copy-Item (Join-Path $installerPublish "NoiseToggleSetup.exe") $setupOut -Force
Write-Host "Installer created: $setupOut"
