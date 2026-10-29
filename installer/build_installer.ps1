<#
.SYNOPSIS
  Builds, tests, publishes and packages Unicaption for Windows x64, then writes latest.json.

.DESCRIPTION
  Run on Windows from any directory:
    powershell -ExecutionPolicy Bypass -File installer\build_installer.ps1

  Requirements: .NET 8 SDK, Inno Setup 6 (ISCC.exe). Optional code signing:
    $env:SIGN_CERT_THUMBPRINT = '<thumbprint>'   # cert in CurrentUser\My
    $env:SIGN_TIMESTAMP_URL   = 'http://timestamp.digicert.com'

  Output: installer\Output\UnicaptionSetup-<ver>.exe, UnicaptionSetup.exe, latest.json
  Upload order (UPDATE 01): installer first, then latest.json.
#>
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$DownloadUrl = 'https://unicaption.com/instals/UnicaptionSetup.exe',
    [string]$Notes = 'Native Windows app: WASAPI microphone/system audio, monotonic session timer, secure sign-in.',
    [string]$Iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup.Version
if (-not $version) { throw 'Version not found in Directory.Build.props' }
$platform = if ($Runtime -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$publishDir = Join-Path $root "artifacts\publish\$Runtime"
$outDir = Join-Path $PSScriptRoot 'Output'

Write-Host "[1/5] Unit tests" -ForegroundColor Cyan
dotnet test tests\Unicaption.Core.Tests -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed' }

Write-Host "[2/5] Publish $Runtime ($version)" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish src\Unicaption.App\Unicaption.App.csproj -c $Configuration -r $Runtime -p:Platform=$platform `
    --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

function Sign-File([string]$path) {
    if (-not $env:SIGN_CERT_THUMBPRINT) { return }
    $ts = if ($env:SIGN_TIMESTAMP_URL) { $env:SIGN_TIMESTAMP_URL } else { 'http://timestamp.digicert.com' }
    & signtool sign /fd SHA256 /sha1 $env:SIGN_CERT_THUMBPRINT /tr $ts /td SHA256 $path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $path" }
}
Sign-File (Join-Path $publishDir 'Unicaption.exe')

Write-Host "[3/5] Inno Setup" -ForegroundColor Cyan
if (-not (Test-Path $Iscc)) { throw "ISCC.exe not found at $Iscc" }
& $Iscc "/DMyAppVersion=$version" "/DPublishDir=$publishDir" (Join-Path $PSScriptRoot 'unicaption_setup.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
$installer = Join-Path $outDir "UnicaptionSetup-$version.exe"
Sign-File $installer

Write-Host "[4/5] Checksum + manifest" -ForegroundColor Cyan
Copy-Item $installer (Join-Path $outDir 'UnicaptionSetup.exe') -Force
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    version      = $version
    download_url = $DownloadUrl
    sha256       = $hash
    notes        = $Notes
}
[System.IO.File]::WriteAllText((Join-Path $outDir 'latest.json'), ($manifest | ConvertTo-Json),
    (New-Object System.Text.UTF8Encoding($false)))

Write-Host "[5/5] Done" -ForegroundColor Green
Write-Host "  $installer"
Write-Host "  sha256 $hash"
Write-Host "Upload Output\UnicaptionSetup.exe first, then Output\latest.json, to /instals/."
