<#
.SYNOPSIS
    Unpacks the ToupTek SDK zip into lib/, include/ and reference/, which is how a vendor drop is taken.

.DESCRIPTION
    Re-run against a new zip, then commit what changed: it is the one way those three folders get
    filled, and it records which zip it filled them from in lib/SOURCE.txt, so the repository always
    says which vendor drop it carries.

    lib/<rid>/ is named by .NET runtime identifier, which is what the csproj maps from. Two vendor
    layouts are renamed on the way:
      - linux/arm64 ships glibc and musl builds side by side; they land as linux-arm64 and
        linux-musl-arm64, the RIDs .NET uses for those two C libraries.
      - linux/armhf is the RID linux-arm. linux/armel (soft-float ARMv5) has no .NET RID and is skipped.
    The macOS dylib is a universal binary (x86_64 + arm64), so it lands once, as lib/osx.

.PARAMETER Zip
    The vendor zip. Defaults to the newest toupcamsdk.*.zip in ~/Downloads.
#>
param(
    [string] $Zip
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not $Zip) {
    $Zip = Get-ChildItem -Path (Join-Path $HOME 'Downloads') -Filter 'toupcamsdk.*.zip' |
        Sort-Object Name -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Zip) { throw "No toupcamsdk.*.zip in ~/Downloads; pass -Zip." }
}

$root = Split-Path -Parent $PSScriptRoot
$map = [ordered]@{
    'win/x64/toupcam.dll'             = 'lib/win-x64/toupcam.dll'
    'win/x86/toupcam.dll'             = 'lib/win-x86/toupcam.dll'
    'win/arm64/toupcam.dll'           = 'lib/win-arm64/toupcam.dll'
    'linux/x64/libtoupcam.so'         = 'lib/linux-x64/libtoupcam.so'
    'linux/x86/libtoupcam.so'         = 'lib/linux-x86/libtoupcam.so'
    'linux/arm64/glibc/libtoupcam.so' = 'lib/linux-arm64/libtoupcam.so'
    'linux/arm64/musl/libtoupcam.so'  = 'lib/linux-musl-arm64/libtoupcam.so'
    'linux/armhf/libtoupcam.so'       = 'lib/linux-arm/libtoupcam.so'
    'mac/libtoupcam.dylib'            = 'lib/osx/libtoupcam.dylib'
    'linux/udev/99-toupcam.rules'     = 'lib/udev/99-toupcam.rules'
    'inc/toupcam.h'                   = 'include/toupcam.h'
    'dotnet/toupcam.cs'               = 'reference/toupcam.cs'
    'doc/en.html'                     = 'reference/en.html'
}

$archive = [System.IO.Compression.ZipFile]::OpenRead($Zip)
try {
    foreach ($entry in $map.GetEnumerator()) {
        $source = $archive.GetEntry($entry.Key)
        if (-not $source) { throw "The zip has no $($entry.Key); the vendor layout changed, update the map." }
        $target = Join-Path $root $entry.Value
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($source, $target, $true)
        '{0,-34} -> {1} ({2:N0} bytes)' -f $entry.Key, $entry.Value, $source.Length
    }
}
finally {
    $archive.Dispose()
}

$hash = (Get-FileHash -Algorithm SHA256 -Path $Zip).Hash.ToLowerInvariant()
@(
    "zip:    $(Split-Path -Leaf $Zip)"
    "sha256: $hash"
    "at:     $((Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ'))"
) | Set-Content -Path (Join-Path $root 'lib/SOURCE.txt') -Encoding utf8
"wrote lib/SOURCE.txt"
