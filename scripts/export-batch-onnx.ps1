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
    },
    @{
        Name        = 'paligemma2-3b-mix-224'
        HF          = 'google/paligemma2-3b-mix-224'
        OutputFolder = 'paligemma2-3b-mix-224-onnx'
        ExpectedSize = '~6.0 GB'
        # SigLIP vision encoder + Gemma 2B decoder + linear projector.
        # 224x224 input is faster; 448 variant gives better fine-detail.
        # Mix-tuned variants are pre-finetuned on multiple tasks
        # (captioning, VQA, OCR) so they handle generic prompts well.
        Description  = 'Google PaliGemma 2 3B (mix, 224x224) - verbose factual captioner.'
    },
    @{
        Name        = 'paligemma2-3b-mix-448'
        HF          = 'google/paligemma2-3b-mix-448'
        OutputFolder = 'paligemma2-3b-mix-448-onnx'
        ExpectedSize = '~6.0 GB'
        # Higher-resolution sibling. Slower (~4x more vision tokens) but
        # noticeably better at fine details, OCR, small-object recognition.
        # This is the better default for D&D scene art with rich detail.
        Description  = 'Google PaliGemma 2 3B (mix, 448x448) - higher-detail captioner.'
    },
    @{
        Name        = 'phi-3.5-vision-instruct'
        HF          = 'microsoft/Phi-3.5-vision-instruct'
        OutputFolder = 'phi-3.5-vision-instruct-onnx-converted'
        ExpectedSize = '~8.0 GB'
        # Microsoft ships a pre-converted GenAI-format ONNX at
        # microsoft/Phi-3.5-vision-instruct-onnx, but only int4 quantized.
        # Try optimum-cli on the original PyTorch repo to get a clean
        # Florence-2-style export. If optimum rejects it as a "custom
        # architecture" (likely), we fall back to GenAI integration on
        # the int4 ONNX.
        Library      = 'transformers'
        Task         = 'image-to-text'
        # Phi-3.5-Vision ships modeling_phi3_v.py as custom code in the HF
        # repo; optimum refuses to load it without an explicit trust opt-in.
        TrustRemoteCode = $true
        Description  = 'Microsoft Phi-3.5-Vision - VLM with instruction-following captions.'
    },
    # ─────────────────────── Image generation candidates ───────────────────────
    #
    # Realistic outcome estimates for an overnight batch run:
    #   FLUX.1-schnell        60% works (optimum 1.21+ has FLUX support)
    #   SD 3.5 Medium / Large 60% works (Stability published export configs)
    #   PixArt-Sigma          40% works (DiT, partial optimum support)
    #   AuraFlow              40% works (non-standard)
    #   Stable Cascade        50% works (multi-stage; needs both prior + decoder)
    #   Kolors                20% works (Chinese-trained, custom code path)
    #   Sana                  20% works (very new, NVIDIA's own DiT)
    #   HunyuanDiT            20% works (Tencent custom architecture)
    #
    # Failures are expected; that's why the batch script exists. Disk
    # budget if all 8 succeed: ~80 GB on top of the converted output;
    # the PyTorch HF cache holds another ~80 GB transiently.

    @{
        Name        = 'flux-schnell'
        HF          = 'black-forest-labs/FLUX.1-schnell'
        OutputFolder = 'flux-schnell-onnx'
        ExpectedSize = '~32 GB at fp16, ~64 GB at fp32'
        # The Apache-2.0 SOTA. 12B params total (transformer + 2 text
        # encoders + VAE). fp16 export halves the disk + VRAM footprint
        # vs fp32 at no measurable quality cost for 4-step distilled
        # output. Required to fit on a 24 GB card without shared-RAM
        # spillover. Gate: requires HF login + license accept.
        Library      = 'diffusers'
        Task         = 'text-to-image'
        Dtype        = 'fp16'
        Description  = 'Black Forest Labs FLUX.1-schnell - Apache-2.0 SOTA image gen (12B), fp16.'
    },
    @{
        Name        = 'sd-3.5-medium'
        HF          = 'stabilityai/stable-diffusion-3.5-medium'
        OutputFolder = 'sd-3.5-medium-onnx'
        ExpectedSize = '~15 GB at fp16'
        # Stability's mid-size SD3.5. Markedly better text rendering and
        # composition than SDXL. Stability AI Community License (same as
        # SDXL-Turbo). Gated -- requires HF login + license accept.
        Library      = 'diffusers'
        Task         = 'text-to-image'
        Dtype        = 'fp16'
        Description  = 'Stable Diffusion 3.5 Medium - improved successor to SDXL, fp16.'
    },
    @{
        Name        = 'sd-3.5-large-turbo'
        HF          = 'stabilityai/stable-diffusion-3.5-large-turbo'
        OutputFolder = 'sd-3.5-large-turbo-onnx'
        ExpectedSize = '~26 GB at fp16, ~52 GB at fp32'
        # SD3.5 Large + Turbo distillation: same quality as SD3.5 Large
        # at 4 inference steps instead of 30. fp16 keeps the resident
        # weights inside a 24 GB card. Same gated license as SD3.5 Medium.
        Library      = 'diffusers'
        Task         = 'text-to-image'
        Dtype        = 'fp16'
        Description  = 'Stable Diffusion 3.5 Large Turbo - 4-step distillation, fp16.'
    },
    @{
        Name        = 'pixart-sigma-1024'
        HF          = 'PixArt-alpha/PixArt-Sigma-XL-2-1024-MS'
        OutputFolder = 'pixart-sigma-1024-onnx'
        ExpectedSize = '~1.5 GB'
        # DiT-based, surprisingly good for ~600M params. OpenRAIL-M
        # license. Not gated. Worth converting just for the
        # quality/footprint ratio.
        Library      = 'diffusers'
        Task         = 'text-to-image'
        Description  = 'PixArt-Sigma 1024MS - 600M DiT, lightweight quality.'
    },
    @{
        Name        = 'auraflow-v0.3'
        HF          = 'fal/AuraFlow-v0.3'
        OutputFolder = 'auraflow-v0.3-onnx'
        ExpectedSize = '~14 GB'
        # fal.ai's Apache-2.0 alternative to FLUX/SDXL. ~6.8B params,
        # composition often beats SDXL. Lower hype than FLUX but fully
        # license-clean and significantly smaller.
        Library      = 'diffusers'
        Task         = 'text-to-image'
        Description  = 'AuraFlow v0.3 - Apache-2.0 alternative to FLUX (6.8B).'
    },
    @{
        Name        = 'stable-cascade'
        HF          = 'stabilityai/stable-cascade'
        OutputFolder = 'stable-cascade-onnx'
        ExpectedSize = '~10 GB'
        # Wuerstchen-architecture: 3-stage (prior -> decoder -> VAE) for
        # better disk/quality tradeoff than monolithic SD. Gated under
        # Stability terms. Conversion may need separate exports per stage.
        Library      = 'diffusers'
        Task         = 'text-to-image'
        Description  = 'Stable Cascade - 3-stage Wuerstchen-architecture text-to-image.'
    },
    @{
        Name        = 'kolors'
        HF          = 'Kwai-Kolors/Kolors-diffusers'
        OutputFolder = 'kolors-onnx'
        ExpectedSize = '~6 GB'
        # Kuaishou's Apache-2.0 image gen. Roughly SDXL-quality. Trained
        # on Asian-leaning data; English prompts work well. Custom code
        # in the HF repo so trust_remote_code is required.
        Library      = 'diffusers'
        Task         = 'text-to-image'
        TrustRemoteCode = $true
        Description  = 'Kuaishou Kolors - Apache-2.0 SDXL-class image gen.'
    },
    @{
        Name        = 'sana-1.6b-1024'
        HF          = 'Efficient-Large-Model/Sana_1600M_1024px_diffusers'
        OutputFolder = 'sana-1.6b-1024-onnx'
        ExpectedSize = '~3 GB'
        # NVIDIA's Sana - 1.6B DiT, very fast inference (~10x SDXL on
        # equal hardware). Apache-2.0. Custom architecture; optimum
        # support is uncertain. Worth a shot.
        Library      = 'diffusers'
        Task         = 'text-to-image'
        TrustRemoteCode = $true
        Description  = 'NVIDIA Sana 1.6B - small, fast Apache-2.0 image gen.'
    },
    @{
        Name        = 'juggernaut-xl-lightning'
        HF          = 'RunDiffusion/Juggernaut-XL-Lightning'
        OutputFolder = 'juggernaut-xl-lightning-onnx'
        ExpectedSize = '~20 GB at fp32'
        # Community SDXL fine-tune + Lightning distillation. Trained
        # heavily on photorealistic / fantasy-character datasets; widely
        # considered one of the best small "fast-quality" generators for
        # D&D-style portraits and scene art. Architecturally identical
        # to SDXL-Turbo, so once converted it can drop into the existing
        # SdxlTurboModel.cs with minor scheduler config changes (4 steps
        # vs 1, different sigma schedule).
        #
        # Exported at fp32: the optimum 1.24 + torch 2.4 + opset-14 fp16
        # path produces a numerically broken UNet (all-NaN noise_pred from
        # valid conditioning) for SDXL-class models. Revisit fp16 once
        # there's a confirmed working fp16 export toolchain.
        Library      = 'diffusers'
        Task         = 'text-to-image'
        Description  = 'JuggernautXL Lightning - SDXL-class fast-quality fantasy generator.'
    },
    @{
        Name        = 'hunyuan-dit'
        HF          = 'Tencent-Hunyuan/HunyuanDiT-v1.2-Diffusers'
        OutputFolder = 'hunyuan-dit-onnx'
        ExpectedSize = '~12 GB'
        # Tencent's DiT image gen. Strong on Asian aesthetic / styles.
        # Custom Tencent license (commercial use OK with caveats).
        # High failure probability due to custom architecture.
        Library      = 'diffusers'
        Task         = 'text-to-image'
        TrustRemoteCode = $true
        Description  = 'Tencent HunyuanDiT v1.2 - DiT, strong on Asian aesthetic.'
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

# Install pipeline. Keep this in sync with export-sdxl-turbo.ps1's install
# block — same venv, same problems, same fix. See that script for the
# detailed reasoning behind each pin.
#
# Headline: optimum 2.x broke transformers.modeling_utils API; the
# `optimum[onnxruntime]` extra installs CPU-only ORT (which conflicts
# with onnxruntime-gpu); torch 2.5+ defaults to the dynamo exporter that
# optimum 1.x can't post-process. Pin everything to the Oct-2024 stack.
#
# sentencepiece: required for T5 / Llama / Gemma family tokenizers.
#   FLUX, SD3.x, AuraFlow, Sana all use T5 — without sentencepiece they
#   fail at "Cannot instantiate this tokenizer from a slow version" during
#   pipeline load. Cheap install (~3 MB).
# accelerate: enables low_cpu_mem_usage path for large models. Required
#   in practice for FLUX, SD3.5, JuggernautXL.
# onnxscript: torch's ONNX exporter dependency on >= 2.5 (kept for
#   forward-compat; harmless on 2.4).

Write-Host 'Cleaning stale packages from prior export attempts ...' -ForegroundColor Cyan
# try/catch swallows PS 5.1 NativeCommandError on pip's stderr-warning when a
# package isn't installed. Empty catch is intentional.
try { pip uninstall -y onnxruntime onnxruntime-gpu optimum optimum-onnx transformers diffusers *>$null } catch { }

Write-Host 'Installing optimum 1.24 + transformers 4.45 + diffusers 0.31 + onnxruntime-gpu (pinned) ...' -ForegroundColor Cyan
pip install --quiet `
    'optimum[onnxruntime-gpu,diffusers]==1.24.0' `
    'transformers==4.45.2' `
    'diffusers==0.31.0' `
    sentencepiece `
    accelerate `
    onnxscript

Write-Host 'Installing CUDA torch 2.4 (legacy exporter) from pytorch cu124 index ...' -ForegroundColor Cyan
# torch 2.4.x keeps the legacy TorchScript exporter as default; optimum
# 1.24's post-export pipeline expects external-weight files only the
# legacy exporter produces. cu124 is the highest CUDA wheel published
# for 2.4.x; cu128-capable drivers run cu124 fine (forward compat).
pip install --quiet --upgrade 'torch<2.5' --index-url https://download.pytorch.org/whl/cu124

# Verify both torch CUDA AND ORT CUDAExecutionProvider are usable. Either
# being CPU-only silently fails ~30 s into a 30-minute conversion.
$cudaCheck = & .\.venv\Scripts\python.exe -c @"
import torch, onnxruntime
torch_ok = torch.cuda.is_available()
ort_ok = 'CUDAExecutionProvider' in onnxruntime.get_available_providers()
print(f'{torch_ok},{ort_ok},{onnxruntime.get_available_providers()}')
"@ 2>&1
if ($LASTEXITCODE -ne 0 -or -not $cudaCheck.Trim().StartsWith('True,True,')) {
    Write-Host ''
    Write-Host 'ERROR: GPU runtime not available after install.' -ForegroundColor Red
    Write-Host "Detected: $cudaCheck" -ForegroundColor Red
    Write-Host 'Possible causes: no NVIDIA GPU, outdated driver (need 525+), CUDA toolkit mismatch,' -ForegroundColor Red
    Write-Host 'or onnxruntime CPU package still resident (try: pip uninstall onnxruntime onnxruntime-gpu, then rerun).' -ForegroundColor Red
    deactivate
    exit 1
}

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
        # --trust-remote-code is opt-in per model entry. Required for
        # repos that ship custom modeling code (Phi-3.5-Vision uses
        # modeling_phi3_v.py rather than a stock transformers
        # architecture). Don't enable it globally; only for models we've
        # explicitly verified.
        if ($model.ContainsKey('TrustRemoteCode') -and $model.TrustRemoteCode) {
            $extraArgs += @('--trust-remote-code')
        }
        # --dtype controls the export precision. Diffusion models default
        # to fp32 (huge); fp16 halves disk + VRAM at no measurable quality
        # cost for 4-step distilled output. bf16 is interchangeable with
        # fp16 on consumer NVIDIA hardware. fp8 is not supported by
        # optimum-cli for diffusion models -- post-export quantization or
        # pre-quantized community models would be needed instead.
        if ($model.ContainsKey('Dtype') -and $model.Dtype) {
            $extraArgs += @('--dtype', $model.Dtype)
        }
        # Heavy diffusion models (FLUX, SD3.5, SDXL-Lightning derivatives)
        # benefit from forcing CUDA + skipping post-export validation.
        # Without these, optimum-cli sometimes falls back to CPU for the
        # validation comparison, which on a 12B-param transformer can hang
        # for hours. Models with Library = 'diffusers' opt in by default;
        # transformer-library models get the lighter validation path which
        # rarely hits the slow case.
        if ($model.ContainsKey('Library') -and $model.Library -eq 'diffusers')
        {
            $extraArgs += @('--device', 'cuda', '--no-post-process')
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
