# Exports Openjourney v4 (SD 1.5 finetune) + Hyper-SD 4-step LoRA to ONNX.
#
# Same pipeline shape as Realistic Vision V6 + Hyper-SD: SD 1.5 architecture
# (single CLIP-L text encoder, 768 hidden dim, 512x512 native), same
# diffusers folder layout, same C# loader (StableDiffusionTurboModel).
#
# Why Openjourney v4:
#   - Trained by PromptHero on Midjourney v4 outputs. Reproduces Midjourney's
#     dramatic lighting, painterly atmosphere, and cinematic composition
#     fingerprint without the proprietary service. Distinct from anything
#     else in the SD 1.5 finetune ecosystem.
#   - Excellent fit for D&D set-pieces — environments, atmospheric scenes,
#     "the party approaches the ancient temple at dusk" prompts. Less
#     character-portrait-focused than RV, more stylized than epiCRealism.
#   - Ships with its own bundled VAE; no separate sd-vae-ft-mse pairing.
#
# Pipeline:
#   1. Load Openjourney v4 via diffusers (uses its bundled VAE)
#   2. Load Hyper-SD-15 4-step LoRA via peft, fuse it into the UNet
#   3. Save the fused pipeline to a temp directory
#   4. Run optimum-cli export onnx on the fused pipeline
#
# Requirements:
#   - Python 3.10 venv at .venv\ (existing one works)
#   - ~3 GB free disk for the download + ~2 GB for the ONNX output
#   - Internet connection
#   - 5-10 minutes
#
# Idempotent: safe to rerun. Reuses an existing .venv.
#
# Usage:
#   ./scripts/export-openjourney-hyper.ps1
#       — exports to $env:DATUMV_MODELS\openjourney-hyper-onnx (FP32)
#   ./scripts/export-openjourney-hyper.ps1 -Fp16
#       — exports as FP16 (~half the disk + VRAM)
#   ./scripts/export-openjourney-hyper.ps1 -OutputDirectory C:\foo

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUMV_MODELS) {
            Join-Path $env:DATUMV_MODELS 'openjourney-hyper-onnx'
        } else {
            throw 'Set $env:DATUMV_MODELS or pass -OutputDirectory <path>.'
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
# Same pinned stack as the other diffusion export scripts:
#   - optimum 1.24.0: last 1.x release with the [diffusers] extra; 2.x broke API
#   - transformers 4.45.2 + diffusers 0.31.0: known-good with optimum 1.24
#   - peft: required for load_lora_weights + fuse_lora (the LoRA merge step)
#   - onnxscript: torch >=2.5 needs it for ONNX export
#   - accelerate: faster pipeline loads, silences low-cpu-mem warning
#
# torch is installed explicitly from the PyTorch CUDA index so pip doesn't
# silently substitute the CPU-only wheel.
Write-Host 'Cleaning stale packages from prior export attempts ...' -ForegroundColor Cyan
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

# 4. Merge: load Openjourney v4 (which ships with its own VAE), fuse the
#    Hyper-SD-15 4-step LoRA into the UNet, save the fully-baked pipeline.
#
# Note on prompts: Openjourney v3 used a "mdjrny-v4 style" trigger token,
# but v4 dropped the trigger requirement — prompts work without any prefix.
# (You can still prepend descriptors like "cinematic lighting, dramatic
# composition" to lean further into the Midjourney aesthetic.)
$mergedDir = Join-Path $env:TEMP "openjourney-hyper-merged-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
Write-Host "Merging Openjourney v4 + Hyper-SD 4-step LoRA at $mergedDir ..." -ForegroundColor Cyan

$mergeScript = @"
import sys, torch
from diffusers import StableDiffusionPipeline
from huggingface_hub import hf_hub_download

merged_dir = sys.argv[1]

print('Loading Openjourney v4 base (with its bundled VAE) ...')
pipe = StableDiffusionPipeline.from_pretrained(
    'prompthero/openjourney-v4',
    torch_dtype=torch.float32,
    safety_checker=None,
    requires_safety_checker=False,
)

# Same Hyper-SD-15 4-step LoRA used across SD 1.5 finetunes.
print('Loading Hyper-SD 4-step LoRA ...')
lora_path = hf_hub_download('ByteDance/Hyper-SD', 'Hyper-SD15-4steps-lora.safetensors')
pipe.load_lora_weights(lora_path)
pipe.fuse_lora()
pipe.unload_lora_weights()

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
Write-Host "Next: register a model that loads this with StableDiffusionTurboModel and steps=4." -ForegroundColor DarkGray
Write-Host "      Same loader / same pipeline shape as the realistic_vision_hyper registration." -ForegroundColor DarkGray
