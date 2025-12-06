# Batch-converts a curated list of HuggingFace models to ONNX via
# `optimum-cli export onnx`. Each conversion that succeeds produces a
# folder of ONNX files (plus tokenizer/config/preprocessor JSON) ready to
# register against the engine's `onnx` backend, retiring the corresponding
# Python-bridge entry.
#
# The list is restricted to models with established `optimum-cli` support.
# Models not on the list (XTTS-v2, StyleTTS2, F5-TTS, GPT-SoVITS, Tortoise)
# either lack export tooling or require per-architecture hand-export work
# beyond what `optimum-cli` does -- those stay on the Python bridge.
#
# Idempotent: skips models whose output folder already exists. Pass -Force
# to re-export. Pass -Models <name1,name2> to limit the set; otherwise all
# known models attempt to convert.
#
# Requirements:
#   - Python 3.10 venv at .venv\ (created automatically if missing)
#   - Disk: budget ~30-50 GB if running the full list
#   - Internet for first-time HuggingFace downloads
#
# Usage:
#   ./scripts/export-batch-onnx.ps1
#       -- converts every known model whose output folder is missing
#   ./scripts/export-batch-onnx.ps1 -Models bark-small,musicgen-small
#       -- converts only the named subset
#   ./scripts/export-batch-onnx.ps1 -Force
#       -- re-export everything, overwriting existing output
#   ./scripts/export-batch-onnx.ps1 -List
#       -- print the known-models table and exit (no conversions)

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputRoot = $(
        if ($env:DATUM_MODELS) {
            $env:DATUM_MODELS
        } else {
            throw 'Set $env:DATUM_MODELS or pass -OutputRoot <path>.'
        }
    ),

    # Subset of model names from the known list. Empty = all known models.
    [Parameter()]
    [string[]]$Models = @(),

    # Re-export models whose output folder already exists.
    [Parameter()]
    [switch]$Force,

    # Print the known-models table and exit.
    [Parameter()]
    [switch]$List
)

$ErrorActionPreference = 'Stop'

# Helper: best-effort folder size summary (e.g. "1.4 GB").
function Get-FolderSize {
    param([string]$Path)
    if (-not (Test-Path $Path)) { return '' }
    $bytes = (Get-ChildItem -Path $Path -Recurse -File -ErrorAction SilentlyContinue |
              Measure-Object -Property Length -Sum).Sum
    if ($null -eq $bytes -or $bytes -eq 0) { return '0 B' }
    $units = @('B', 'KB', 'MB', 'GB', 'TB')
    $i = 0
    while ($bytes -ge 1024 -and $i -lt $units.Length - 1) {
        $bytes = $bytes / 1024
        $i++
    }
    return ('{0:N1} {1}' -f $bytes, $units[$i])
}

# ---- Curated model list -------------------------------------------------
#
# Each entry: a display name (matches the catalog entry name), HF model ID,
# the output folder name, an expected-disk note, and a one-line description
# of why it's here. Tasks are auto-inferred by optimum-cli where possible.

$KnownModels = @(
    @{
        Name        = 'bark-small'
        HF          = 'suno/bark-small'
        OutputFolder = 'bark-small-onnx'
        ExpectedSize = '~1.0 GB'
        # optimum-cli rejects Bark with "custom or unsupported architecture"
        # -- they recommend filing a feature request at github.com/huggingface/optimum.
        # Bark's 4-stage autoregressive pipeline doesn't fit static ONNX
        # cleanly. Stays on the Python bridge.
        Skip         = $true
        SkipReason   = 'optimum does not support bark architecture'
        Description  = 'Suno Bark Small - TTS with embedded sound effects.'
    },
    @{
        Name        = 'bark'
        HF          = 'suno/bark'
        OutputFolder = 'bark-onnx'
        ExpectedSize = '~3.5 GB'
        Skip         = $true
        SkipReason   = 'optimum does not support bark architecture'
        Description  = 'Suno Bark (full) - higher-quality TTS variant.'
    },
    @{
        Name        = 'musicgen-small'
        HF          = 'facebook/musicgen-small'
        OutputFolder = 'musicgen-small-onnx'
        ExpectedSize = '~1.5 GB'
        Description  = 'Meta MusicGen Small - text-to-music, 30s clips.'
    },
    @{
        Name        = 'musicgen-medium'
        HF          = 'facebook/musicgen-medium'
        OutputFolder = 'musicgen-medium-onnx'
        ExpectedSize = '~6.0 GB'
        Description  = 'Meta MusicGen Medium - better quality than small.'
    },
    @{
        Name        = 'audiogen-medium'
        HF          = 'facebook/audiogen-medium'
        OutputFolder = 'audiogen-medium-onnx'
        ExpectedSize = '~6.0 GB'
        # AudioGen is structurally a MusicGen variant. optimum can't infer
        # the source library automatically, so hint --library transformers.
        # Whether the export then succeeds is uncertain (audiocraft-format
        # checkpoints sometimes need extra coaxing).
        Library      = 'transformers'
        Task         = 'text-to-audio'
        Description  = 'Meta AudioGen - text-to-sound-effect (D&D ambience).'
    },
    @{
        Name        = 'whisper-tiny'
        HF          = 'openai/whisper-tiny'
        OutputFolder = 'whisper-tiny-onnx'
        ExpectedSize = '~150 MB'
        Description  = 'OpenAI Whisper Tiny - fastest STT, lowest accuracy.'
    },
    @{
        Name        = 'whisper-base'
        HF          = 'openai/whisper-base'
        OutputFolder = 'whisper-base-onnx'
        ExpectedSize = '~290 MB'
        Description  = 'OpenAI Whisper Base - balanced STT.'
    },
    @{
        Name        = 'whisper-small'
        HF          = 'openai/whisper-small'
        OutputFolder = 'whisper-small-onnx'
        ExpectedSize = '~970 MB'
        Description  = 'OpenAI Whisper Small - better accuracy than base.'
    },
    @{
        Name        = 'whisper-medium'
        HF          = 'openai/whisper-medium'
        OutputFolder = 'whisper-medium-onnx'
        ExpectedSize = '~3.0 GB'
        Description  = 'OpenAI Whisper Medium - strong STT, slower.'
    },
    @{
        Name        = 'blip2-opt-2.7b'
        HF          = 'Salesforce/blip2-opt-2.7b'
        OutputFolder = 'blip2-opt-2_7b-onnx'
        ExpectedSize = '~6.0 GB'
        # optimum-cli rejects BLIP-2 the same way it rejects Bark: "custom
        # or unsupported architecture". Florence-2 (already wired up
        # natively) covers the same captioning niche with cleaner export
        # support, so this entry stays for documentation only.
        Skip         = $true
        SkipReason   = 'optimum does not support blip-2; Florence-2 covers this niche'
        Description  = 'BLIP-2 - image captioning (Florence-2 is the recommended alternative).'
    },
    @{
        Name        = 'clip-vit-base'
        HF          = 'openai/clip-vit-base-patch32'
        OutputFolder = 'clip-vit-base-patch32-onnx'
        ExpectedSize = '~600 MB'
        Description  = 'CLIP ViT-B/32 - image/text contrastive embeddings.'
    }
)

# ---- -List: print the table and exit -----------------------------------

if ($List) {
    Write-Host ''
    Write-Host 'Known optimum-exportable models:' -ForegroundColor Cyan
    Write-Host ''
    foreach ($entry in $KnownModels) {
        $tags = @()
        if ($entry.ContainsKey('Task') -and $entry.Task) { $tags += "[$($entry.Task)]" }
        if ($entry.ContainsKey('Library') -and $entry.Library) { $tags += "[lib:$($entry.Library)]" }
        if ($entry.ContainsKey('Skip') -and $entry.Skip) { $tags += '[unsupported]' }
        $tagStr = if ($tags.Count -gt 0) { ($tags -join ' ') } else { '' }

        $line = '  {0,-20} {1,-12} {2,-30} {3}' -f $entry.Name, $entry.ExpectedSize, $tagStr, $entry.Description
        if ($entry.ContainsKey('Skip') -and $entry.Skip) {
            Write-Host $line -ForegroundColor DarkGray
        } else {
            Write-Host $line
        }
    }
    Write-Host ''
    Write-Host 'Run with -Models <name1,name2> to convert a subset, or no args for all.'
    Write-Host 'Tags: [task] = explicit task hint, [lib:X] = source library hint,'
    Write-Host '      [unsupported] = optimum-cli does not support this architecture; skipped'
    Write-Host '      by default. Pass explicitly via -Models to retry anyway.'
    return
}

# ---- Resolve which models to attempt ------------------------------------

$ToConvert = if ($Models.Count -gt 0) {
    # Explicit -Models list: include everything the user named, even
    # entries marked Skip (the user is opting in to retry a known-broken
    # conversion).
    $unknown = @()
    foreach ($name in $Models) {
        $match = $KnownModels | Where-Object { $_.Name -eq $name }
        if (-not $match) { $unknown += $name }
    }
    if ($unknown.Count -gt 0) {
        Write-Host "Unknown model name(s): $($unknown -join ', ')" -ForegroundColor Red
        Write-Host 'Run with -List to see known models.'
        exit 1
    }
    $KnownModels | Where-Object { $Models -contains $_.Name }
} else {
    # Default run: filter out Skip=$true entries so we don't waste cycles
    # on conversions that are known to fail. They still appear in -List.
    $KnownModels | Where-Object { -not ($_.ContainsKey('Skip') -and $_.Skip) }
}

# ---- Set up Python venv (reuses .venv\ from sibling export scripts) ----

if (-not (Test-Path '.venv\Scripts\python.exe')) {
    Write-Host 'Creating Python 3.10 virtual environment at .venv\ ...' -ForegroundColor Cyan
    py -3.10 -m venv .venv
} else {
    Write-Host '.venv exists, reusing.' -ForegroundColor DarkGray
}

& .\.venv\Scripts\Activate.ps1

Write-Host 'Ensuring optimum + transformers + diffusers + torch installed ...' -ForegroundColor Cyan
# optimum 2.x removed the [diffusers] / [exporters] extras; install
# diffusers separately and use the bare optimum[onnxruntime] extra. The
# stale extras names were producing "does not provide the extra" warnings
# without any other functional impact.
pip install --quiet --upgrade 'optimum[onnxruntime]' transformers diffusers torch

# ---- Loop over models ---------------------------------------------------

$results = New-Object System.Collections.Generic.List[object]

foreach ($model in $ToConvert) {
    $outputPath = Join-Path $OutputRoot $model.OutputFolder

    if ((Test-Path $outputPath) -and -not $Force) {
        $existingSize = Get-FolderSize -Path $outputPath
        Write-Host ''
        Write-Host ('[skip] {0} - output exists at {1}' -f $model.Name, $outputPath) -ForegroundColor DarkYellow
        $results.Add([pscustomobject]@{
            Model  = $model.Name
            Status = 'skipped'
            Path   = $outputPath
            Size   = $existingSize
            Note   = 'already present (use -Force to re-export)'
        })
        continue
    }

    Write-Host ''
    Write-Host ('[convert] {0} ({1}) -> {2}' -f $model.Name, $model.HF, $outputPath) -ForegroundColor Cyan
    Write-Host ('          {0}' -f $model.Description) -ForegroundColor DarkGray
    Write-Host ('          Expected size: {0}' -f $model.ExpectedSize) -ForegroundColor DarkGray
    if ($model.ContainsKey('Task') -and $model.Task) {
        Write-Host ('          Task hint: {0}' -f $model.Task) -ForegroundColor DarkGray
    }
    if ($model.ContainsKey('Library') -and $model.Library) {
        Write-Host ('          Library hint: {0}' -f $model.Library) -ForegroundColor DarkGray
    }
    if ($model.ContainsKey('Skip') -and $model.Skip) {
        Write-Host ('          [retry of known-unsupported model: {0}]' -f $model.SkipReason) -ForegroundColor Yellow
    }

    $startedAt = Get-Date
    $errorMsg = $null
    try {
        # Build the optimum-cli arg list dynamically: append --task and
        # --library only when the model entry hints them. Auto-inference
        # works for the bulk of cases; hints are for models where
        # inference picks the wrong slot or can't determine the source
        # library at all (AudioGen).
        $extraArgs = @()
        if ($model.ContainsKey('Task') -and $model.Task) {
            $extraArgs += @('--task', $model.Task)
        }
        if ($model.ContainsKey('Library') -and $model.Library) {
            $extraArgs += @('--library', $model.Library)
        }
        optimum-cli export onnx --model $model.HF @extraArgs $outputPath
        $exitCode = $LASTEXITCODE
    } catch {
        $exitCode = 1
        $errorMsg = $_.Exception.Message
    }
    $elapsed = (Get-Date) - $startedAt

    if ($exitCode -eq 0 -and (Test-Path $outputPath)) {
        $finalSize = Get-FolderSize -Path $outputPath
        $elapsedStr = $elapsed.ToString('mm\:ss')
        Write-Host ('[ok]    {0} - {1} in {2}' -f $model.Name, $finalSize, $elapsedStr) -ForegroundColor Green
        $results.Add([pscustomobject]@{
            Model  = $model.Name
            Status = 'ok'
            Path   = $outputPath
            Size   = $finalSize
            Note   = ''
        })
    } else {
        $note = if ($errorMsg) { $errorMsg } else { "optimum-cli exit code $exitCode" }
        Write-Host ('[fail]  {0} - {1}' -f $model.Name, $note) -ForegroundColor Red
        $results.Add([pscustomobject]@{
            Model  = $model.Name
            Status = 'failed'
            Path   = $outputPath
            Size   = ''
            Note   = $note
        })
    }
}

deactivate

# ---- Summary ------------------------------------------------------------

Write-Host ''
Write-Host '------------------------- Summary --------------------------' -ForegroundColor Cyan
$results | Format-Table -AutoSize Model, Status, Size, Note

$ok      = ($results | Where-Object { $_.Status -eq 'ok' }).Count
$skipped = ($results | Where-Object { $_.Status -eq 'skipped' }).Count
$failed  = ($results | Where-Object { $_.Status -eq 'failed' }).Count

$summaryColor = if ($failed -gt 0) { 'Yellow' } else { 'Green' }
Write-Host ''
Write-Host "Converted: $ok   Skipped: $skipped   Failed: $failed" -ForegroundColor $summaryColor

if ($failed -gt 0) {
    Write-Host ''
    Write-Host 'Failed models stay on the Python bridge - register them via PythonBackedModel'
    Write-Host '(see RegisterBarkSmall in BuiltinModels.cs as a template).'
    exit 1
}
