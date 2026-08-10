<#
.SYNOPSIS
    Build and sign CmuxGui as an MSIX.

.DESCRIPTION
    Builds the native Explorer command and produces a signed package under
    windows\dist using the single-project MSIX tooling built into the WinUI SDK.
    MSBuild owns the package layout on purpose:
    packing a publish drop by hand omits the compiled XAML (*.xbf) and the app's
    resource index, and the resulting app dies inside Microsoft.UI.Xaml.dll
    before Main runs.

    The version comes from Package.appxmanifest, so that file stays the single
    place a release version is written.

    The certificate is looked up in Cert:\CurrentUser\My by subject rather than
    read from a .pfx: the private key never has to sit in the working tree. Run
    windows\scripts\new-dev-cert.ps1 first if the lookup fails.

.PARAMETER Install
    Install the package after signing, replacing any previous version.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$CertSubject = 'CN=cmux Development',
    [switch]$Install
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$proj = Join-Path $repo 'windows\CmuxGui\CmuxGui.csproj'
$shellProj = Join-Path $repo 'windows\CmuxShellExtension\CmuxShellExtension.vcxproj'
$manifest = Join-Path $repo 'windows\CmuxGui\Package.appxmanifest'
$dist = Join-Path $repo 'windows\dist'

# --- tools ------------------------------------------------------------------

$msbuild = Get-ChildItem 'C:\Program Files (x86)\Microsoft Visual Studio' -Directory -EA SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName 'BuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe' } |
    Where-Object { Test-Path $_ } |
    Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild.exe not found.' }

# --- identity ---------------------------------------------------------------

[xml]$xml = Get-Content $manifest
$version = $xml.Package.Identity.Version
$name = $xml.Package.Identity.Name
$publisher = $xml.Package.Identity.Publisher

$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $CertSubject -and $_.HasPrivateKey } |
    Select-Object -First 1
if (-not $cert) {
    throw "No private-key certificate for '$CertSubject'. Run windows\scripts\new-dev-cert.ps1."
}
# Windows rejects the package outright if these disagree, and the error names
# neither file, so check it here where the fix is obvious.
if ($publisher -ne $cert.Subject) {
    throw "Package.appxmanifest Publisher '$publisher' does not match certificate subject '$($cert.Subject)'."
}

Write-Host "packaging $name $version ($Configuration), signing with $($cert.Thumbprint)" -ForegroundColor Cyan

# --- build ------------------------------------------------------------------

& $msbuild $shellProj `
    -p:Configuration=$Configuration `
    -p:Platform=x64 `
    -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "shell extension build failed ($LASTEXITCODE)" }

$shellDll = Join-Path $repo "windows\CmuxShellExtension\bin\x64\$Configuration\CmuxShellExtension.dll"
if (-not (Test-Path $shellDll -PathType Leaf)) {
    throw "shell extension build reported success but did not produce '$shellDll'."
}

Remove-Item $dist -Recurse -Force -EA SilentlyContinue
New-Item -ItemType Directory -Force -Path $dist | Out-Null

# WindowsPackageType overrides the csproj, which says None so an ordinary build
# runs straight out of bin\. Packaging that unpackaged output yields an app that
# has package identity yet still initializes the App SDK through the unpackaged
# bootstrapper, which fails at startup.
#
# -restore, not a separate call: the RID-specific runtime packs only resolve
# when restore runs with the same properties as the build.
& $msbuild $proj `
    -restore `
    -p:Configuration=$Configuration `
    -p:Platform=x64 `
    -p:RuntimeIdentifier=win-x64 `
    -p:SelfContained=true `
    -p:WindowsAppSDKSelfContained=true `
    -p:WindowsPackageType=MSIX `
    -p:GenerateAppxPackageOnBuild=true `
    -p:AppxBundle=Never `
    -p:UapAppxPackageBuildMode=SideloadOnly `
    -p:AppxPackageDir=$dist\ `
    -p:AppxPackageSigningEnabled=true `
    -p:PackageCertificateThumbprint=$($cert.Thumbprint) `
    -v:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw "build failed ($LASTEXITCODE)" }

$msix = Get-ChildItem $dist -Recurse -Filter *.msix |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $msix) { throw "build reported success but produced no .msix under $dist" }

Write-Host "signed $($msix.FullName)" -ForegroundColor Green

# --- install ----------------------------------------------------------------

if ($Install) {
    # The running app holds its own files open, and an in-place update over a
    # live process fails.
    Get-Process CmuxGui -EA SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
    try {
        Add-AppxPackage -Path $msix.FullName -ForceUpdateFromAnyVersion -ErrorAction Stop
    } catch {
        # Two failures that both mean "the installed package cannot be upgraded
        # in place, only replaced", and that no amount of -Force covers:
        #   0x80073CFB  same identity and version, different content
        #   0x80073CF3  same name, different architecture
        # A real version bump takes the upgrade path above instead.
        if ("$_" -notmatch '0x80073CFB|0x80073CF3') { throw }
        Write-Host 'cannot upgrade in place: replacing' -ForegroundColor Yellow
        Get-AppxPackage -Name $name | Remove-AppxPackage
        Add-AppxPackage -Path $msix.FullName -ErrorAction Stop
    }
    Write-Host "installed $version" -ForegroundColor Green
}
