# Builds a CUDA runtime bundle (.tar.zst) for upload to R2 / external CDN,
# consumed by the in-app GPU support installer at first launch.
#
# Usage: powershell -File build-cuda-bundle.ps1 [-Version 1.0.0]
#
# Output: ..\build\cuda-bundle\cuda-runtime-win-x64-v<version>.tar.zst
#         (prints sha256 + size for the manifest entry)
param(
    [string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$Reqs = Join-Path $ScriptDir 'cuda-bundle\requirements.txt'
$OutDir = Resolve-Path (Join-Path $ScriptDir '..\build') | ForEach-Object { Join-Path $_ 'cuda-bundle' }
$Bundle = Join-Path $OutDir "cuda-runtime-win-x64-v$Version.tar.zst"

# tar + zstd: Windows 10+ ships bsdtar; zstd is not bundled.
# Install via `winget install Facebook.Zstandard` if missing.
foreach ($tool in @('py', 'tar', 'zstd')) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        Write-Host "[bundle] ERROR: '$tool' not on PATH." -ForegroundColor Red
        if ($tool -eq 'zstd') {
            Write-Host "[bundle] Install via: winget install Meta.Zstandard" -ForegroundColor Yellow
        }
        exit 1
    }
}

$Stage = New-Item -ItemType Directory -Path (Join-Path $env:TEMP "cuda-bundle-$([guid]::NewGuid())")
try {
    Write-Host "[bundle] pip install --target=$Stage -r $Reqs"
    py -m pip install --quiet --target="$Stage" -r "$Reqs"

    $LibDir = New-Item -ItemType Directory -Path (Join-Path $Stage 'lib')
    Write-Host "[bundle] flattening .dll files into staging dir"
    Get-ChildItem -Path (Join-Path $Stage 'nvidia') -Recurse -Filter '*.dll' |
        Where-Object { $_.FullName -match '\\bin\\' } |
        ForEach-Object { Copy-Item $_.FullName -Destination $LibDir -Force }

    Write-Host "[bundle] trimming unused libraries"
    foreach ($pat in @('cusolverMg64_*.dll', 'nvrtc-builtins.alt.*.dll')) {
        Get-ChildItem -Path $LibDir -Filter $pat -ErrorAction SilentlyContinue | ForEach-Object {
            Write-Host "  - $($_.Name)"
            Remove-Item $_.FullName -Force
        }
    }

    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    # PowerShell's native-to-native pipe is an *object* stream — it tries to
    # interpret tar's binary stdout as text and either corrupts it or OOMs
    # ("Insufficient memory"). Stage tar to a real file, then zstd that file.
    # Costs ~5 GB of scratch disk for the intermediate, freed immediately.
    $TarPath = Join-Path $env:TEMP "cuda-stage-$([guid]::NewGuid()).tar"
    try {
        Write-Host "[bundle] tar -> $TarPath"
        & tar -C $LibDir -cf $TarPath .
        if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE" }
        Write-Host "[bundle] zstd -19 (high compression, runs ~1-2 min)"
        & zstd -T0 -19 -f -o $Bundle $TarPath
        if ($LASTEXITCODE -ne 0) { throw "zstd failed with exit code $LASTEXITCODE" }
    }
    finally {
        if (Test-Path $TarPath) { Remove-Item $TarPath -Force }
    }

    $Sha = (Get-FileHash $Bundle -Algorithm SHA256).Hash.ToLower()
    $Size = (Get-Item $Bundle).Length
    $Extracted = (Get-ChildItem $LibDir -File | Measure-Object Length -Sum).Sum

    Write-Host ""
    Write-Host "=== bundle ready ==="
    Write-Host "  path:      $Bundle"
    Write-Host "  size:      $([math]::Round($Size / 1GB, 2)) GB  ($Size bytes)"
    Write-Host "  extracted: $([math]::Round($Extracted / 1GB, 2)) GB"
    Write-Host "  sha256:    $Sha"
    Write-Host ""
    Write-Host "=== manifest entry ==="
    Write-Host @"
"win-x64": {
  "url": "https://<your-r2-domain>/cuda-runtime-win-x64-v$Version.tar.zst",
  "sha256": "$Sha",
  "size_bytes": $Size,
  "extracted_size_bytes": $Extracted
}
"@
    Write-Host ""
    Write-Host "=== upload to R2 ==="
    Write-Host "  wrangler r2 object put <bucket>/cuda-runtime-win-x64-v$Version.tar.zst --file=`"$Bundle`""
}
finally {
    Remove-Item -Recurse -Force $Stage
}
