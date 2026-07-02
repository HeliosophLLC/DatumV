#requires -Version 7
<#
.SYNOPSIS
    Builds the sampled Heliosoph/DocBank mirror: image zips + reshaped JSONL.

.DESCRIPTION
    Upstream DocBank (liminghao1630/DocBank on HF) is 54 GB — page images
    split across a ten-part zip, annotations as a 3 GB zip of one-.txt-per-page
    files. The catalog wants a small, per-page-joinable cut instead. This
    script samples N train + M test pages and emits, per split:

        docbank-<split>-images.zip   flat zip of the sampled page images
        docbank-<split>.jsonl.gz     one JSON object per page:
                                       { page_id, image_file, width, height,
                                         text, tokens:[{text,x0,y0,x1,y1,
                                         label,font}] }

    matching datasets/docbank/split.sql (open_jsonl JOIN open_archive on
    image_file). Token text + boxes are copied byte-faithfully from DocBank's
    .txt ground truth; bboxes stay in DocBank's 0-1000 normalized space.

.PARAMETER SourceDir
    A DocBank checkout already extracted to disk, containing the token files
    and original images. If omitted the script fetches + extracts from HF
    (needs huggingface-cli + 7-Zip and ~60 GB free — see notes at the bottom).

.PARAMETER TxtSubdir / ImgSubdir
    Folder names under SourceDir holding the .txt annotations and _ori images.
    Defaults match the upstream zip layout; override if your extraction differs.

.PARAMETER TrainCount / TestCount
    How many pages to sample per split. Defaults: 5000 / 1000 (the catalog cut).

.PARAMETER StagingDir
    Where the four output files are written.

.NOTES
    Requires the .NET image + zip assemblies (bundled with PowerShell 7 on
    Windows). Image dimensions are read from each sampled file; DocBank's .txt
    carries only normalized boxes, so the actual pixel size comes from the image.
#>
[CmdletBinding()]
param(
    [string]$SourceDir,
    [switch]$Download,
    [string]$TxtSubdir = 'DocBank_500K_txt',
    [string]$ImgSubdir = 'DocBank_500K_ori_img',
    [int]$TrainCount = 5000,
    [int]$TestCount = 1000,
    [string]$StagingDir = (Join-Path ([System.IO.Path]::GetTempPath()) 'docbank-mirror')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not $SourceDir) {
    throw @"
No -SourceDir given. Either pass a DocBank checkout you've already extracted,
or let the script fetch + extract it for you (heavy, one-time — ~54 GB down,
needs ~110 GB free during extraction):

  ./export-docbank.ps1 -SourceDir E:\DocBank -Download

Manual equivalent:
  huggingface-cli download liminghao1630/DocBank --repo-type dataset --local-dir E:\DocBank
  7z x E:\DocBank\DocBank_500K_ori_img.zip.001   # auto-joins .002..010
  7z x E:\DocBank\DocBank_500K_txt.zip
  ./export-docbank.ps1 -SourceDir E:\DocBank
"@
}

# Locate 7-Zip: PATH first, then the default install location.
function Get-SevenZip {
    $cmd = Get-Command '7z' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $default = 'C:\Program Files\7-Zip\7z.exe'
    if (Test-Path $default) { return $default }
    throw "7-Zip not found on PATH or at '$default'. Install it or extract the archives manually."
}

if ($Download) {
    New-Item -ItemType Directory -Force -Path $SourceDir | Out-Null
    Write-Host "Downloading DocBank into $SourceDir (~54 GB) ..." -ForegroundColor Cyan
    & huggingface-cli download liminghao1630/DocBank --repo-type dataset --local-dir $SourceDir
    if ($LASTEXITCODE -ne 0) { throw "huggingface-cli download failed ($LASTEXITCODE)." }

    $sevenZip = Get-SevenZip
    $imgPart = Join-Path $SourceDir 'DocBank_500K_ori_img.zip.001'
    $txtZip  = Join-Path $SourceDir 'DocBank_500K_txt.zip'
    Write-Host "Extracting image volumes (this reassembles .001-.010) ..." -ForegroundColor Cyan
    & $sevenZip x $imgPart "-o$SourceDir" -y
    if ($LASTEXITCODE -ne 0) { throw "7-Zip failed extracting images ($LASTEXITCODE)." }
    Write-Host "Extracting annotation files ..." -ForegroundColor Cyan
    & $sevenZip x $txtZip "-o$SourceDir" -y
    if ($LASTEXITCODE -ne 0) { throw "7-Zip failed extracting annotations ($LASTEXITCODE)." }
    Write-Host "Download + extract complete." -ForegroundColor Green
}

if (-not (Test-Path $SourceDir)) {
    throw "SourceDir '$SourceDir' does not exist. Point it at an extracted DocBank checkout (the folder holding the .txt annotations and _ori images)."
}

# Resolve the annotation + image roots. Prefer the explicit subdir names, but
# fall back to auto-discovery so it doesn't matter exactly how the upstream
# zips were extracted (flat under SourceDir, or nested a level or two down).
function Find-Root {
    param([string]$Base, [string]$Preferred, [string]$Filter, [string]$Kind)

    $pref = Join-Path $Base $Preferred
    if (Test-Path $pref) { return (Resolve-Path $pref).Path }

    # SourceDir itself holds the files?
    if (Get-ChildItem -LiteralPath $Base -Filter $Filter -File -ErrorAction SilentlyContinue | Select-Object -First 1) {
        return (Resolve-Path $Base).Path
    }

    # Otherwise pick the subdirectory (to depth 3) holding the most matches.
    $best = Get-ChildItem -LiteralPath $Base -Filter $Filter -File -Recurse -Depth 3 -ErrorAction SilentlyContinue |
        Group-Object DirectoryName | Sort-Object Count -Descending | Select-Object -First 1
    if ($best) {
        Write-Host "  auto-detected $Kind root: $($best.Name) ($($best.Count)+ files)" -ForegroundColor DarkGray
        return $best.Name
    }
    throw "Could not find any '$Filter' files for the $Kind root under '$Base'. Is DocBank fully extracted there?"
}

Write-Host "Resolving DocBank roots under $SourceDir ..." -ForegroundColor Cyan
$txtRoot = Find-Root -Base $SourceDir -Preferred $TxtSubdir -Filter '*.txt' -Kind 'annotation'
$imgRoot = Find-Root -Base $SourceDir -Preferred $ImgSubdir -Filter '*.jpg' -Kind 'image'
Write-Host "  annotations: $txtRoot"
Write-Host "  images:      $imgRoot"

New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null

# DocBank token line: text \t x0 \t y0 \t x1 \t y1 \t R \t G \t B \t font \t label
function Read-Page {
    param([string]$TxtPath, [string]$ImgPath)

    $tokens = [System.Collections.Generic.List[object]]::new()
    $words = [System.Collections.Generic.List[string]]::new()
    foreach ($line in [System.IO.File]::ReadLines($TxtPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $f = $line -split "`t"
        if ($f.Count -lt 10) { continue }   # skip malformed rows
        $words.Add($f[0])
        $tokens.Add([ordered]@{
            text  = $f[0]
            x0    = [int]$f[1]; y0 = [int]$f[2]; x1 = [int]$f[3]; y1 = [int]$f[4]
            label = $f[9]
            font  = $f[8]
        })
    }

    $w = 0; $h = 0
    try {
        $img = [System.Drawing.Image]::FromFile($ImgPath)
        try { $w = $img.Width; $h = $img.Height } finally { $img.Dispose() }
    } catch { }   # leave 0×0 if unreadable; column stays nullable downstream

    [ordered]@{
        page_id    = [System.IO.Path]::GetFileNameWithoutExtension($ImgPath)
        image_file = [System.IO.Path]::GetFileName($ImgPath)
        width      = $w
        height     = $h
        text       = ($words -join ' ')
        tokens     = $tokens
    }
}

# Pair each .txt with its image. DocBank images are named "<stem>_ori.jpg"
# alongside "<stem>.txt"; fall back to "<stem>.jpg" if the _ori suffix is absent.
function Resolve-Image {
    param([string]$Stem)
    foreach ($cand in @("$Stem`_ori.jpg", "$Stem.jpg", "$Stem`_ori.png", "$Stem.png")) {
        $p = Join-Path $imgRoot $cand
        if (Test-Path $p) { return $p }
    }
    return $null
}

function Build-Split {
    param([string]$Name, $Files)

    $txtFiles = @($Files)
    $Count = $txtFiles.Count
    Write-Host "Building '$Name' split ($Count pages) ..." -ForegroundColor Cyan

    $imagesZip = Join-Path $StagingDir "docbank-$Name-images.zip"
    $jsonlGz   = Join-Path $StagingDir "docbank-$Name.jsonl.gz"
    if (Test-Path $imagesZip) { Remove-Item $imagesZip -Force }
    if (Test-Path $jsonlGz)   { Remove-Item $jsonlGz -Force }

    $zip = [System.IO.Compression.ZipFile]::Open($imagesZip, 'Create')
    $gz  = [System.IO.Compression.GZipStream]::new(
        [System.IO.File]::Create($jsonlGz), [System.IO.Compression.CompressionLevel]::Optimal)
    $writer = [System.IO.StreamWriter]::new($gz, [System.Text.UTF8Encoding]::new($false))

    $done = 0; $skipped = 0
    try {
        foreach ($txt in $txtFiles) {
            $stem = [System.IO.Path]::GetFileNameWithoutExtension($txt.Name)
            $imgPath = Resolve-Image $stem
            if (-not $imgPath) { $skipped++; continue }

            $page = Read-Page -TxtPath $txt.FullName -ImgPath $imgPath
            # image into the zip under its exact join-key name
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $imgPath, $page.image_file) | Out-Null
            # one compact JSON object per line
            $writer.WriteLine(($page | ConvertTo-Json -Depth 6 -Compress))
            $done++
            if ($done % 500 -eq 0) { Write-Host "  $done / $Count" }
        }
    }
    finally {
        $writer.Dispose(); $zip.Dispose()
    }

    $imgMb = [math]::Round((Get-Item $imagesZip).Length / 1MB, 1)
    $annMb = [math]::Round((Get-Item $jsonlGz).Length / 1MB, 1)
    Write-Host ("  {0}: {1} pages ({2} skipped, no image) — images {3} MB, annotations {4} MB" -f `
        $Name, $done, $skipped, $imgMb, $annMb) -ForegroundColor Green
    if ($done -ne $Count) {
        Write-Warning "Sampled $done pages but the catalog expects $Count — reconcile expectedRowCounts or the sample."
    }
}

Write-Host "Enumerating + sorting annotation files (a few seconds over 500K files) ..." -ForegroundColor Cyan
$allTxt = Get-ChildItem -LiteralPath $txtRoot -Filter '*.txt' -File | Sort-Object Name
Write-Host "  $($allTxt.Count) annotation files found."
# Disjoint splits: train from the front of the sorted list, test from the back,
# so the two sets come from different papers (no page-level train/test leak).
Build-Split -Name 'train' -Files ($allTxt | Select-Object -First $TrainCount)
Build-Split -Name 'test'  -Files ($allTxt | Select-Object -Last  $TestCount)

Write-Host ''
Write-Host "Staged in: $StagingDir" -ForegroundColor Green
Write-Host ''
Write-Host 'Next steps:' -ForegroundColor Yellow
Write-Host '  1. Create the dataset repo (once):'
Write-Host '       huggingface-cli repo create DocBank --type dataset --organization Heliosoph'
Write-Host '  2. Upload the four staged files:'
Write-Host "       huggingface-cli upload Heliosoph/DocBank `"$StagingDir`" . --repo-type dataset --include `"docbank-*`""
Write-Host '  3. Replace "revision": "main" in the two DocBank variants in'
Write-Host '     datasets/catalog.json with the commit hash HF returns.'
