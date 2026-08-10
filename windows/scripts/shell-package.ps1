[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Install', 'Uninstall')]
    [string]$Action,

    [string]$PackagePath,
    [string]$ExternalLocation,
    [string]$PackageName = 'cmux.Windows.ShellIntegration',
    [string]$LegacyPackageName = 'cmux.Windows'
)

$ErrorActionPreference = 'Stop'

if ($Action -eq 'Uninstall') {
    Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue |
        Remove-AppxPackage -ErrorAction Stop
    return
}

if ([string]::IsNullOrWhiteSpace($PackagePath) -or
    -not (Test-Path $PackagePath -PathType Leaf)) {
    throw "Shell integration package not found at '$PackagePath'."
}
if ([string]::IsNullOrWhiteSpace($ExternalLocation) -or
    -not (Test-Path $ExternalLocation -PathType Container)) {
    throw "Shell integration external location not found at '$ExternalLocation'."
}

if (-not [string]::IsNullOrWhiteSpace($LegacyPackageName)) {
    Get-AppxPackage -Name $LegacyPackageName -ErrorAction SilentlyContinue |
        Remove-AppxPackage -ErrorAction Stop
}

try {
    Add-AppxPackage `
        -Path $PackagePath `
        -ExternalLocation $ExternalLocation `
        -ForceApplicationShutdown `
        -ForceUpdateFromAnyVersion `
        -ErrorAction Stop
} catch {
    if ("$_" -notmatch '0x80073CFB|0x80073CF3|0x80073D06') {
        throw
    }

    Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue |
        Remove-AppxPackage -ErrorAction Stop
    Add-AppxPackage `
        -Path $PackagePath `
        -ExternalLocation $ExternalLocation `
        -ForceApplicationShutdown `
        -ErrorAction Stop
}
