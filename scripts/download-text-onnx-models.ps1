# Downloads pre-converted ONNX checkpoints for sentence-level NLP tasks
# (embeddings, classification, NLI, NER, QA, reranking) directly from
# HuggingFace via `huggingface-cli download`.
#
# These models all have community ONNX exports already published, so we
# pull the .onnx + tokenizer + config files instead of running
# optimum-cli export. That avoids the 1.5h batch export + the
# optimum/transformers/torch version pinning headache from
# export-batch-onnx.ps1.
#
# Source-repo selection per model:
#   - Use the original repo when it publishes ONNX itself (BAAI/bge-*,
#     jinaai/jina-embeddings-v2-base-en, optimum/all-MiniLM-L6-v2,
#     martin-ha/toxic-comment-model).
#   - Fall back to Xenova/* or onnx-community/* (HF's two main "we
#     pre-converted everything" orgs) when the original is PyTorch-only.
#     Xenova exports are produced by transformers.js maintainers and are
#     reliably structured (model.onnx + model_quantized.onnx + tokenizer.json).
#
# Idempotent: skips models whose output folder already exists. Pass -Force
# to redownload. Pass -Models <name1,name2> to limit the set.
#
# Usage:
#   ./scripts/download-text-onnx-models.ps1
#       -- download everything missing
#   ./scripts/download-text-onnx-models.ps1 -Models bge-small,bge-reranker-base
#       -- download only the named subset
#   ./scripts/download-text-onnx-models.ps1 -Force
#       -- redownload everything
#   ./scripts/download-text-onnx-models.ps1 -List
#       -- print the table and exit

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputRoot = $(
        if ($env:DATUMV_MODELS) {
            $env:DATUMV_MODELS
        } else {
            throw 'Set $env:DATUMV_MODELS or pass -OutputRoot <path>.'
        }
    ),

    [Parameter()]
    [string[]]$Models = @(),

    [Parameter()]
    [switch]$Force,

    [Parameter()]
    [switch]$List
)

$ErrorActionPreference = 'Stop'

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
# Each entry:
#   Name        - short alias for -Models filter
#   HF          - HF repo ID to download from (may differ from the
#                 "canonical" PyTorch repo when ONNX lives elsewhere)
#   Origin      - original PyTorch repo, for traceability when HF != origin
#   OutputFolder- subdir under $OutputRoot
#   Include     - glob patterns for huggingface-cli --include. Always
#                 includes tokenizer/config; .onnx pattern varies by repo
#                 layout (some use onnx/ subfolder, some put model.onnx at
#                 root).
#   ExpectedSize- rough disk budget
#   Task        - one-line capability tag
#   Description - human-readable purpose

$KnownModels = @(
    # ─────────────────────────── Embeddings ───────────────────────────
    @{
        Name         = 'all-minilm-l6-v2'
        HF           = 'optimum/all-MiniLM-L6-v2'
        Origin       = 'sentence-transformers/all-MiniLM-L6-v2'
        OutputFolder = 'all-MiniLM-L6-v2-onnx'
        Include      = @('*.onnx', '*.json', '*.txt')
        ExpectedSize = '~90 MB'
        Task         = 'embeddings (384-dim)'
        Description  = 'Sentence embeddings baseline - tiny, fast, "good enough" default.'
    },
    @{
        Name         = 'bge-small-en-v1.5'
        HF           = 'BAAI/bge-small-en-v1.5'
        Origin       = 'BAAI/bge-small-en-v1.5'
        OutputFolder = 'bge-small-en-v1.5-onnx'
        # BAAI publishes ONNX under onnx/ subfolder alongside the .bin
        # weights. Grab the onnx/ folder + the root config/tokenizer.
        Include      = @('onnx/*', 'config.json', 'tokenizer*', 'special_tokens_map.json', 'vocab.txt', '1_Pooling/*', 'sentence_bert_config.json')
        ExpectedSize = '~130 MB'
        Task         = 'embeddings (384-dim)'
        Description  = 'BGE small - MTEB-leaderboard quality at MiniLM size.'
    },
    @{
        Name         = 'jina-embeddings-v2-base-en'
        HF           = 'jinaai/jina-embeddings-v2-base-en'
        Origin       = 'jinaai/jina-embeddings-v2-base-en'
        OutputFolder = 'jina-embeddings-v2-base-en-onnx'
        Include      = @('onnx/*', 'config.json', 'tokenizer*', 'special_tokens_map.json', 'vocab.txt')
        ExpectedSize = '~330 MB'
        Task         = 'embeddings (768-dim, 8K context)'
        Description  = 'Jina v2 base - long-context (8K) embedder for whole-document retrieval.'
    },
    @{
        Name         = 'paraphrase-multilingual-minilm-l12-v2'
        HF           = 'Xenova/paraphrase-multilingual-MiniLM-L12-v2'
        Origin       = 'sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2'
        OutputFolder = 'paraphrase-multilingual-MiniLM-L12-v2-onnx'
        Include      = @('onnx/*', '*.json', '*.txt')
        ExpectedSize = '~470 MB'
        Task         = 'embeddings (multilingual)'
        Description  = 'Multilingual paraphrase MiniLM - 50+ languages, same shape as all-MiniLM.'
    },

    # ─────────────────────────── Rerankers ───────────────────────────
    @{
        Name         = 'ms-marco-minilm-l6-v2'
        HF           = 'Xenova/ms-marco-MiniLM-L-6-v2'
        Origin       = 'cross-encoder/ms-marco-MiniLM-L-6-v2'
        OutputFolder = 'ms-marco-MiniLM-L-6-v2-onnx'
        Include      = @('onnx/*', '*.json', '*.txt')
        ExpectedSize = '~90 MB'
        Task         = 'reranker (cross-encoder)'
        Description  = 'Classic MS-MARCO cross-encoder reranker.'
    },
    @{
        Name         = 'bge-reranker-base'
        HF           = 'Xenova/bge-reranker-base'
        Origin       = 'BAAI/bge-reranker-base'
        OutputFolder = 'bge-reranker-base-onnx'
        Include      = @('onnx/*', '*.json', '*.txt')
        ExpectedSize = '~280 MB'
        Task         = 'reranker (cross-encoder)'
        Description  = 'BGE reranker base - current SOTA-class for small reranker tier.'
    },
    @{
        Name         = 'bge-reranker-large'
        HF           = 'Xenova/bge-reranker-large'
        Origin       = 'BAAI/bge-reranker-large'
        OutputFolder = 'bge-reranker-large-onnx'
        Include      = @('onnx/*', '*.json', '*.txt')
        ExpectedSize = '~1.1 GB'
        Task         = 'reranker (cross-encoder)'
        Description  = 'BGE reranker large - higher accuracy, 4x slower than base.'
    },

    # ───────────────────── Zero-shot classification / NLI ─────────────────────
    @{
        Name         = 'deberta-v3-base-zeroshot-v2'
        HF           = 'onnx-community/deberta-v3-base-zeroshot-v2.0-ONNX'
        Origin       = 'MoritzLaurer/deberta-v3-base-zeroshot-v2.0'
        OutputFolder = 'deberta-v3-base-zeroshot-v2.0-onnx'
        Include      = @('onnx/*', '*.json', '*.txt', '*.model', 'spm.model')
        ExpectedSize = '~370 MB'
        Task         = 'zero-shot / NLI'
        Description  = 'DeBERTa v3 base - arbitrary-label classifier via NLI entailment.'
    },
    @{
        Name         = 'deberta-v3-large-zeroshot-v2'
        HF           = 'onnx-community/deberta-v3-large-zeroshot-v2.0-ONNX'
        Origin       = 'MoritzLaurer/deberta-v3-large-zeroshot-v2.0'
        OutputFolder = 'deberta-v3-large-zeroshot-v2.0-onnx'
        Include      = @('onnx/*', '*.json', '*.txt', '*.model', 'spm.model')
        ExpectedSize = '~1.4 GB'
        Task         = 'zero-shot / NLI'
        Description  = 'DeBERTa v3 large - stronger zero-shot at 4x the cost of base.'
    },
    @{
        Name         = 'roberta-large-mnli'
        HF           = 'Xenova/roberta-large-mnli'
        Origin       = 'FacebookAI/roberta-large-mnli'
        OutputFolder = 'roberta-large-mnli-onnx'
        Include      = @('onnx/*', '*.json', '*.txt')
        ExpectedSize = '~1.4 GB'
        Task         = 'NLI (entailment/contradiction/neutral)'
        Description  = 'Classic RoBERTa-large MNLI - 3-class NLI; also usable for zero-shot.'
    },

    # ─────────────────────── Sentiment / emotion ───────────────────────
    @{
        Name         = 'twitter-roberta-sentiment'
        HF           = 'Xenova/twitter-roberta-base-sentiment-latest'
        Origin       = 'cardiffnlp/twitter-roberta-base-sentiment-latest'
        OutputFolder = 'twitter-roberta-base-sentiment-latest-onnx'
        Include      = @('onnx/*', '*.json', '*.txt')
        ExpectedSize = '~500 MB'
        Task         = 'sentiment (3-class: pos/neg/neutral)'
        Description  = 'CardiffNLP Twitter sentiment - generalizes well beyond tweets.'
    },
    @{
        Name         = 'emotion-distilroberta'
        HF           = 'Xenova/emotion-english-distilroberta-base'
        Origin       = 'j-hartmann/emotion-english-distilroberta-base'
        OutputFolder = 'emotion-english-distilroberta-base-onnx'
        Include      = @('onnx/*', '*.json', '*.txt')
        ExpectedSize = '~330 MB'
        Task         = 'emotion (7-class)'
        Description  = 'Ekman-style emotions: anger, disgust, fear, joy, neutral, sadness, surprise.'
    },

    # ─────────────────────────── NER ───────────────────────────
    @{
        Name         = 'bert-base-ner'
        HF           = 'Xenova/bert-base-NER'
        Origin       = 'dslim/bert-base-NER'
        OutputFolder = 'bert-base-NER-onnx'
        Include      = @('onnx/*', '*.json', '*.txt')
        ExpectedSize = '~440 MB'
        Task         = 'NER (CoNLL: PER/ORG/LOC/MISC)'
        Description  = 'English NER baseline - standard CoNLL-2003 tags.'
    },
    @{
        Name         = 'bert-multilingual-ner'
        HF           = 'Xenova/bert-base-multilingual-cased-ner-hrl'
        Origin       = 'Davlan/bert-base-multilingual-cased-ner-hrl'
        OutputFolder = 'bert-base-multilingual-cased-ner-hrl-onnx'
        Include      = @('onnx/*', '*.json', '*.txt')
        ExpectedSize = '~720 MB'
        Task         = 'NER (multilingual)'
        Description  = 'Multilingual NER over 10 high-resource languages.'
    },

    # ─────────────────────── Toxicity / moderation ───────────────────────
    @{
        Name         = 'toxic-bert'
        HF           = 'Xenova/toxic-bert'
        Origin       = 'unitary/toxic-bert'
        OutputFolder = 'toxic-bert-onnx'
        Include      = @('onnx/*', '*.json', '*.txt')
        ExpectedSize = '~440 MB'
        Task         = 'toxicity (6 multi-label)'
        Description  = 'Unitary toxic-bert - toxic/severe/obscene/threat/insult/identity-hate.'
    },
    @{
        Name         = 'toxic-comment-distilbert'
        HF           = 'Xenova/toxic-comment-model'
        Origin       = 'martin-ha/toxic-comment-model'
        OutputFolder = 'toxic-comment-model-onnx'
        Include      = @('onnx/*', '*.json', '*.txt')
        ExpectedSize = '~270 MB'
        # Source repo is DistilBERT; smaller alternative to toxic-bert.
        # If Xenova export is missing, falling back to the original repo
        # works only if it ships ONNX -- martin-ha's repo does not, so the
        # Xenova mirror is the canonical source.
        Description  = 'DistilBERT toxicity - smaller, faster alternative to toxic-bert.'
    },

    # ───────────────────────── Extractive QA ─────────────────────────
    @{
        Name         = 'distilbert-squad'
        HF           = 'Xenova/distilbert-base-cased-distilled-squad'
        Origin       = 'distilbert-base-cased-distilled-squad'
        OutputFolder = 'distilbert-base-cased-distilled-squad-onnx'
        Include      = @('onnx/*', '*.json', '*.txt')
        ExpectedSize = '~260 MB'
        Task         = 'extractive QA (SQuAD v1)'
        Description  = 'DistilBERT SQuAD - fast extractive QA; no unanswerable handling.'
    },
    @{
        Name         = 'roberta-squad2'
        HF           = 'Xenova/roberta-base-squad2'
        Origin       = 'deepset/roberta-base-squad2'
        OutputFolder = 'roberta-base-squad2-onnx'
        Include      = @('onnx/*', '*.json', '*.txt')
        ExpectedSize = '~500 MB'
        Task         = 'extractive QA (SQuAD v2)'
        Description  = 'RoBERTa SQuAD2 - handles unanswerable questions ("no answer found").'
    }
)

# ---- -List: print the table and exit -----------------------------------

if ($List) {
    Write-Host ''
    Write-Host 'Sentence/text ONNX models available for download:' -ForegroundColor Cyan
    Write-Host ''
    foreach ($entry in $KnownModels) {
        $line = '  {0,-40} {1,-12} [{2}]' -f $entry.Name, $entry.ExpectedSize, $entry.Task
        Write-Host $line
        Write-Host ('      {0}' -f $entry.Description) -ForegroundColor DarkGray
        if ($entry.HF -ne $entry.Origin) {
            Write-Host ('      source: {0} (origin: {1})' -f $entry.HF, $entry.Origin) -ForegroundColor DarkGray
        } else {
            Write-Host ('      source: {0}' -f $entry.HF) -ForegroundColor DarkGray
        }
    }
    Write-Host ''
    Write-Host 'Run with -Models <name1,name2> to download a subset, or no args for all.'
    return
}

# ---- Resolve which models to fetch -------------------------------------

$ToFetch = if ($Models.Count -gt 0) {
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
    $KnownModels
}

# ---- Set up Python venv (reuses .venv\ from sibling export scripts) ----

if (-not (Test-Path '.venv\Scripts\python.exe')) {
    Write-Host 'Creating Python 3.10 virtual environment at .venv\ ...' -ForegroundColor Cyan
    py -3.10 -m venv .venv
} else {
    Write-Host '.venv exists, reusing.' -ForegroundColor DarkGray
}

& .\.venv\Scripts\Activate.ps1

# huggingface_hub provides the `huggingface-cli download` subcommand we use
# below. Pin to a recent version with --include support (added 0.20).
# hf_transfer is the rust-accelerated downloader -- 3-5x throughput on
# large repos, no-op if it can't be imported.
Write-Host 'Installing huggingface_hub + hf_transfer ...' -ForegroundColor Cyan
pip install --quiet --upgrade 'huggingface_hub>=0.24' 'hf_transfer>=0.1.6'

# Opt into the Rust downloader. The cli respects this env var.
$env:HF_HUB_ENABLE_HF_TRANSFER = '1'

# ---- Loop over models ---------------------------------------------------

$results = New-Object System.Collections.Generic.List[object]

foreach ($model in $ToFetch) {
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
            Note   = 'already present (use -Force to redownload)'
        })
        continue
    }

    Write-Host ''
    Write-Host ('[fetch] {0} ({1}) -> {2}' -f $model.Name, $model.HF, $outputPath) -ForegroundColor Cyan
    Write-Host ('        {0}' -f $model.Description) -ForegroundColor DarkGray
    Write-Host ('        Expected size: {0}' -f $model.ExpectedSize) -ForegroundColor DarkGray

    # Build --include args: one per pattern.
    $includeArgs = @()
    foreach ($pattern in $model.Include) {
        $includeArgs += @('--include', $pattern)
    }

    $startedAt = Get-Date
    $errorMsg = $null
    try {
        huggingface-cli download $model.HF `
            @includeArgs `
            --local-dir $outputPath `
            --local-dir-use-symlinks False
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
        $note = if ($errorMsg) { $errorMsg } else { "huggingface-cli exit code $exitCode" }
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
Write-Host "Downloaded: $ok   Skipped: $skipped   Failed: $failed" -ForegroundColor $summaryColor

if ($failed -gt 0) {
    Write-Host ''
    Write-Host 'Failed downloads usually mean the listed HF repo path is wrong (Xenova/onnx-community'
    Write-Host 'naming varies). Search hf.co for an alternate ONNX mirror, or fall back to'
    Write-Host 'optimum-cli export against the original PyTorch repo (Origin field in -List).'
    exit 1
}
