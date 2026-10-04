param(
    [switch]$OpenOutputFolder
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $PSScriptRoot "NorthstarNetworkLab.wixproj"
$installerPath = Join-Path $PSScriptRoot "bin\x64\Release\NorthstarNetworkLab-Setup.msi"
$artworkScript = Join-Path $PSScriptRoot "Generate-InstallerArtwork.ps1"
$bannerPath = Join-Path $PSScriptRoot "InstallerBanner.bmp"
$dialogPath = Join-Path $PSScriptRoot "InstallerDialog.bmp"
$licensePath = Join-Path $PSScriptRoot "License.rtf"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK was not found. Install the .NET 8 SDK, then run this script again."
}

Push-Location $repositoryRoot
try {
    & $artworkScript
    if (-not (Test-Path -LiteralPath $bannerPath) -or -not (Test-Path -LiteralPath $dialogPath)) {
        throw "Installer artwork generation did not produce the required banner and dialog images."
    }
    foreach ($asset in @(@{ Path = $bannerPath; Width = 493; Height = 58 }, @{ Path = $dialogPath; Width = 493; Height = 312 })) {
        $bytes = [System.IO.File]::ReadAllBytes($asset.Path)
        if ($bytes.Length -lt 54 -or $bytes[0] -ne 0x42 -or $bytes[1] -ne 0x4d -or
            [BitConverter]::ToInt32($bytes, 18) -ne $asset.Width -or [BitConverter]::ToInt32($bytes, 22) -ne $asset.Height) {
            throw "Installer bitmap has an invalid BMP header or dimensions: $($asset.Path)"
        }
    }
    if (-not (Test-Path -LiteralPath $licensePath) -or (Get-Item -LiteralPath $licensePath).Length -lt 5000) {
        throw "The installer license file is missing or unexpectedly short."
    }
    dotnet build $projectPath --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "Installer build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Build reported success, but the MSI was not found at $installerPath."
}

$installer = Get-Item -LiteralPath $installerPath
Write-Host "Installer created: $($installer.FullName)"
Write-Host "Size: $([math]::Round($installer.Length / 1MB, 1)) MB"

if ($OpenOutputFolder) {
    Invoke-Item (Split-Path -Parent $installerPath)
}