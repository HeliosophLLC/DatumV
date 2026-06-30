#requires -Version 7
<#
.SYNOPSIS
    Stages the EMNIST IDX files for the Heliosoph/EMNIST Hugging Face mirror.

.DESCRIPTION
    The catalog's EMNIST entry ingests per-split IDX `.gz` files individually
    (open_idx_images / open_idx_labels each take a single file path). NIST,
    however, ships every split bundled inside one `gzip.zip`, and a SQL recipe
    can't pull a member out of a zip into a path. This script bridges that gap:
    it downloads NIST's bundle, extracts the per-split members the catalog
    references, and flattens them into a staging directory ready to upload.

    The original IDX bytes are preserved verbatim — orientation (EMNIST's
    transposed glyphs) is corrected at query time by image_transpose() in
    datasets/emnist/split.sql, not here, so the mirror stays a faithful copy
    of upstream.

    After staging, publish with the huggingface-cli command this script prints,
    then pin the resulting commit hash into the "revision" fields of the EMNIST
    variants in datasets/catalog.json (currently "main").

.PARAMETER StagingDir
    Where to write the flattened .gz files. Defaults to a temp subfolder.

.PARAMETER KeepDownload
    Keep the downloaded gzip.zip after extraction (default removes it).
#>
[CmdletBinding()]
param(
    [string]$StagingDir = (Join-Path ([System.IO.Path]::GetTempPath()) 'emnist-mirror'),
    [switch]$KeepDownload
)

$ErrorActionPreference = 'Stop'

$SourceUrl = 'https://biometrics.nist.gov/cs_links/EMNIST/gzip.zip'

# The 12 members the catalog's six variants reference (balanced / letters /
# byclass, each train + test, images + labels). Inside the zip they live under
# a `gzip/` prefix; we flatten that away.
$Members = @(
    'emnist-balanced-train-images-idx3-ubyte.gz',
    'emnist-balanced-train-labels-idx1-ubyte.gz',
    'emnist-balanced-test-images-idx3-ubyte.gz',
    'emnist-balanced-test-labels-idx1-ubyte.gz',
    'emnist-letters-train-images-idx3-ubyte.gz',
    'emnist-letters-train-labels-idx1-ubyte.gz',
    'emnist-letters-test-images-idx3-ubyte.gz',
    'emnist-letters-test-labels-idx1-ubyte.gz',
    'emnist-byclass-train-images-idx3-ubyte.gz',
    'emnist-byclass-train-labels-idx1-ubyte.gz',
    'emnist-byclass-test-images-idx3-ubyte.gz',
    'emnist-byclass-test-labels-idx1-ubyte.gz'
)

New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null
$zipPath = Join-Path $StagingDir 'gzip.zip'

Write-Host "Downloading EMNIST bundle from $SourceUrl ..." -ForegroundColor Cyan
Write-Host '  (~540 MB — this is the full byclass/bymerge/balanced/letters/digits/mnist set)'
Invoke-WebRequest -Uri $SourceUrl -OutFile $zipPath

Write-Host 'Extracting the 12 referenced members ...' -ForegroundColor Cyan
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    foreach ($member in $Members) {
        # Members may be stored as "gzip/<name>" or "<name>" depending on the
        # bundle layout; match on the leaf name.
        $entry = $archive.Entries | Where-Object { [System.IO.Path]::GetFileName($_.FullName) -eq $member } | Select-Object -First 1
        if ($null -eq $entry) {
            throw "Member '$member' not found inside gzip.zip — has the upstream layout changed?"
        }
        $dest = Join-Path $StagingDir $member
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
        $sizeMb = [math]::Round((Get-Item $dest).Length / 1MB, 2)
        Write-Host ("  {0,-48} {1,8} MB" -f $member, $sizeMb)
    }
}
finally {
    $archive.Dispose()
}

if (-not $KeepDownload) {
    Remove-Item $zipPath -Force
}

Write-Host ''
Write-Host "Staged 12 files in: $StagingDir" -ForegroundColor Green
Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Yellow
Write-Host '  1. Create the dataset repo (once):'
Write-Host '       huggingface-cli repo create EMNIST --type dataset --organization Heliosoph'
Write-Host '  2. Upload the staged files:'
Write-Host "       huggingface-cli upload Heliosoph/EMNIST `"$StagingDir`" . --repo-type dataset --include `"*.gz`""
Write-Host '  3. Copy the commit hash HF prints and replace "revision": "main" in'
Write-Host '     the six EMNIST variants in datasets/catalog.json so installs pin it.'
