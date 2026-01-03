# Exports Realistic Vision V6 (SD 1.5 finetune) + Hyper-SD 4-step LoRA to ONNX.
#
# This is a "drop-in stability upgrade" for SD-Turbo: same SD-1.x diffusers
# folder layout (text_encoder/, unet/, vae_decoder/, vae_encoder/, tokenizer/,
# scheduler/), same ~860M-param UNet, same 512x512 native resolution, so the
# existing C# StableDiffusionTurboModel can load it with only a text-embedding
# dim change (1024 -> 768; SD 1.5 uses CLIP-L instead of OpenCLIP-H).
#
# Why this combo:
#   - Realistic Vision V6 is one of the most-downloaded SD 1.5 finetunes for
#     people / portraits. Trained on a narrower distribution than base SD 1.5,
#     which is why it's *more stable* across seeds for the same prompt.
#   - Hyper-SD is ByteDance's 2024 distillation method (trajectory segmented
#     consistency distillation + RLHF). At 4 steps with the standard Euler
#     scheduler and CFG=1 it produces noticeably calmer outputs than SD-Turbo
#     (ADD-distilled) while running at the same wall-clock cost.
#   - Realistic Vision V6 ships without a VAE ("noVAE" suffix). The merge
#     step below pairs it with stabilityai/sd-vae-ft-mse, the SD 1.5 community
#     standard fine-tuned VAE, before export.
#
# Pipeline:
#   1. Load Realistic Vision V6 + sd-vae-ft-mse via diffusers
#   2. Load Hyper-SD-15 4-step LoRA via peft, fuse it into the UNet
#   3. Save the fused pipeline to a temp directory
#   4. Run optimum-cli export onnx on the fused pipeline
#
# Requirements:
#   - Python 3.10 venv at .venv\ (the existing one from
#     export-sd-turbo.ps1 / export-sdxl-turbo.ps1 works)
#   - ~3 GB free disk for the download + ~2 GB for the ONNX output
#   - Internet connection
#   - 5-10 minutes (UNet export is the bottleneck, same as SD-Turbo)
#
# Idempotent: safe to rerun. Reuses an existing .venv.
#
# Usage:
#   ./scripts/export-realistic-vision-hyper.ps1
#       — exports to $env:DATUM_MODELS\realistic-vision-hyper-onnx (FP32)
#   ./scripts/export-realistic-vision-hyper.ps1 -Fp16
#       — exports as FP16 (~half the disk + VRAM)
#   ./scripts/export-realistic-vision-hyper.ps1 -OutputDirectory C:\foo
#       — exports to a specific directory

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUM_MODELS) {
            Join-Path $env:DATUM_MODELS 'realistic-vision-hyper-onnx'
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
# Same pinned stack as the SD-Turbo / SDXL-Turbo scripts. See export-sd-turbo.ps1
# for the full rationale; the short version is:
#   - optimum 1.24.0: last release with the [diffusers] extra; 2.x broke API
#   - transformers 4.45.2 + diffusers 0.31.0: known-good with optimum 1.24
#   - peft: required for load_lora_weights + fuse_lora (the LoRA merge step)
#   - onnxscript: torch >=2.5 needs it for ONNX export
#   - accelerate: faster pipeline loads, silences low-cpu-mem warning
#
# torch is installed explicitly from the PyTorch CUDA index so pip doesn't
# silently substitute the CPU-only wheel.
Write-Host 'Cleaning stale packages from prior export attempts ...' -ForegroundColor Cyan
# The empty catch swallows pip's "not installed" stderr noise that PS 5.1
# wraps as NativeCommandError when $ErrorActionPreference=Stop. See the
# export-sd-turbo.ps1 comment for the full reasoning.
try { pip uninstall -y onnxruntime onnxruntime-gpu optimum optimum-onnx transformers diffusers *>$null } catch { }

Write-Host 'Installing optimum 1.24 + transformers 4.45 + diffusers 0.31 + peft + onnxscript + accelerate ...' -ForegroundColor Cyan
pip install --quiet `
    'optimum[onnxruntime-gpu,diffusers]==1.24.0' `
    'transformers==4.45.2' `
    'diffusers==0.31.0' `
    'peft>=0.11' `
    onnxscript `
    accelerate

Write-Host 'Installing CUDA torch 2.4 (legacy exporter) from pytorch cu124 index ...' -ForegroundColor Cyan
# torch 2.5+ defaults to the dynamo ONNX exporter, which optimum 1.24's
# post-export cleanup can't handle. 2.4.x keeps the legacy TorchScript
# exporter as default. cu124 is the highest CUDA wheel for 2.4.x.
pip install --quiet --upgrade 'torch<2.5' --index-url https://download.pytorch.org/whl/cu124

# 3a. Verify BOTH torch CUDA AND ORT CUDAExecutionProvider are usable.
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

# 4. Merge: load Realistic Vision V6 + sd-vae-ft-mse + Hyper-SD 4-step LoRA,
#    fuse the LoRA into the UNet, save a fully-baked pipeline that optimum-cli
#    can then export. We can't ask optimum-cli to do this directly because the
#    LoRA-merge step has to happen on the PyTorch model before tracing.
#
# The merged pipeline lives in a temp directory under $env:TEMP and is deleted
# at the end. ~3 GB on disk while the export runs.
$mergedDir = Join-Path $env:TEMP "rv-hyper-merged-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
Write-Host "Merging Realistic Vision V6 + sd-vae-ft-mse + Hyper-SD 4-step LoRA at $mergedDir ..." -ForegroundColor Cyan

$mergeScript = @"
import sys, torch
from diffusers import StableDiffusionPipeline, AutoencoderKL
from huggingface_hub import hf_hub_download

merged_dir = sys.argv[1]

# Realistic Vision V6 ships without a VAE ('noVAE' suffix). Pair it with
# the SD 1.5 community standard fine-tuned VAE.
print('Loading Realistic Vision V6 base ...')
vae = AutoencoderKL.from_pretrained('stabilityai/sd-vae-ft-mse', torch_dtype=torch.float32)
pipe = StableDiffusionPipeline.from_pretrained(
    'SG161222/Realistic_Vision_V6.0_B1_noVAE',
    vae=vae,
    torch_dtype=torch.float32,
    safety_checker=None,
    requires_safety_checker=False,
)

# Download Hyper-SD 4-step LoRA for SD 1.5. Note: ByteDance/Hyper-SD also
# publishes a 1-step LoRA, but that one requires the TCD scheduler (different
# update rule than Euler). The 4-step LoRA works with the standard Euler /
# DPM schedulers at CFG=1.
print('Loading Hyper-SD 4-step LoRA ...')
lora_path = hf_hub_download('ByteDance/Hyper-SD', 'Hyper-SD15-4steps-lora.safetensors')
pipe.load_lora_weights(lora_path)
pipe.fuse_lora()
pipe.unload_lora_weights()  # the weights are now baked into the UNet; drop the adapter

print(f'Saving merged pipeline to {merged_dir} ...')
pipe.save_pretrained(merged_dir)
print('Done merging.')
"@

$tmpMerge = [System.IO.Path]::GetTempFileName() -replace '\.tmp$', '.py'
Set-Content -Path $tmpMerge -Value $mergeScript -Encoding utf8
& .\.venv\Scripts\python.exe $tmpMerge $mergedDir
$mergeExit = $LASTEXITCODE
Remove-Item $tmpMerge -Force
if ($mergeExit -ne 0) {
    Write-Host ''
    Write-Host "ERROR: merge step failed (exit $mergeExit). The fused pipeline was not produced." -ForegroundColor Red
    if (Test-Path $mergedDir) { Remove-Item -Recurse -Force $mergedDir }
    deactivate
    exit 1
}

# 5. Convert the merged pipeline to ONNX.
#
# --device cuda     forces export + validation to GPU (CPU fp16 produces
#                   max-diff > 1.0 + intermittent NaN).
# --no-post-process skips ONNX graph constant-folding (does NOT skip
#                   numerical validation).
# --atol 0.1        FP16 validation tolerance; default 1e-5 is fp32-specific.
#
# Opset is left at optimum's default (14). Forcing higher opsets pushes the
# legacy TorchScript exporter into op variants that misbehave in fp16.
$dtypeLabel = if ($Fp16) { 'FP16 (~1.7 GB UNet)' } else { 'FP32 (~3.4 GB UNet)' }
Write-Host "Exporting fused pipeline to $OutputDirectory ($dtypeLabel) ..." -ForegroundColor Cyan
Write-Host "(this takes 5-10 minutes; UNet is the bottleneck)" -ForegroundColor DarkGray

if ($Fp16) {
    optimum-cli export onnx `
        --model $mergedDir `
        --task text-to-image `
        --dtype fp16 `
        --device cuda `
        --no-post-process `
        --atol 0.1 `
        $OutputDirectory
} else {
    optimum-cli export onnx `
        --model $mergedDir `
        --task text-to-image `
        --device cuda `
        --no-post-process `
        $OutputDirectory
}

if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host "ERROR: optimum-cli exited with code $LASTEXITCODE. The export failed." -ForegroundColor Red
    Write-Host "The output directory may contain a partial dump. Delete it before retrying:" -ForegroundColor Red
    Write-Host "  Remove-Item -Recurse -Force '$OutputDirectory'" -ForegroundColor Yellow
    Remove-Item -Recurse -Force $mergedDir
    deactivate
    exit 1
}

# 6. Tidy up the temp merged directory.
Remove-Item -Recurse -Force $mergedDir

# 7. Tidy up the venv.
deactivate

Write-Host ""
Write-Host "Done. ONNX files at $OutputDirectory" -ForegroundColor Green
Write-Host "Verify with: Get-ChildItem -Recurse $OutputDirectory" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Next: register a model that loads this with embedding-dim=768 (CLIP-L) and steps=4." -ForegroundColor DarkGray
Write-Host "      The existing StableDiffusionTurboModel works almost as-is; only the text-embedding" -ForegroundColor DarkGray
Write-Host "      dim differs (1024 OpenCLIP-H -> 768 CLIP-L). Same Euler scheduler, same sigma schedule." -ForegroundColor DarkGray
