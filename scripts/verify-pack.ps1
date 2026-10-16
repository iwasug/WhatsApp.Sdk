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
      5. Each nupkg contains README.md, LICENSE, icon.png, and, under lib/,
         the Iwasug.WhatsApp.Sdk* assembly plus its XML doc file.
      6. Each nupkg has its sibling .snupkg (exactly two, with matching
         package IDs and versions; the symbols ride the same push).

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

# --- Assertion 5: required content entries inside each nupkg ----------------
$requiredItems = @('README.md', 'LICENSE', 'icon.png')
foreach ($package in @($sdkPackages[0], $testingPackages[0])) {
    $entries = Get-PackageEntries $package.File.FullName
    foreach ($required in $requiredItems) {
        if ($entries -notcontains $required) {
            Fail "assertion 5: '$($package.File.Name)' has no $required entry"
        }
    }
    $libAssemblies = @($entries | Where-Object { $_ -match '^lib/[^/]+/Iwasug\.WhatsApp\.Sdk[^/]*\.dll$' })
    if ($libAssemblies.Count -eq 0) {
        Fail "assertion 5: '$($package.File.Name)' has no lib/*/Iwasug.WhatsApp.Sdk*.dll assembly entry"
    }
    foreach ($assembly in $libAssemblies) {
        $xmlEntry = $assembly -replace '\.dll$', '.xml'
        if ($entries -notcontains $xmlEntry) {
            Fail "assertion 5: '$($package.File.Name)' has no $xmlEntry entry (GenerateDocumentationFile/DocumentationFile wiring)"
        }
    }
}

# --- Assertion 6: sibling .snupkg for each nupkg ----------------------------
$snupkgs = @(Get-ChildItem -LiteralPath $ArtifactsPath -Filter '*.snupkg' -File)
if ($snupkgs.Count -ne 2) {
    $found = if ($snupkgs.Count -eq 0) { '<none>' } else { ($snupkgs | ForEach-Object { $_.Name }) -join ', ' }
    Fail "assertion 6: expected exactly 2 .snupkg files, found $($snupkgs.Count): $found"
}
foreach ($package in @($sdkPackages[0], $testingPackages[0])) {
    $expectedName = [System.IO.Path]::GetFileNameWithoutExtension($package.File.Name) + '.snupkg'
    if (-not ($snupkgs | Where-Object { $_.Name -eq $expectedName })) {
        Fail "assertion 6: '$($package.File.Name)' has no sibling $expectedName (IncludeSymbols/SymbolPackageFormat are not packing symbols)"
    }
}

Write-Output "verify-pack: OK - $version; 2 packages + 2 symbol packages verified ($($sdkPackages[0].File.Name), $($testingPackages[0].File.Name))"
