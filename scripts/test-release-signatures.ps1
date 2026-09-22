[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$buildDirectory = Join-Path $repositoryRoot 'src/ClaudeUsage.WinForms/bin/Release/net48'
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('ClaudeUsageSigningTests-' + [guid]::NewGuid().ToString('N'))

function Assert-Rejected([scriptblock]$Action, [string]$ExpectedMessage) {
    try {
        & $Action
    } catch {
        if ($_.Exception.Message -like $ExpectedMessage) { return }
        throw
    }
    throw "Expected signing verification to reject: $ExpectedMessage"
}

try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Copy-Item -LiteralPath (Join-Path $buildDirectory 'ClaudeUsage.exe') -Destination $scratch
    Assert-Rejected {
        & "$PSScriptRoot/verify-release-signatures.ps1" -Directory $scratch -ExpectedPublisherSubject 'CN=Test Publisher'
    } 'Required release binary is missing: ClaudeUsage.Core.dll'

    Copy-Item -LiteralPath (Join-Path $buildDirectory 'ClaudeUsage.Core.dll') -Destination $scratch
    Assert-Rejected {
        & "$PSScriptRoot/verify-release-signatures.ps1" -Directory $scratch -ExpectedPublisherSubject 'CN=Test Publisher'
    } '*does not have a valid embedded Authenticode signature*'

    $packages = Join-Path $scratch 'packages'
    Assert-Rejected {
        & "$PSScriptRoot/package-release.ps1" -NoBuild -Version signing-test -OutputDirectory $packages `
            -RequireSigned -ExpectedPublisherSubject 'CN=Test Publisher'
    } '*does not have a valid embedded Authenticode signature*'
    if (@(Get-ChildItem -LiteralPath $packages -Filter '*.zip' -File).Count -ne 0) {
        throw 'Unsigned release packaging must not produce a ZIP.'
    }
    Write-Host 'PASS: missing or unsigned binaries cannot be published as signed releases.'
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
