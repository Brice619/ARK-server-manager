param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64')][string]$Runtime,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($Destination)) { throw 'Choose an absolute output folder.' }
$payloadDirectory = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $payloadDirectory) { throw 'Choose a new output folder to avoid stale installation files.' }
$project = Join-Path $PSScriptRoot '../src/ArkServerManager.Desktop/ArkServerManager.Desktop.csproj'
& dotnet publish $project -c Release -r $Runtime --self-contained true -o $payloadDirectory --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Payload publishing failed.' }
$launcherProject = Join-Path $PSScriptRoot '../src/ArkServerManager.Launcher/ArkServerManager.Launcher.csproj'
& dotnet publish $launcherProject -c Release -r $Runtime --self-contained true -o (Join-Path $payloadDirectory 'bootstrap-payload') --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'Recovery launcher publishing failed.' }
& (Join-Path $PSScriptRoot 'Export-DependencyNotices.ps1') -AssetsFiles @(
    (Join-Path $PSScriptRoot '../src/ArkServerManager.Desktop/obj/project.assets.json'),
    (Join-Path $PSScriptRoot '../src/ArkServerManager.Launcher/obj/project.assets.json'),
    (Join-Path $PSScriptRoot '../src/ArkServerManager.Host/obj/project.assets.json'),
    (Join-Path $PSScriptRoot '../src/ArkServerManager.Updater/obj/project.assets.json'),
    (Join-Path $PSScriptRoot '../src/ArkServerManager.WindowsSnapshot/obj/project.assets.json')
) -Destination (Join-Path $payloadDirectory 'licenses/third-party')
$suffix = if ($Runtime -eq 'win-x64') { '.exe' } else { '' }
$required = @("ArkServerManager.Desktop$suffix", 'System.Private.CoreLib.dll',
    "manager/ArkServerManager.Host$suffix", 'manager/ArkServerManager.Host.dll',
    'manager/System.Private.CoreLib.dll', 'manager/ArkServerManager.Host.runtimeconfig.json',
    "updater/ArkServerManager.Updater$suffix", 'updater/ArkServerManager.Updater.dll',
    'updater/System.Private.CoreLib.dll', 'updater/ArkServerManager.Updater.runtimeconfig.json')
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $payloadDirectory $relative) -PathType Leaf)) {
        throw "Required payload file missing: $relative"
    }
}
foreach ($configuration in @('ArkServerManager.Desktop.runtimeconfig.json', 'manager/ArkServerManager.Host.runtimeconfig.json', 'updater/ArkServerManager.Updater.runtimeconfig.json')) {
    $runtimeOptions = (Get-Content -LiteralPath (Join-Path $payloadDirectory $configuration) -Raw | ConvertFrom-Json).runtimeOptions
    if (-not $runtimeOptions.includedFrameworks) { throw "Runtime is not bundled: $configuration" }
}
$files = @(Get-ChildItem -LiteralPath $payloadDirectory -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path = [IO.Path]::GetRelativePath($payloadDirectory, $_.FullName).Replace('\','/');
        Bytes = $_.Length; SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{ SchemaVersion=1; Runtime=$Runtime; SelfContained=$true; Files=$files }
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payloadDirectory 'payload-manifest.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'Test-InstallerPayload.ps1') -Directory $payloadDirectory -Runtime $Runtime
Write-Output "Prepared $Runtime installer payload with $($files.Count) checked files."
