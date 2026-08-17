[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Version = '0.2.0',

    [string]$TargetFramework = 'net48',

    [string]$Project = 'src/ClaudeUsage.WinForms/ClaudeUsage.WinForms.csproj',

    [string]$OutputDirectory = 'artifacts',

    [switch]$NoBuild,

    [switch]$IncludeSymbols
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') {
    throw "Version '$Version' contains characters that are unsafe in an artifact name."
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = if ([System.IO.Path]::IsPathRooted($Project)) {
    [System.IO.Path]::GetFullPath($Project)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Project))
}

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "Project not found: $projectPath"
}

if (-not $NoBuild) {
    & dotnet build $projectPath --configuration $Configuration -p:Version=$Version
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE."
    }
}

$projectDirectory = Split-Path -Parent $projectPath
$buildDirectory = Join-Path (Join-Path (Join-Path $projectDirectory 'bin') $Configuration) $TargetFramework
$executablePath = Join-Path $buildDirectory 'ClaudeUsage.exe'

if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
    throw "Expected build output not found: $executablePath"
}

$artifactRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null

$artifactBaseName = "ClaudeUsage-$Version-win-anycpu"
$stageDirectory = Join-Path $artifactRoot $artifactBaseName
$archivePath = Join-Path $artifactRoot "$artifactBaseName.zip"
$checksumPath = "$archivePath.sha256"

$expectedStagePrefix = $artifactRoot.TrimEnd([char[]]@(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar
)) + [System.IO.Path]::DirectorySeparatorChar
if (-not $stageDirectory.StartsWith($expectedStagePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to stage outside the artifact directory: $stageDirectory"
}

if (Test-Path -LiteralPath $stageDirectory) {
    Remove-Item -LiteralPath $stageDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
if (Test-Path -LiteralPath $checksumPath) {
    Remove-Item -LiteralPath $checksumPath -Force
}

New-Item -ItemType Directory -Path $stageDirectory | Out-Null

$runtimePatterns = @(
    'ClaudeUsage.exe',
    'ClaudeUsage.exe.config',
    '*.deps.json',
    '*.runtimeconfig.json'
)

foreach ($pattern in $runtimePatterns) {
    Get-ChildItem -LiteralPath $buildDirectory -Filter $pattern -File -ErrorAction SilentlyContinue |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $stageDirectory -Force }
}

# Include only runtime assemblies copied beside the executable. Framework and
# reference assemblies are not part of the app's portable payload.
Get-ChildItem -LiteralPath $buildDirectory -Filter '*.dll' -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'ClaudeUsage.*.dll' } |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $stageDirectory -Force }

if ($IncludeSymbols) {
    Get-ChildItem -LiteralPath $buildDirectory -Filter '*.pdb' -File -ErrorAction SilentlyContinue |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $stageDirectory -Force }
}

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging/install.cmd') -Destination $stageDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging/uninstall.cmd') -Destination $stageDirectory
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging/README-WINDOWS.txt') -Destination (Join-Path $stageDirectory 'README.txt')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $stageDirectory 'LICENSE.txt')
Set-Content -LiteralPath (Join-Path $stageDirectory 'VERSION') -Value $Version -Encoding ASCII

Compress-Archive -Path (Join-Path $stageDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $checksumPath -Value "$hash  $([System.IO.Path]::GetFileName($archivePath))" -Encoding ASCII

Write-Host "Created $archivePath"
Write-Host "Created $checksumPath"
