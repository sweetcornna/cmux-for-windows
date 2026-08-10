<#
.SYNOPSIS
    Create and trust the local development signing certificate.

.DESCRIPTION
    Windows refuses to install an MSIX whose signer it does not trust, so a
    development build needs a certificate that is both usable for signing and
    present in CurrentUser\TrustedPeople.

    This is a self-signed certificate. It is only good for installing your own
    builds on your own machine: nobody else's Windows will trust it, and it is
    not a substitute for a CA-issued code signing certificate on a real release.
    Keeping trust in the current-user store preserves the per-user, no-elevation
    installer model.

.NOTES
    The subject must match Package.appxmanifest's Identity/@Publisher exactly,
    character for character, or packaging fails validation.
#>
[CmdletBinding()]
param(
    [string]$Subject = 'CN=cmux Development',
    [int]$Years = 3
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$signing = Join-Path $repo 'windows\signing'
New-Item -ItemType Directory -Force -Path $signing | Out-Null

$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey }
if ($existing) {
    Write-Host "reusing $($existing[0].Thumbprint) (expires $($existing[0].NotAfter))" -ForegroundColor Cyan
    $cert = $existing[0]
} else {
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $Subject `
        -KeyUsage DigitalSignature `
        -FriendlyName 'cmux development signing' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears($Years) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Write-Host "created $($cert.Thumbprint)" -ForegroundColor Green
}

# Export the public half only. The private key stays in the user's store so it
# never lands in the working tree.
$cerPath = Join-Path $signing 'cmux-dev.cer'
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null

Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
Write-Host "trusted $Subject for the current user; public cert at $cerPath" -ForegroundColor Green
