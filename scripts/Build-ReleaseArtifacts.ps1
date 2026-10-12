param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Run release orchestration on Windows; Linux candidates are cross-built without native execution.' }
if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'Choose a normalized three-part release version.'
}
$releaseVersion = [Version]::Parse($Version)
if (@($releaseVersion.Major, $releaseVersion.Minor, $releaseVersion.Build) | Where-Object { $_ -gt 65534 }) {
    throw 'Release version components must be at most 65534.'
}
if (-not [IO.Path]::IsPathFullyQualified($Destination) -or (Test-Path -LiteralPath $Destination)) {
    throw 'Choose a new absolute release output folder.'
}
$outputRoot = [IO.Path]::GetFullPath($Destination)
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if ($outputRoot.Equals($sourceRoot, $comparison) -or
    $outputRoot.StartsWith([IO.Path]::TrimEndingDirectorySeparator($sourceRoot) + [IO.Path]::DirectorySeparatorChar, $comparison)) {
    throw 'Release output must be outside the source tree.'
}
for ($ancestor = [IO.Path]::GetDirectoryName($outputRoot); $null -ne $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Linked release output paths are unsupported.'
    }
}
$previousVersion = $env:ARK_RELEASE_VERSION
try {
    $env:ARK_RELEASE_VERSION = $Version
    [IO.Directory]::CreateDirectory($outputRoot) | Out-Null
    $releaseRoot = Join-Path $outputRoot 'release'
    [IO.Directory]::CreateDirectory($releaseRoot) | Out-Null
    & dotnet restore (Join-Path $sourceRoot 'ArkServerManager.slnx') --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Release restore failed.' }
    & dotnet build (Join-Path $sourceRoot 'ArkServerManager.slnx') -c Release --no-restore --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
    & dotnet test (Join-Path $sourceRoot 'tests/ArkServerManager.Core.Tests/ArkServerManager.Core.Tests.csproj') -c Release --no-build --no-restore --verbosity quiet --logger 'trx;LogFileName=release.trx' --results-directory (Join-Path $outputRoot 'test-results')
    if ($LASTEXITCODE -ne 0) { throw 'Release tests failed; no installer candidates will be built.' }
    foreach ($runtime in @('win-x64', 'linux-x64')) {
        $payload = Join-Path $outputRoot "payload-$runtime"
        & (Join-Path $PSScriptRoot 'Build-InstallerPayload.ps1') -Runtime $runtime -Destination $payload
        $installer = Join-Path $outputRoot "installer-$runtime"
        & (Join-Path $PSScriptRoot 'Build-StandaloneInstaller.ps1') -Runtime $runtime -Payload $payload -Destination $installer
        $installerName = if ($runtime -eq 'win-x64') { 'ARK-server-manager-setup-win-x64.exe' } else { 'ARK-server-manager-setup-linux-x64.tar.gz' }
        $installerFile = Join-Path $installer $installerName
        if (-not (Test-Path -LiteralPath $installerFile -PathType Leaf)) { throw "Missing $runtime installer candidate." }
        Copy-Item -LiteralPath $installerFile -Destination (Join-Path $releaseRoot $installerName)
        & (Join-Path $PSScriptRoot 'Build-AppUpdatePackage.ps1') -PayloadDirectory $payload -Runtime $runtime -DestinationDirectory $releaseRoot
    }
    $files = @(Get-ChildItem -LiteralPath $releaseRoot -File | Sort-Object Name | ForEach-Object {
        [ordered]@{ Name=$_.Name; Bytes=$_.Length; SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    if ($files.Count -ne 4) { throw 'Expected two installer and two automatic-update candidates.' }
    [ordered]@{ SchemaVersion=1; Version=$Version; NativeLinuxVerified=$false; Files=$files } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $releaseRoot 'release-manifest.json') -Encoding utf8
    $files | ForEach-Object { $_.SHA256 + '  ' + $_.Name } | Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding ascii
    Write-Output "Release candidates prepared in $releaseRoot. Publication and installed runtime verification are separate."
} finally { $env:ARK_RELEASE_VERSION = $previousVersion }
