param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][ValidateSet('win-x64','linux-x64')][string]$Runtime
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($Directory)) { throw 'Choose an absolute payload folder.' }
$payloadRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Directory))
$rootItem = Get-Item -LiteralPath $payloadRoot
if (-not $rootItem.PSIsContainer -or ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Payload must be a regular folder.'
}
$allItems = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -Force)
if ($allItems | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
    throw 'Linked payload files or folders are unsupported.'
}
$manifestFile = Join-Path $payloadRoot 'payload-manifest.json'
if ((Get-Item -LiteralPath $manifestFile).Length -gt 4MB) { throw 'Payload manifest exceeds the size limit.' }
$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
if ($manifest.SchemaVersion -ne 1 -or $manifest.Runtime -cne $Runtime -or $manifest.SelfContained -ne $true -or -not $manifest.Files) {
    throw 'Unsupported payload manifest or platform.'
}
$comparison = if ($IsWindows) { [StringComparer]::OrdinalIgnoreCase } else { [StringComparer]::Ordinal }
$paths = [Collections.Generic.HashSet[string]]::new($comparison)
foreach ($entry in $manifest.Files) {
    $relative = $entry.Path
    if ($relative -isnot [string] -or [string]::IsNullOrWhiteSpace($relative) -or $relative.Contains('\') -or
        $relative.Contains(':') -or $relative.StartsWith('/') -or ($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') })) {
        throw 'Invalid file path in payload manifest.'
    }
    if (-not $paths.Add($relative) -or $relative -eq 'payload-manifest.json') { throw 'Duplicate or reserved payload path.' }
    if ($entry.Bytes -isnot [long] -and $entry.Bytes -isnot [int]) { throw 'Invalid payload file size.' }
    if ($entry.Bytes -lt 0 -or $entry.SHA256 -isnot [string] -or $entry.SHA256 -cnotmatch '^[0-9a-f]{64}$') {
        throw 'Invalid payload file integrity data.'
    }
    $filePath = Join-Path $payloadRoot $relative
    $file = Get-Item -LiteralPath $filePath
    if ($file.PSIsContainer -or $file.Length -ne $entry.Bytes) { throw "Missing or changed payload file: $relative" }
    if ((Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.SHA256) {
        throw "Payload checksum mismatch: $relative"
    }
}
foreach ($file in $allItems | Where-Object { -not $_.PSIsContainer }) {
    $relative = [IO.Path]::GetRelativePath($payloadRoot, $file.FullName).Replace('\','/')
    if ($relative -ne 'payload-manifest.json' -and -not $paths.Contains($relative)) { throw "Unlisted payload file: $relative" }
}
$suffix = if ($Runtime -eq 'win-x64') { '.exe' } else { '' }
foreach ($required in @("ArkServerManager.Desktop$suffix", 'ArkServerManager.Desktop.runtimeconfig.json',
    'System.Private.CoreLib.dll', "manager/ArkServerManager.Host$suffix", 'manager/ArkServerManager.Host.dll',
    'manager/ArkServerManager.Host.runtimeconfig.json', 'manager/System.Private.CoreLib.dll',
    "updater/ArkServerManager.Updater$suffix", 'updater/ArkServerManager.Updater.dll',
    'updater/ArkServerManager.Updater.runtimeconfig.json', 'updater/System.Private.CoreLib.dll',
    'licenses/ARKServerManager.LICENSE.txt', 'licenses/third-party/inventory.json')) {
    if (-not $paths.Contains($required)) { throw "Required component absent from manifest: $required" }
}
$updaterOptions = (Get-Content -LiteralPath (Join-Path $payloadRoot 'updater/ArkServerManager.Updater.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions
if (-not $updaterOptions.includedFrameworks) { throw 'Updater runtime is not bundled.' }
if ($Runtime -eq 'win-x64') {
    foreach ($required in @('ArkServerManager.WindowsSnapshot.exe', 'ArkServerManager.WindowsSnapshot.runtimeconfig.json',
        'System.Private.CoreLib.dll', 'AlphaVSS.Common.dll', 'third-party/AlphaVSS-LICENSE.txt',
        'third-party/NOTICE.txt', 'third-party/Ijwhost-DotNet-LICENSE.txt', 'third-party/Ijwhost-DotNet-THIRD-PARTY-NOTICES.txt')) {
        if (-not $paths.Contains("manager/WindowsSnapshot/$required")) { throw "Required snapshot component absent: $required" }
    }
    $snapshotOptions = (Get-Content -LiteralPath (Join-Path $payloadRoot 'manager/WindowsSnapshot/ArkServerManager.WindowsSnapshot.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions
    if (-not $snapshotOptions.includedFrameworks) { throw 'Snapshot helper runtime is not bundled.' }
}
Write-Output "Verified $Runtime payload: $($paths.Count) file sizes and checksums match."
