[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishedDirectory,
    [Parameter(Mandatory)][string]$DestinationZip,
    [string]$TerrainDirectory = (Join-Path $env:LOCALAPPDATA 'Skypod\AtlasTerrain')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$files = [System.Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
function Add-UserFile([string]$Name, [string]$Root) {
    if ([IO.Path]::IsPathRooted($Name) -or $Name.Contains('\') -or '..' -in $Name.Split('/')) { throw "Invalid package path: $Name" }
    if ([IO.Path]::GetExtension($Name) -in @('.pdb','.cs','.cpp','.ps1','.py','.log','.bak','.lib')) { throw "Unexpected package file: $Name" }
    $source = Join-Path $Root $Name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing required Studio file: $Name" }
    $files.Add($Name, $source)
}
$manifest = Get-Content -LiteralPath (Join-Path $projectRoot 'packaging\StudioFiles.json') -Raw | ConvertFrom-Json
foreach ($name in $manifest) { Add-UserFile $name $PublishedDirectory }
$catalogPath = Join-Path $TerrainDirectory 'catalog.json'
$catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json
$mapFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$null = $mapFiles.Add('catalog.json')
foreach ($region in $catalog) {
    foreach ($property in @('tiles','overlays')) {
        if ($region.PSObject.Properties.Name -notcontains $property) { continue }
        foreach ($tile in $region.$property) {
            foreach ($channel in @('color','normal','water')) {
                if ($tile.PSObject.Properties.Name -notcontains $channel -or -not $tile.$channel) { continue }
                $name = [string]$tile.$channel
                if ([IO.Path]::GetExtension($name) -ne '.png') { throw "Unexpected map format: $name" }
                $null = $mapFiles.Add($name)
            }
        }
    }
}
foreach ($name in $mapFiles) {
    if ([IO.Path]::IsPathRooted($name) -or $name.Contains('\') -or '..' -in $name.Split('/')) { throw "Invalid map path: $name" }
    $source = Join-Path $TerrainDirectory $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing map file: $name" }
    $files.Add("AtlasTerrain/$name", $source)
}
# Independent requirements catch omissions from the package manifest.
$required = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in @('SOURCES.txt','Licenses/SkyPod/Skypod-1.0.0-Source.zip','LICENSE.txt','NOTICE.txt','Skypod.Studio.exe','Skypod.Studio.dll','Skypod.Core.dll',
    'Skypod.Studio.deps.json','Skypod.Studio.runtimeconfig.json','tools/Skypod.AudioEncoder.exe',
    'AtlasTerrain/catalog.json','Licenses/FFmpeg-COPYING.LGPLv2.1.txt',
    'Licenses/FFmpeg/Skypod-AudioEncoder-8.1.3-Source.tar.xz')) { $null = $required.Add($name) }
$deps = Get-Content -LiteralPath (Join-Path $PublishedDirectory 'Skypod.Studio.deps.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($component in $deps.targets[$deps.runtimeTarget.name].Values) {
    foreach ($kind in @('runtime','native','resources')) {
        if (-not $component.ContainsKey($kind)) { continue }
        foreach ($entry in $component[$kind].GetEnumerator()) {
            if ($entry.Key.EndsWith('/_._')) { continue }
            $name = [IO.Path]::GetFileName($entry.Key)
            if ($kind -eq 'resources') { $name = "$($entry.Value.locale)/$name" }
            $null = $required.Add($name)
        }
    }
}
foreach ($name in $required) { if (-not $files.ContainsKey($name)) { throw "Package dependency missing: $name" } }
& (Join-Path $PublishedDirectory 'tools\Skypod.AudioEncoder.exe') -version
if ($LASTEXITCODE -ne 0) { throw 'Bundled encoder could not start.' }
$destination = [IO.Path]::GetFullPath($DestinationZip)
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
$temporary = "$destination.$([guid]::NewGuid().ToString('N')).tmp"
try {
    $archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in ($files.Keys | Sort-Object)) {
            $compression = if ($name.EndsWith('.png')) { [IO.Compression.CompressionLevel]::NoCompression } else { [IO.Compression.CompressionLevel]::Optimal }
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $files[$name], $name, $compression)
        }
    } finally { $archive.Dispose() }
    $archive = [IO.Compression.ZipFile]::OpenRead($temporary)
    try {
        if ($archive.Entries.Count -ne $files.Count) { throw 'Archive file count mismatch.' }
        foreach ($entry in $archive.Entries) {
            $stream = $entry.Open()
            try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            $expected = (Get-FileHash -LiteralPath $files[$entry.FullName] -Algorithm SHA256).Hash
            if ($actual -ne $expected) { throw "Archive verification failed: $($entry.FullName)" }
        }
    } finally { $archive.Dispose() }
    [IO.File]::Move($temporary, $destination, $true)
} finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
Write-Host "Studio ZIP verified: $destination ($($files.Count) files)"
