<#
.SYNOPSIS
    Builds FocusFlow for Windows: a portable zip, an MSIX package, or both.

.DESCRIPTION
    Run this ON Windows — it needs the Windows SDK (makeappx, signtool) for MSIX.

        .\build\windows\package.ps1 -Portable
        .\build\windows\package.ps1 -Msix -Publisher "CN=FocusFlow" -CertPath test.pfx

    Portable is the mode that works today: a zip anyone can unpack and run, with a
    SmartScreen warning on first launch. An *unsigned* MSIX cannot be installed at all —
    Windows refuses packages without a trusted signature — so -Msix is only useful once a
    certificate exists, whether self-signed for testing or a real code-signing cert.
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
    [switch] $Portable,
    [switch] $Msix,
    [string] $Publisher = 'CN=FocusFlow',
    [string] $CertPath,
    [string] $CertPassword
)

$ErrorActionPreference = 'Stop'
if (-not $Portable -and -not $Msix) { $Portable = $true }

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dist = Join-Path $root 'dist\windows'
$tfm  = 'net10.0-windows10.0.17763.0'
$rid  = "win-$Arch"

# Read the version from the project so the package can never disagree with the assembly.
$csproj  = Join-Path $root 'FocusFlow.App\FocusFlow.App.csproj'
$version = ([xml](Get-Content $csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Could not read <Version> from the csproj' }
Write-Host "==> FocusFlow $version ($Arch)"

$stage = Join-Path $dist "app-$Arch"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Write-Host "==> publishing $rid"
dotnet publish $csproj -c Release -f $tfm -r $rid --self-contained true `
    -p:PublishSingleFile=false -p:DebugType=none -o $stage --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

if ($Portable) {
    $zip = Join-Path $dist "FocusFlow-$version-$Arch-portable.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
    Write-Host "portable: $zip"
}

if ($Msix) {
    $makeappx = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $makeappx) { throw 'makeappx.exe not found — install the Windows 10/11 SDK' }

    # MSIX wants a 4-part version; the csproj carries three.
    $msixVersion = if ($version -match '^\d+\.\d+\.\d+$') { "$version.0" } else { $version }

    Copy-Item (Join-Path $PSScriptRoot 'Assets') (Join-Path $stage 'Assets') -Recurse -Force
    (Get-Content (Join-Path $PSScriptRoot 'Package.appxmanifest')) `
        -replace '__VERSION__', $msixVersion `
        -replace '__PUBLISHER__', $Publisher `
        -replace '__ARCH__', $Arch |
        Set-Content (Join-Path $stage 'AppxManifest.xml') -Encoding UTF8

    $msix = Join-Path $dist "FocusFlow-$version-$Arch.msix"
    if (Test-Path $msix) { Remove-Item $msix -Force }
    & $makeappx.FullName pack /d $stage /p $msix /o
    if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }

    if ($CertPath) {
        $signtool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending | Select-Object -First 1
        if (-not $signtool) { throw 'signtool.exe not found — install the Windows 10/11 SDK' }
        $args = @('sign', '/fd', 'SHA256', '/a', '/f', $CertPath)
        if ($CertPassword) { $args += @('/p', $CertPassword) }
        & $signtool.FullName @args $msix
        if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }
        Write-Host "    signed with $CertPath"
    } else {
        Write-Warning 'MSIX is UNSIGNED and cannot be installed. Sign it with -CertPath.'
    }

    Write-Host "msix: $msix"
}
