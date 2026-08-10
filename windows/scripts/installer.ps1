<#
.SYNOPSIS
    Build the unpackaged CmuxGui app and wrap it in a per-user Setup.exe.

.DESCRIPTION
    Publishes the self-contained WinUI application, builds the native Explorer
    command, creates a signed sparse identity package, and compiles a per-user
    Inno Setup installer. The installer trusts only the sparse package's public
    certificate for the current user and needs no elevation.

    Package.appxmanifest remains the single release-version source. Its four-part
    version is stamped into the app, sparse package, and installer; the first
    three components are used as the displayed release version.

.PARAMETER EngineDll
    Release cmux_ffi.dll to bundle. Defaults to Cargo's windows-gnu Release output.

.PARAMETER OutputDir
    Directory for the installer and its SHA-256 file. Defaults to windows\dist.

.PARAMETER CertSubject
    Subject of the current-user private-key certificate used to sign the sparse
    Explorer integration package.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$EngineDll,
    [string]$OutputDir,
    [string]$CertSubject = 'CN=cmux Development'
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$proj = Join-Path $repo 'windows\CmuxGui\CmuxGui.csproj'
$shellProj = Join-Path $repo 'windows\CmuxShellExtension\CmuxShellExtension.vcxproj'
$manifest = Join-Path $repo 'windows\CmuxGui\Package.appxmanifest'
$shellManifest = Join-Path $repo 'windows\CmuxShellPackage\Package.appxmanifest'
$shellPackageScript = Join-Path $repo 'windows\scripts\shell-package.ps1'
$installer = Join-Path $repo 'windows\installer\CmuxGui.iss'
$dist = if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    Join-Path $repo 'windows\dist'
} else {
    [System.IO.Path]::GetFullPath($OutputDir)
}
$stage = Join-Path $dist 'installer-stage'
$shellPackageStage = Join-Path $dist 'shell-package-stage'

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

$msbuild = Get-ChildItem 'C:\Program Files (x86)\Microsoft Visual Studio' -Directory -EA SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName 'BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe' } |
    Where-Object { Test-Path $_ -PathType Leaf } |
    Select-Object -First 1
if (-not $msbuild) {
    throw 'MSBuild.exe with the Visual C++ x64 tools was not found.'
}

$kitsRoot = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows Kits\Installed Roots' -EA SilentlyContinue).KitsRoot10
$sdkTools = Get-ChildItem (Join-Path $kitsRoot 'bin') -Directory -EA SilentlyContinue |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64' } |
    Where-Object {
        (Test-Path (Join-Path $_ 'makeappx.exe') -PathType Leaf) -and
        (Test-Path (Join-Path $_ 'signtool.exe') -PathType Leaf)
    } |
    Select-Object -First 1
if (-not $sdkTools) {
    throw 'The Windows SDK x64 makeappx.exe and signtool.exe were not found.'
}
$makeappx = Join-Path $sdkTools 'makeappx.exe'
$signtool = Join-Path $sdkTools 'signtool.exe'

$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object {
        $_.Subject -eq $CertSubject -and
        $_.HasPrivateKey -and
        $_.NotAfter -gt (Get-Date)
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1
if (-not $cert) {
    throw "No unexpired private-key certificate for '$CertSubject'. Run windows\scripts\new-dev-cert.ps1."
}

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

Write-Host "building cmux $appVersion installer ($Configuration), signing Explorer integration with $($cert.Thumbprint)" -ForegroundColor Cyan

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $shellPackageStage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null
New-Item -ItemType Directory -Force -Path $shellPackageStage | Out-Null
New-Item -ItemType Directory -Force -Path $dist | Out-Null

& $msbuild $shellProj `
    -p:Configuration=$Configuration `
    -p:Platform=x64 `
    -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "shell extension build failed ($LASTEXITCODE)" }

$shellDll = Join-Path $repo "windows\CmuxShellExtension\bin\x64\$Configuration\CmuxShellExtension.dll"
if (-not (Test-Path $shellDll -PathType Leaf)) {
    throw "shell extension build reported success but did not produce '$shellDll'."
}

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
Copy-Item $shellDll (Join-Path $stage 'CmuxShellExtension.dll') -Force
Copy-Item $shellPackageScript (Join-Path $stage 'shell-package.ps1') -Force
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $stage 'LICENSE') -Force
Copy-Item (Join-Path $repo 'THIRD_PARTY_LICENSES.md') (Join-Path $stage 'THIRD_PARTY_LICENSES.md') -Force

$licenseDir = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force -Path $licenseDir | Out-Null
Copy-Item (Join-Path $repo 'ghostty\LICENSE') (Join-Path $licenseDir 'Ghostty-MIT.txt') -Force
Copy-Item (Join-Path $repo 'cmux-tui\vendor\crossterm\LICENSE') (Join-Path $licenseDir 'Crossterm-MIT.txt') -Force
Copy-Item (Join-Path $repo 'cmux-tui\vendor\terminput-crossterm\LICENSE-MIT') (Join-Path $licenseDir 'terminput-crossterm-MIT.txt') -Force
Copy-Item (Join-Path $repo 'cmux-tui\vendor\terminput-crossterm\LICENSE-APACHE') (Join-Path $licenseDir 'terminput-crossterm-Apache-2.0.txt') -Force

[xml]$sparseXml = Get-Content $shellManifest
$sparseXml.Package.Identity.SetAttribute('Version', $packageVersion)
$sparseXml.Package.Identity.SetAttribute('Publisher', $cert.Subject)

$sparseManifest = Join-Path $shellPackageStage 'AppxManifest.xml'
$xmlSettings = [System.Xml.XmlWriterSettings]::new()
$xmlSettings.Encoding = [System.Text.UTF8Encoding]::new($false)
$xmlSettings.Indent = $true
$xmlWriter = [System.Xml.XmlWriter]::Create($sparseManifest, $xmlSettings)
try {
    $sparseXml.Save($xmlWriter)
} finally {
    $xmlWriter.Dispose()
}

$sparseAssets = Join-Path $shellPackageStage 'Assets'
New-Item -ItemType Directory -Force -Path $sparseAssets | Out-Null
foreach ($asset in @('StoreLogo.png', 'Square150x150Logo.png', 'Square44x44Logo.png')) {
    Copy-Item (Join-Path $repo "windows\CmuxGui\Assets\$asset") (Join-Path $sparseAssets $asset) -Force
}

$shellMsix = Join-Path $stage 'CmuxShellIntegration.msix'
& $makeappx pack /d $shellPackageStage /p $shellMsix /o /nv
if ($LASTEXITCODE -ne 0) { throw "sparse package creation failed ($LASTEXITCODE)" }

& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $shellMsix
if ($LASTEXITCODE -ne 0) { throw "sparse package signing failed ($LASTEXITCODE)" }

$shellCertificate = Join-Path $stage 'CmuxShellIntegration.cer'
Export-Certificate -Cert $cert -FilePath $shellCertificate -Force | Out-Null

$required = @(
    'CmuxGui.exe',
    'CmuxGui.dll',
    'CmuxGui.deps.json',
    'CmuxGui.runtimeconfig.json',
    'Assets\AppIcon.ico',
    'cmux_ffi.dll',
    'CmuxShellExtension.dll',
    'CmuxShellIntegration.msix',
    'CmuxShellIntegration.cer',
    'shell-package.ps1',
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
    "/DShellCertificateThumbprint=$($cert.Thumbprint)" `
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
