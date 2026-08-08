<#
.SYNOPSIS
    Build the unpackaged CmuxGui app and wrap it in a per-user Setup.exe.

.DESCRIPTION
    Publishes the self-contained WinUI application, puts the Release GNU FFI
    engine beside CmuxGui.exe, and compiles a standard Inno Setup installer.
    The installer never modifies the certificate store and needs no elevation.

    Package.appxmanifest remains the single release-version source. Its four-part
    version is stamped into the app and installer; the first three components are
    used as the displayed release version.

.PARAMETER EngineDll
    Release cmux_ffi.dll to bundle. Defaults to Cargo's windows-gnu Release output.

.PARAMETER OutputDir
    Directory for the installer and its SHA-256 file. Defaults to windows\dist.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$EngineDll,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$proj = Join-Path $repo 'windows\CmuxGui\CmuxGui.csproj'
$manifest = Join-Path $repo 'windows\CmuxGui\Package.appxmanifest'
$installer = Join-Path $repo 'windows\installer\CmuxGui.iss'
$dist = if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    Join-Path $repo 'windows\dist'
} else {
    [System.IO.Path]::GetFullPath($OutputDir)
}
$stage = Join-Path $dist 'installer-stage'

if ([string]::IsNullOrWhiteSpace($EngineDll)) {
    $EngineDll = Join-Path $repo 'cmux-tui\target\x86_64-pc-windows-gnu\release\cmux_ffi.dll'
} else {
    $EngineDll = [System.IO.Path]::GetFullPath($EngineDll)
}
if (-not (Test-Path $EngineDll -PathType Leaf)) {
    throw "Release engine not found at '$EngineDll'. Build cmux-ffi for x86_64-pc-windows-gnu first."
}

[xml]$xml = Get-Content $manifest
$packageVersion = [string]$xml.Package.Identity.Version
$parts = $packageVersion.Split('.')
if ($parts.Count -ne 4) {
    throw "Package version '$packageVersion' must have four numeric components."
}
$null = $parts | ForEach-Object {
    $component = 0
    if (-not [int]::TryParse($_, [ref]$component)) {
        throw "Package version '$packageVersion' must have four numeric components."
    }
}
$appVersion = $parts[0..2] -join '.'

$isccCandidates = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    'C:\Program Files (x86)\Inno Setup 7\ISCC.exe',
    'C:\Program Files\Inno Setup 7\ISCC.exe',
    'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ -PathType Leaf } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup was not found. Install it with: winget install --exact --id JRSoftware.InnoSetup'
}

Write-Host "building cmux $appVersion installer ($Configuration)" -ForegroundColor Cyan

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null
New-Item -ItemType Directory -Force -Path $dist | Out-Null

& dotnet publish $proj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:WindowsAppSDKSelfContained=true `
    -p:WindowsPackageType=None `
    -p:Version=$appVersion `
    -p:AssemblyVersion=$packageVersion `
    -p:FileVersion=$packageVersion `
    -o $stage
if ($LASTEXITCODE -ne 0) { throw "GUI publish failed ($LASTEXITCODE)" }

Copy-Item $EngineDll (Join-Path $stage 'cmux_ffi.dll') -Force
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $stage 'LICENSE') -Force
Copy-Item (Join-Path $repo 'THIRD_PARTY_LICENSES.md') (Join-Path $stage 'THIRD_PARTY_LICENSES.md') -Force

$licenseDir = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force -Path $licenseDir | Out-Null
Copy-Item (Join-Path $repo 'ghostty\LICENSE') (Join-Path $licenseDir 'Ghostty-MIT.txt') -Force
Copy-Item (Join-Path $repo 'cmux-tui\vendor\crossterm\LICENSE') (Join-Path $licenseDir 'Crossterm-MIT.txt') -Force
Copy-Item (Join-Path $repo 'cmux-tui\vendor\terminput-crossterm\LICENSE-MIT') (Join-Path $licenseDir 'terminput-crossterm-MIT.txt') -Force
Copy-Item (Join-Path $repo 'cmux-tui\vendor\terminput-crossterm\LICENSE-APACHE') (Join-Path $licenseDir 'terminput-crossterm-Apache-2.0.txt') -Force

$required = @(
    'CmuxGui.exe',
    'CmuxGui.dll',
    'CmuxGui.deps.json',
    'CmuxGui.runtimeconfig.json',
    'cmux_ffi.dll',
    'LICENSE',
    'THIRD_PARTY_LICENSES.md',
    'licenses\Ghostty-MIT.txt',
    'licenses\Crossterm-MIT.txt',
    'licenses\terminput-crossterm-MIT.txt',
    'licenses\terminput-crossterm-Apache-2.0.txt',
    'Microsoft.ui.xaml.dll',
    'resources.pri'
)
foreach ($name in $required) {
    if (-not (Test-Path (Join-Path $stage $name) -PathType Leaf)) {
        throw "Publish output is incomplete: missing $name"
    }
}

& $iscc `
    "/DAppVersion=$appVersion" `
    "/DPackageVersion=$packageVersion" `
    "/DSourceDir=$stage" `
    "/DOutputDir=$dist" `
    $installer
if ($LASTEXITCODE -ne 0) { throw "installer compilation failed ($LASTEXITCODE)" }

$setup = Join-Path $dist "cmux-windows-v$appVersion-setup.exe"
if (-not (Test-Path $setup -PathType Leaf)) {
    throw "Inno Setup reported success but did not produce '$setup'."
}

$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$hashPath = "$setup.sha256"
[System.IO.File]::WriteAllText($hashPath, $hash, [System.Text.Encoding]::ASCII)

Write-Host "built $setup" -ForegroundColor Green
Write-Host "sha256 $hash" -ForegroundColor Green
