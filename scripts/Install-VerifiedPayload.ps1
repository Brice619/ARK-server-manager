param(
    [Parameter(Mandatory)][string]$Payload,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][ValidateSet('win-x64','linux-x64')][string]$Runtime
)
$ErrorActionPreference = 'Stop'
foreach ($selectedPath in @($Payload, $Destination)) {
    if (-not [IO.Path]::IsPathFullyQualified($selectedPath)) { throw 'Choose absolute installation folders.' }
}
$payloadDirectory = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Payload))
$installationDirectory = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Destination))
if ($installationDirectory -eq [IO.Path]::GetPathRoot($installationDirectory)) { throw 'Choose a dedicated folder.' }
if (Test-Path -LiteralPath $installationDirectory) { throw 'The installation folder already exists. Existing installations require an update workflow.' }
$installationParent = [IO.Path]::GetDirectoryName($installationDirectory)
if (-not (Test-Path -LiteralPath $installationParent -PathType Container)) { throw 'Choose an existing parent folder.' }
for ($ancestor = [IO.DirectoryInfo]::new($installationParent); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked installation locations require separate handling.' }
}
& (Join-Path $PSScriptRoot 'Test-InstallerPayload.ps1') -Directory $payloadDirectory -Runtime $Runtime
$stagingDirectory = Join-Path $installationParent ('.ark-install-' + [Guid]::NewGuid().ToString('N'))
$createdStaging = $false
try {
    New-Item -ItemType Directory -Path $stagingDirectory | Out-Null
    $createdStaging = $true
    foreach ($payloadItem in Get-ChildItem -LiteralPath $payloadDirectory -Force) {
        Copy-Item -LiteralPath $payloadItem.FullName -Destination $stagingDirectory -Recurse
    }
    # Recheck copied bytes before adding installer-owned settings and publishing the folder.
    & (Join-Path $PSScriptRoot 'Test-InstallerPayload.ps1') -Directory $stagingDirectory -Runtime $Runtime
    $bootstrap = @{ SchemaVersion = 1; ServerRoot = $installationDirectory } | ConvertTo-Json
    $bootstrapPath = Join-Path $stagingDirectory 'installation.json'
    if (Test-Path -LiteralPath $bootstrapPath) { throw 'The payload contains reserved installation settings.' }
    [IO.File]::WriteAllText($bootstrapPath, $bootstrap, [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $installationDirectory) { throw 'The installation folder was created by another operation.' }
    # Same-parent rename publishes the complete application and its chosen server root together.
    [IO.Directory]::Move($stagingDirectory, $installationDirectory)
    $createdStaging = $false
    [pscustomobject]@{ Installed = $true; Directory = $installationDirectory; Runtime = $Runtime }
}
finally {
    if ($createdStaging -and (Test-Path -LiteralPath $stagingDirectory)) {
        $resolvedStaging = [IO.Path]::GetFullPath($stagingDirectory)
        if ([IO.Path]::GetDirectoryName($resolvedStaging) -ne $installationParent -or
            -not [IO.Path]::GetFileName($resolvedStaging).StartsWith('.ark-install-')) {
            throw 'Installation staging cleanup path is invalid.'
        }
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
