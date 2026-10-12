param(
    [Parameter(Mandatory)][ValidateSet('win-x64','linux-x64')][string]$Runtime,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($Destination)) { throw 'Choose an absolute output folder.' }
$bundleDirectory = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $bundleDirectory) { throw 'Choose a new output folder.' }
$setupProject = Join-Path $PSScriptRoot '../src/ArkServerManager.Setup/ArkServerManager.Setup.csproj'
& dotnet publish $setupProject -c Release -r $Runtime --self-contained true -o $bundleDirectory --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Setup publishing failed.' }
& (Join-Path $PSScriptRoot 'Build-InstallerPayload.ps1') -Runtime $Runtime -Destination (Join-Path $bundleDirectory 'payload')
$suffix = if ($Runtime -eq 'win-x64') { '.exe' } else { '' }
if (-not (Test-Path -LiteralPath (Join-Path $bundleDirectory "ArkServerManager.Installer$suffix") -PathType Leaf)) {
    throw 'Setup entry point is missing.'
}
Write-Output "Prepared $Runtime setup bundle. Standalone packaging, signing and prerequisite installation remain unfinished."
