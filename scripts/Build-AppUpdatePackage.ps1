param(
    [Parameter(Mandatory)][string]$PayloadDirectory,
    [Parameter(Mandatory)][ValidateSet('win-x64','linux-x64')][string]$Runtime,
    [Parameter(Mandatory)][string]$DestinationDirectory
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($PayloadDirectory) -or -not [IO.Path]::IsPathFullyQualified($DestinationDirectory)) {
    throw 'Choose absolute payload and output folders.'
}
$sourceDirectory = [IO.Path]::GetFullPath($PayloadDirectory)
$outputDirectory = [IO.Path]::GetFullPath($DestinationDirectory)
$pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if ($outputDirectory.Equals($sourceDirectory, $pathComparison) -or
    $outputDirectory.StartsWith([IO.Path]::TrimEndingDirectorySeparator($sourceDirectory) + [IO.Path]::DirectorySeparatorChar, $pathComparison)) {
    throw 'The update package output must be outside the payload folder.'
}
if (-not (Test-Path -LiteralPath $outputDirectory -PathType Container)) { throw 'Choose an existing output folder.' }
for ($itemPath = $outputDirectory; $null -ne $itemPath; $itemPath = [IO.Path]::GetDirectoryName($itemPath)) {
    if ((Get-Item -LiteralPath $itemPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked output folders are unsupported.' }
}
& (Join-Path $PSScriptRoot 'Test-InstallerPayload.ps1') -Directory $sourceDirectory -Runtime $Runtime
$packagePath = Join-Path $outputDirectory "ARK-server-manager-$Runtime.zip"
if (Test-Path -LiteralPath $packagePath) { throw 'The update package already exists.' }
$temporaryPath = Join-Path $outputDirectory ('.ark-package-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    [IO.Compression.ZipFile]::CreateFromDirectory($sourceDirectory, $temporaryPath, [IO.Compression.CompressionLevel]::Optimal, $false)
    $updateArchive = [IO.Compression.ZipFile]::Open($temporaryPath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        foreach ($bootstrapEntry in @($updateArchive.Entries | Where-Object { $_.FullName.StartsWith('bootstrap-payload/', [StringComparison]::OrdinalIgnoreCase) })) { $bootstrapEntry.Delete() }
        $updateManifest = Get-Content -LiteralPath (Join-Path $sourceDirectory 'payload-manifest.json') -Raw | ConvertFrom-Json
        $updateManifest.Files = @($updateManifest.Files | Where-Object { -not $_.Path.StartsWith('bootstrap-payload/', [StringComparison]::OrdinalIgnoreCase) })
        $updateArchive.GetEntry('payload-manifest.json').Delete()
        $manifestEntry = $updateArchive.CreateEntry('payload-manifest.json')
        $manifestWriter = [IO.StreamWriter]::new($manifestEntry.Open(), [Text.UTF8Encoding]::new($false))
        try { $manifestWriter.Write(($updateManifest | ConvertTo-Json -Depth 6)) } finally { $manifestWriter.Dispose() }
    } finally { $updateArchive.Dispose() }
    [IO.File]::Move($temporaryPath, $packagePath, $false)
    [ordered]@{ Package=$packagePath; Bytes=(Get-Item -LiteralPath $packagePath).Length;
        SHA256=(Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant() } | ConvertTo-Json
} finally {
    if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) { Remove-Item -LiteralPath $temporaryPath }
}
