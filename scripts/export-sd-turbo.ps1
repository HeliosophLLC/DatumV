# Exports Stability AI's SD-Turbo from PyTorch (HuggingFace) to ONNX.
#
# stabilityai/sd-turbo is a 1-step text-to-image model. The PyTorch
# weights (~2.5 GB safetensors) get converted into the diffusers-format
# ONNX layout (text_encoder/, unet/, vae_decoder/, vae_encoder/,
# tokenizer/, scheduler/) that DatumIngest's StableDiffusionTurboModel
# expects.
#
# Why convert instead of downloading a pre-built ONNX? Most published
# SD-Turbo ONNX builds (e.g. tlwu/sd-turbo-onnxruntime) are optimised
# for the DirectML execution provider — they use Microsoft-specific
# NhwcConv ops that CPU/CUDA EPs don't handle. optimum-cli produces
# a portable build with standard Conv ops that works everywhere ONNX
# Runtime runs.
#
# Requirements:
#   - Python 3.10 venv at .venv\ (the export-vit-gpt-image-captioning.ps1
#     script already creates this; reuse it)
#   - ~3 GB free disk for the download + ~2 GB for the ONNX output
#   - Internet connection
#   - Some patience: the conversion takes 5-10 minutes (UNet is ~860M
#     params and the export traces every operation)
#
# Idempotent: safe to rerun. Reuses an existing .venv.
#
# Usage:
#   ./scripts/export-sd-turbo.ps1
#       — exports to $env:DATUM_MODELS\sd-turbo-onnx (FP32 default)
#   ./scripts/export-sd-turbo.ps1 -Fp16
#       — exports as FP16 (~half the disk + VRAM, 2-4x faster compute on
#         consumer NVIDIA cards; quality difference imperceptible at the
#         1-step distillation)
#   ./scripts/export-sd-turbo.ps1 -OutputDirectory C:\foo
#       — exports to a specific directory

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUM_MODELS) {
            Join-Path $env:DATUM_MODELS 'sd-turbo-onnx'
        } else {
            throw 'Set $env:DATUM_MODELS or pass -OutputDirectory <path>.'
        }
    ),

    [Parameter()]
    [switch]$Fp16
)

$ErrorActionPreference = 'Stop'

# 1. Project-local Python 3.10 venv. Reuse if present.
if (-not (Test-Path '.venv\Scripts\python.exe')) {
    Write-Host 'Creating Python 3.10 virtual environment at .venv\ ...' -ForegroundColor Cyan
    py -3.10 -m venv .venv
} else {
    Write-Host '.venv exists, reusing.' -ForegroundColor DarkGray
}

# 2. Activate.
& .\.venv\Scripts\Activate.ps1

# 3. Install conversion tooling + CUDA torch.
#
# Pin optimum to 1.x: optimum 2.0+ broke API compatibility — it imports
# `get_parameter_dtype` from `transformers.modeling_utils`, which the
# latest transformers no longer exports. The 1.x line is stable for our
# diffusers + onnxruntime exports and gets the same export quality.
#
# `[onnxruntime-gpu,diffusers]` extras pull in:
#   - onnxruntime-gpu   (validation pass; CPU fp16 is sloppy and produces
#                        max-diff > 1.0 + intermittent NaN)
#   - diffusers + transformers (the model definitions themselves)
#
# torch is installed explicitly from the PyTorch CUDA index so pip
# doesn't silently substitute the CPU-only wheel.

# Defensively uninstall conflicting packages from any prior run:
#   - onnxruntime (CPU): cannot coexist with onnxruntime-gpu, and the
#     optimum[onnxruntime] extra used to install it.
#   - optimum: pip --upgrade alone won't downgrade a stuck 2.x install,
#     so force a clean reinstall to guarantee the <2.0 pin takes effect.
Write-Host 'Cleaning stale packages from prior export attempts ...' -ForegroundColor Cyan
# In Windows PS 5.1, pip's "Skipping X as it is not installed" warning hits
# stderr, which PS wraps as NativeCommandError BEFORE the redirect operator
# applies. With $ErrorActionPreference=Stop the script then halts. try/catch
# is the only construct that reliably swallows this wrapper. The empty catch
# is intentional: a missing package is the success case for this step.
#
# Why a wide uninstall sweep:
#   - onnxruntime (CPU): conflicts with onnxruntime-gpu
#   - optimum / optimum-onnx: optimum 2.x leaves an `optimum-onnx` plugin
#     pinned to optimum~=2.1, which conflicts with our 1.x pin and produces
#     "Multiple distributions found for package optimum" if not removed.
#   - transformers / diffusers: the latest releases now require bleeding-edge
#     deps (e.g., diffusers' nucleusmoe pipeline imports Qwen3VLForConditional-
#     Generation from transformers). Wipe and reinstall pinned versions.
try { pip uninstall -y onnxruntime onnxruntime-gpu optimum optimum-onnx transformers diffusers *>$null } catch { }

Write-Host 'Installing optimum 1.24 + transformers 4.45 + diffusers 0.31 + onnxscript + accelerate ...' -ForegroundColor Cyan
# Why these specific pins:
#   - optimum 1.24.0: last 1.x release with the [diffusers] extra (1.25+
#     dropped it). 2.0+ broke the transformers.modeling_utils API.
#   - transformers 4.45.2: stable Oct-2024 release used by optimum 1.24's
#     exporter; satisfies the >=4.36 floor without the cutting-edge model
#     classes that newer diffusers wants.
#   - diffusers 0.31.0: SDXL-Turbo export path is stable; predates the
#     nucleusmoe / Qwen3VL pipeline additions.
#   - onnxscript: torch >= 2.5 split its ONNX exporter into a separate
#     package; without it, torch.onnx.export fails on import.
#   - accelerate: silences the "Cannot initialize model with low cpu memory
#     usage" warning and makes large pipeline loads much faster.
pip install --quiet `
    'optimum[onnxruntime-gpu,diffusers]==1.24.0' `
    'transformers==4.45.2' `
    'diffusers==0.31.0' `
    onnxscript `
    accelerate

Write-Host 'Installing CUDA torch 2.4 (legacy exporter) from pytorch cu124 index ...' -ForegroundColor Cyan
# Pin torch to 2.4.x with cu124 wheels. Why:
#   - torch 2.5+ defaults to the dynamo-based ONNX exporter, which emits a
#     single inlined .onnx file. Optimum 1.24's post-export cleanup expects
#     external-weight files (model.onnx.data) from the legacy TorchScript
#     exporter and crashes on os.remove() when they don't exist.
#   - torch 2.4.x keeps the legacy exporter as default → optimum 1.24's
#     pipeline works end-to-end.
#   - cu124 is the highest CUDA wheel published for 2.4.x; CUDA drivers
#     are forward-compatible, so a cu128-capable driver runs cu124 fine.
pip install --quiet --upgrade 'torch<2.5' --index-url https://download.pytorch.org/whl/cu124

# 3a. Verify BOTH torch CUDA AND ORT CUDAExecutionProvider are usable.
# torch.cuda is necessary for the PyTorch tracing pass; the ORT provider
# is necessary for the validation pass. Either being CPU-only silently
# corrupts fp16 exports.
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

# 4. Convert.
#
# --device cuda   Forces export + validation to run on GPU.
#
# --no-post-process  Skips ONNX graph post-processing (constant folding,
#                    shape inference optimisation). Does NOT skip the
#                    numerical validation pass.
#
# --atol 0.1      FP16-appropriate validation tolerance. The default 1e-5
#                 is fp32-specific; fp16 quantization noise accumulates to
#                 ~0.01-0.05 max diff over many layers, which is normal
#                 and does not affect output quality.
$dtypeLabel = if ($Fp16) { 'FP16 (~1.7 GB UNet)' } else { 'FP32 (~3.4 GB UNet)' }
Write-Host "Exporting SD-Turbo to $OutputDirectory ($dtypeLabel) ..." -ForegroundColor Cyan
Write-Host "(this takes 5-10 minutes; UNet is the bottleneck)" -ForegroundColor DarkGray
#
# Opset is left at optimum's default (14). With torch 2.4's legacy
# TorchScript exporter, LayerNorm/GroupNorm/etc. decompose into opset-14
# primitives — the well-tested SDXL export path. Forcing --opset 18
# pushes the legacy exporter into op variants that misbehave in fp16
# (the UNet returns all-NaN even though text encoders look correct).
if ($Fp16) {
    optimum-cli export onnx `
        --model stabilityai/sd-turbo `
        --task text-to-image `
        --dtype fp16 `
        --device cuda `
        --no-post-process `
        --atol 0.1 `
        $OutputDirectory
} else {
    optimum-cli export onnx `
        --model stabilityai/sd-turbo `
        --task text-to-image `
        --device cuda `
        --no-post-process `
        $OutputDirectory
}

# Trap optimum-cli failure — $ErrorActionPreference does not stop on
# native-exe non-zero exits, so partial/corrupt exports otherwise print
# "Done." silently.
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host "ERROR: optimum-cli exited with code $LASTEXITCODE. The export failed." -ForegroundColor Red
    Write-Host "The output directory may contain a partial/mixed-precision dump. Delete it before retrying:" -ForegroundColor Red
    Write-Host "  Remove-Item -Recurse -Force '$OutputDirectory'" -ForegroundColor Yellow
    deactivate
    exit 1
}

# 5. Tidy up.
deactivate

Write-Host ""
Write-Host "Done. ONNX files at $OutputDirectory" -ForegroundColor Green
Write-Host "Verify with: Get-ChildItem -Recurse $OutputDirectory" -ForegroundColor DarkGray
