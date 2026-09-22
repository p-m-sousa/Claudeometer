[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Directory,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedPublisherSubject
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

foreach ($name in @('ClaudeUsage.exe', 'ClaudeUsage.Core.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $Directory $name) -PathType Leaf)) {
        throw "Required release binary is missing: $name"
    }
}

$binaries = @(Get-ChildItem -LiteralPath $Directory -File -Recurse |
    Where-Object { $_.Extension -in @('.exe', '.dll') })
foreach ($binary in $binaries) {
    $signature = Get-AuthenticodeSignature -LiteralPath $binary.FullName
    if ($signature.Status -ne 'Valid' -or $signature.SignatureType -ne 'Authenticode') {
        throw "Release binary does not have a valid embedded Authenticode signature: $($binary.Name) ($($signature.Status))."
    }
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -cne $ExpectedPublisherSubject) {
        throw "Release binary has an unexpected signing publisher: $($binary.Name)."
    }
    if ($null -eq $signature.TimeStamperCertificate) {
        throw "Release binary has no trusted timestamp: $($binary.Name)."
    }
    Write-Host "Verified $($binary.Name): $($signature.SignerCertificate.Subject)"
}
