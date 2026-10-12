param(
    [Parameter(Mandatory)][ValidateSet('win-x64','linux-x64')][string]$Runtime,
    [Parameter(Mandatory)][string]$Payload,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($Payload) -or -not [IO.Path]::IsPathFullyQualified($Destination)) { throw 'Choose absolute payload and output folders.' }
if (Test-Path -LiteralPath $Destination) { throw 'Choose a new output folder.' }
& (Join-Path $PSScriptRoot 'Test-InstallerPayload.ps1') -Directory $Payload -Runtime $Runtime
[IO.Directory]::CreateDirectory($Destination) | Out-Null
$archive = Join-Path $Destination 'embedded-payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($Payload, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
$project = Join-Path $PSScriptRoot '../src/ArkServerManager.Setup/ArkServerManager.Setup.csproj'
$published = Join-Path $Destination 'installer'
& dotnet publish $project -c Release -r $Runtime --self-contained true -o $published --verbosity quiet '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' "-p:InstallerPayloadZip=$archive"
if ($LASTEXITCODE -ne 0) { throw 'Standalone installer publishing failed.' }
$suffix = if ($Runtime -eq 'win-x64') { '.exe' } else { '' }
$entry = Join-Path $published "ArkServerManager.Installer$suffix"
if (-not (Test-Path -LiteralPath $entry -PathType Leaf)) { throw 'Standalone installer is missing.' }
Write-Output "Built standalone $Runtime installer: $entry"
Get-FileHash -LiteralPath $entry -Algorithm SHA256
if ($Runtime -eq 'win-x64') {
    $candidateInstaller = Join-Path $Destination 'ARK-server-manager-setup-win-x64.exe'
    Copy-Item -LiteralPath $entry -Destination $candidateInstaller
} else {
    $candidateInstaller = Join-Path $Destination 'ARK-server-manager-setup-linux-x64.tar.gz'
    $installerInput = [IO.File]::OpenRead($entry)
    $archiveOutput = [IO.File]::Open($candidateInstaller, [IO.FileMode]::CreateNew)
    $compression = [IO.Compression.GZipStream]::new($archiveOutput, [IO.Compression.CompressionLevel]::Optimal)
    $writer = [System.Formats.Tar.TarWriter]::new($compression, [System.Formats.Tar.TarEntryFormat]::Pax, $true)
    try {
        $installerEntry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, 'ArkServerManager.Installer')
        $installerEntry.Mode = [IO.UnixFileMode]493
        $installerEntry.DataStream = $installerInput
        $writer.WriteEntry($installerEntry)
    } finally {
        $writer.Dispose(); $compression.Dispose(); $archiveOutput.Dispose(); $installerInput.Dispose()
    }
}
Get-FileHash -LiteralPath $candidateInstaller -Algorithm SHA256
