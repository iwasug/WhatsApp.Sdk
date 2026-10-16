<#
.SYNOPSIS
    Post-pack content gate for the sdk-wa release pipeline.

.DESCRIPTION
    Asserts, read-only, that a fresh `dotnet pack` produced exactly the two
    intended NuGet packages with the expected contents. The release workflow
    (release.yml) runs this script IMMEDIATELY BEFORE the irreversible
    `dotnet nuget push`; a throw exits non-zero under `pwsh -File` and stops
    the push.

    Assertions:
      1. The artifacts directory exists, is a directory, and is not empty
         (fail closed: an absent package is a failure, never a pass).
      2. Exactly two .nupkg files are present.
      3. They are named Iwasug.WhatsApp.Sdk.<version>.nupkg and
         Iwasug.WhatsApp.Sdk.Testing.<version>.nupkg - anything else
         (PairingSample, InteropProbe, strays) is a failure.
      4. Both versions are identical and match the plain release shape
         ^0\.1\.\d+$ (no -g<sha> suffix, no build metadata).
      5. Each nupkg contains a README.md entry.

    No network access; the artifacts directory is never mutated (zips are
    opened read-only and never extracted).

.PARAMETER ArtifactsPath
    Directory passed to `dotnet pack -o`, e.g. `artifacts`.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ArtifactsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$message) {
    throw "verify-pack: $message"
}

function Get-PackageEntries([string]$nupkgPath) {
    if (-not ('System.IO.Compression.ZipFile' -as [type])) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
    }
    $zip = [System.IO.Compression.ZipFile]::OpenRead($nupkgPath)
    try {
        $entries = @()
        foreach ($entry in $zip.Entries) { $entries += $entry.FullName }
        return $entries
    }
    finally {
        $zip.Dispose()
    }
}

# --- Assertion 1: artifacts directory present, a directory, non-empty --------
if (-not (Test-Path -LiteralPath $ArtifactsPath -PathType Container)) {
    Fail "assertion 1: artifacts path '$ArtifactsPath' does not exist or is not a directory"
}
$nupkgs = @(Get-ChildItem -LiteralPath $ArtifactsPath -Filter '*.nupkg' -File)
if ($nupkgs.Count -eq 0) {
    Fail "assertion 1: artifacts directory '$ArtifactsPath' is empty (no .nupkg files) - failing closed"
}

# --- Assertion 2: exactly two .nupkg files -----------------------------------
if ($nupkgs.Count -ne 2) {
    $found = ($nupkgs | ForEach-Object { $_.Name }) -join ', '
    Fail "assertion 2: expected exactly 2 .nupkg files, found $($nupkgs.Count): $found"
}

# --- Assertion 3: exactly the two expected package IDs ----------------------
$sdkPattern = '^Iwasug\.WhatsApp\.Sdk\.(?<version>\d+\.\d+\.\d+[0-9A-Za-z.\-+]*)\.nupkg$'
$testingPattern = '^Iwasug\.WhatsApp\.Sdk\.Testing\.(?<version>\d+\.\d+\.\d+[0-9A-Za-z.\-+]*)\.nupkg$'
$sdkPackages = @()
$testingPackages = @()
foreach ($file in $nupkgs) {
    if ($file.Name -match $testingPattern) {
        $testingPackages += [pscustomobject]@{ File = $file; Version = $Matches['version'] }
    }
    elseif ($file.Name -match $sdkPattern) {
        $sdkPackages += [pscustomobject]@{ File = $file; Version = $Matches['version'] }
    }
    else {
        Fail "assertion 3: unexpected package '$($file.Name)' - only Iwasug.WhatsApp.Sdk.<version>.nupkg and Iwasug.WhatsApp.Sdk.Testing.<version>.nupkg may be produced"
    }
}
if ($sdkPackages.Count -ne 1) {
    Fail "assertion 3: expected exactly one Iwasug.WhatsApp.Sdk.<version>.nupkg, found $($sdkPackages.Count)"
}
if ($testingPackages.Count -ne 1) {
    Fail "assertion 3: expected exactly one Iwasug.WhatsApp.Sdk.Testing.<version>.nupkg, found $($testingPackages.Count)"
}

# --- Assertion 4: identical, plain-release version on both packages ---------
if ($sdkPackages[0].Version -ne $testingPackages[0].Version) {
    Fail "assertion 4: package versions differ: SDK=$($sdkPackages[0].Version), Testing=$($testingPackages[0].Version)"
}
$version = $sdkPackages[0].Version
if ($version -notmatch '^0\.1\.\d+$') {
    Fail "assertion 4: version '$version' is not the plain release shape ^0\.1\.\d+$ (a -g<sha> suffix or build metadata means the ref is not in publicReleaseRefSpec)"
}

# --- Assertion 5: README.md entry inside each nupkg -------------------------
foreach ($package in @($sdkPackages[0], $testingPackages[0])) {
    $entries = Get-PackageEntries $package.File.FullName
    if ($entries -notcontains 'README.md') {
        Fail "assertion 5: '$($package.File.Name)' has no README.md entry (PackageReadmeFile is not being packed)"
    }
}

Write-Output "verify-pack: OK - $version; 2 packages verified ($($sdkPackages[0].File.Name), $($testingPackages[0].File.Name))"
