[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishedDirectory,
    [Parameter(Mandatory)][string]$DestinationZip
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$names = Get-Content -LiteralPath (Join-Path $projectRoot 'packaging\MainModFiles.json') -Raw | ConvertFrom-Json
$seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($name in $names) {
    if ([IO.Path]::IsPathRooted($name) -or $name.Contains('\') -or '..' -in $name.Split('/')) { throw "Invalid package path: $name" }
    if (-not $seen.Add($name)) { throw "Duplicate package file: $name" }
    if (-not (Test-Path -LiteralPath (Join-Path $PublishedDirectory $name) -PathType Leaf)) { throw "Missing package file: $name" }
}
foreach ($name in @('Skypod.esp','SKSE/Plugins/Skypod.dll','Scripts/Skypod_MCM.pex','LICENSE.txt','NOTICE.txt','SOURCES.txt','Licenses/SkyPod/CommonLibSSE-LICENSE.txt')) {
    if (-not $seen.Contains($name)) { throw "Required file missing from package manifest: $name" }
}
$destination = [IO.Path]::GetFullPath($DestinationZip)
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
$temporary = "$destination.$([guid]::NewGuid().ToString('N')).tmp"
try {
    $archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $names) {
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $PublishedDirectory $name), $name, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
    $archive = [IO.Compression.ZipFile]::OpenRead($temporary)
    try {
        if ($archive.Entries.Count -ne $names.Count) { throw 'Archive entry count mismatch.' }
        foreach ($entry in $archive.Entries) {
            $stream = $entry.Open()
            try { $actual = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            $expected = (Get-FileHash -LiteralPath (Join-Path $PublishedDirectory $entry.FullName) -Algorithm SHA256).Hash
            if ($actual -ne $expected) { throw "Archive verification failed: $($entry.FullName)" }
        }
    } finally { $archive.Dispose() }
    [IO.File]::Move($temporary, $destination, $true)
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
Write-Host "Main mod ZIP verified: $destination ($($names.Count) files)"
