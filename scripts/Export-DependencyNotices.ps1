param(
    [Parameter(Mandatory)][string[]]$AssetsFiles,
    [Parameter(Mandatory)][string]$Destination
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathFullyQualified($Destination) -or (Test-Path -LiteralPath $Destination)) {
    throw 'Choose a new absolute dependency-notice folder.'
}
$vendorRoot = Join-Path $PSScriptRoot '../third-party'
$vendors = @(Get-Content -LiteralPath (Join-Path $vendorRoot 'upstream-notices.json') -Raw | ConvertFrom-Json)
function Normalize-Repository([string]$Url) { return $Url.TrimEnd('/').Replace('.git', '') }
$packages = @{}
foreach ($assetsFile in $AssetsFiles) {
    $assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json
    $packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
    $libraries = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' })
    foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
        $references = @($framework.Value.frameworkReferences.PSObject.Properties.Name)
        foreach ($download in $framework.Value.downloadDependencies) {
            if (-not ($references | Where-Object { $download.name.StartsWith($_ + '.Runtime.', [StringComparison]::OrdinalIgnoreCase) })) { continue }
            if ($download.version -notmatch '^\[([0-9]+\.[0-9]+\.[0-9]+), \1\]$') { throw 'Runtime notice version must resolve exactly.' }
            $downloadVersion = $Matches[1]
            $downloadPath = $download.name.ToLowerInvariant() + '/' + $downloadVersion
            $downloadDirectory = $null
            foreach ($packageRoot in $packageRoots) {
                $candidate = Join-Path $packageRoot $downloadPath
                if (Test-Path -LiteralPath $candidate -PathType Container) { $downloadDirectory = $candidate; break }
            }
            if ($null -eq $downloadDirectory) { throw "Runtime dependency cache unavailable: $($download.name)" }
            $downloadFiles = @(Get-ChildItem -LiteralPath $downloadDirectory -File | ForEach-Object { $_.Name })
            $downloadHashFile = @(Get-ChildItem -LiteralPath $downloadDirectory -Filter '*.nupkg.sha512')
            if ($downloadHashFile.Count -ne 1) { throw 'Runtime package checksum unavailable.' }
            $libraries += [pscustomobject]@{Name=($download.name + '/' + $downloadVersion); Value=[pscustomobject]@{
                path=$downloadPath; files=$downloadFiles; sha512=([IO.File]::ReadAllText($downloadHashFile[0].FullName).Trim())
            }}
        }
    }
    foreach ($library in $libraries) {
        if ($packages.ContainsKey($library.Name)) { continue }
        $packageDirectory = $null
        foreach ($packageRoot in $packageRoots) {
            $candidate = Join-Path $packageRoot $library.Value.path
            if (Test-Path -LiteralPath $candidate -PathType Container) { $packageDirectory = $candidate; break }
        }
        if ($null -eq $packageDirectory) { throw "Dependency cache unavailable: $($library.Name)" }
        $nuspec = @(Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec')
        if ($nuspec.Count -ne 1) { throw "Dependency metadata unavailable: $($library.Name)" }
        [xml]$metadata = Get-Content -LiteralPath $nuspec[0].FullName -Raw
        $noticeFiles = @($library.Value.files | Where-Object { $_ -match '(?i)(^|/)(licen[cs]e|copying|third-party-notices|notice)([._-].*)?$' })
        $sources = @()
        foreach ($notice in $noticeFiles) {
            if ([IO.Path]::IsPathFullyQualified($notice) -or (($notice -split '[/\\]') -contains '..')) { throw 'Invalid dependency notice path.' }
            $sources += [ordered]@{ Original=$notice; Path=(Join-Path $packageDirectory $notice); Source='NuGet package' }
        }
        if ($sources.Count -eq 0) {
            $repo = Normalize-Repository ([string]$metadata.package.metadata.repository.url)
            $commit = [string]$metadata.package.metadata.repository.commit
            $matching = @($vendors | Where-Object {
                (Normalize-Repository ('https://github.com/' + $_.Repository)) -eq $repo -and $_.Commit -eq $commit
            })
            if ($matching.Count -ne 1) { throw "Review and vendor the upstream license for $($library.Name) at its recorded commit." }
            $vendorFile = Join-Path $vendorRoot $matching[0].File
            if ((Get-FileHash -LiteralPath $vendorFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $matching[0].SHA256) {
                throw "Vendored license changed: $($library.Name)"
            }
            $sources += [ordered]@{Original=$matching[0].File; Path=$vendorFile; Source=$matching[0].Source}
        }
        foreach ($source in $sources) {
            if (-not (Test-Path -LiteralPath $source.Path -PathType Leaf) -or (Get-Item -LiteralPath $source.Path).Length -eq 0) {
                throw "Dependency notice missing or empty: $($library.Name)"
            }
        }
        $packages[$library.Name] = [ordered]@{Package=$library.Name; ArchiveSHA512=$library.Value.sha512;
            License=[string]$metadata.package.metadata.license.InnerText; Copyright=[string]$metadata.package.metadata.copyright;
            Repository=[string]$metadata.package.metadata.repository.url; Commit=[string]$metadata.package.metadata.repository.commit; Sources=$sources}
    }
}
# Complete source validation precedes publication. Preserve each notice's exact bytes.
[IO.Directory]::CreateDirectory($Destination) | Out-Null
$inventory = foreach ($package in $packages.Values | Sort-Object Package) {
    $packageFolder = $package.Package.Replace('/', '-')
    $files = foreach ($source in $package.Sources) {
        $relative = $packageFolder + '/' + $source.Original.Replace('\', '/')
        $target = Join-Path $Destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $source.Path -Destination $target
        [ordered]@{File=$relative; Source=$source.Source; SHA256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
    [ordered]@{Package=$package.Package; ArchiveSHA512=$package.ArchiveSHA512; License=$package.License; Copyright=$package.Copyright;
        Repository=$package.Repository; Commit=$package.Commit; Notices=@($files)}
}
[ordered]@{SchemaVersion=1; Scope='Resolved NuGet dependencies, including build dependencies'; Packages=@($inventory)} |
    ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $Destination 'inventory.json') -Encoding utf8
Write-Output "Included notices for $($packages.Count) resolved dependency packages."
