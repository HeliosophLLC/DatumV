# Exports SDXL base + ByteDance's SDXL-Lightning 2-step UNet to ONNX.
#
# SDXL-Lightning is ByteDance's progressive-distillation answer to SDXL-Turbo
# (which is ADD-distilled). The 2-step variant produces noticeably more stable
# outputs across seeds at half the wall-clock cost of SDXL-Turbo's recommended
# 4-step operating point. Same SDXL diffusers folder layout (text_encoder/,
# text_encoder_2/, unet/, vae_decoder/, vae_encoder/, tokenizer/, tokenizer_2/,
# scheduler/), same ~2.6B-param UNet shape, same 1024x1024 native resolution.
#
# IMPORTANT — scheduler differences vs SDXL-Turbo:
#
#   1. PREDICTION TYPE. The SDXL-Lightning 2-step UNet is trained for
#      x0/SAMPLE prediction, not epsilon/NOISE prediction. The Euler update
#      rule changes:
#        epsilon: latents = latents - deriv * dt
#        sample : latents = pred_x0 + sigma_next * (latents - pred_x0) / sigma
#      The existing SdxlTurboModel hardcodes epsilon. Loading this export
#      without a sample-prediction branch in the model will produce garbage.
#
#      (Lightning 4-step and 8-step are epsilon-prediction; only 2-step is
#       sample-prediction. If you switch step counts later, check the
#       ByteDance/SDXL-Lightning model card.)
#
#   2. TIMESTEP SPACING. Lightning needs timestep_spacing="trailing" instead
#      of the default "linspace". This affects which discrete training
#      timesteps the sigma schedule maps to. The change is small but matters.
#
#   3. CFG. Lightning is trained for CFG=1 (no classifier-free guidance). Like
#      Turbo, this means no negative prompt — pass only positive embeddings.
#
# Why convert instead of downloading a pre-built ONNX? Same reason as SDXL-Turbo:
# most published SDXL ONNX builds are optimised for DirectML and use NhwcConv
# operators that the standard CPU/CUDA execution providers don't handle.
#
# Pipeline:
#   1. Load SDXL base 1.0 via diffusers
#   2. Download sdxl_lightning_2step_unet.safetensors and load it as the UNet
#      (full UNet replacement, not a LoRA — Lightning publishes both, the full
#       UNet is preferable for ONNX export quality)
#   3. Save the modified pipeline to a temp directory
#   4. Run optimum-cli export onnx
#
# Requirements:
#   - Python 3.10 venv at .venv\ (existing one works)
#   - ~12 GB free disk for the FP32 ONNX output (~6 GB for FP16)
#   - ~7 GB free disk for the SDXL base download + ~5 GB for the Lightning UNet
#   - Internet connection
#   - 15-25 minutes (UNet trace is the bottleneck, same as SDXL-Turbo)
#
# Idempotent: safe to rerun.
#
# Usage:
#   ./scripts/export-sdxl-lightning.ps1
#       — exports to $env:DATUM_MODELS\sdxl-lightning-2step-onnx (FP32)
#   ./scripts/export-sdxl-lightning.ps1 -Fp16
#       — exports as FP16 (~half the disk + VRAM); VAE decoder kept FP32
#         (see fp16 fix block at the bottom for the softmax-overflow rationale)
#   ./scripts/export-sdxl-lightning.ps1 -OutputDirectory C:\foo

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUM_MODELS) {
            Join-Path $env:DATUM_MODELS 'sdxl-lightning-2step-onnx'
        } else {
            throw 'Set $env:DATUM_MODELS or pass -OutputDirectory <path>.'
        }
    ),

    [Parameter()]
    [switch]$Fp16
)

$ErrorActionPreference = 'Stop'

# 1. Reuse the project-local Python 3.10 venv. Create if missing.
if (-not (Test-Path '.venv\Scripts\python.exe')) {
    Write-Host 'Creating Python 3.10 virtual environment at .venv\ ...' -ForegroundColor Cyan
    py -3.10 -m venv .venv
} else {
    Write-Host '.venv exists, reusing.' -ForegroundColor DarkGray
}

# 2. Activate.
& .\.venv\Scripts\Activate.ps1

# 3. Install conversion tooling + CUDA torch. Same pinned stack as the other
#    diffusion export scripts; see export-sdxl-turbo.ps1 for the full rationale
#    on each pin (optimum 1.24, transformers 4.45.2, diffusers 0.31.0, etc.).
Write-Host 'Cleaning stale packages from prior export attempts ...' -ForegroundColor Cyan
try { pip uninstall -y onnxruntime onnxruntime-gpu optimum optimum-onnx transformers diffusers *>$null } catch { }

Write-Host 'Installing optimum 1.24 + transformers 4.45 + diffusers 0.31 + onnxscript + accelerate ...' -ForegroundColor Cyan
pip install --quiet `
    'optimum[onnxruntime-gpu,diffusers]==1.24.0' `
    'transformers==4.45.2' `
    'diffusers==0.31.0' `
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

# 4. Build a "merged" SDXL pipeline whose UNet is replaced by the Lightning
#    2-step weights. We can't pass the Lightning UNet to optimum-cli directly;
#    optimum's exporter loads from a HuggingFace repo path or a local pipeline
#    directory, so we construct the local directory ourselves.
#
#    The temp directory holds ~12 GB during the export and is deleted at the end.
$mergedDir = Join-Path $env:TEMP "sdxl-lightning-merged-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
Write-Host "Building SDXL + Lightning 2-step UNet pipeline at $mergedDir ..." -ForegroundColor Cyan

$mergeScript = @"
import sys, torch
from diffusers import StableDiffusionXLPipeline, UNet2DConditionModel
from huggingface_hub import hf_hub_download
from safetensors.torch import load_file

merged_dir = sys.argv[1]
base = 'stabilityai/stable-diffusion-xl-base-1.0'

print('Loading SDXL base 1.0 pipeline ...')
pipe = StableDiffusionXLPipeline.from_pretrained(
    base,
    torch_dtype=torch.float32,
    variant=None,
)

print('Downloading SDXL-Lightning 2-step UNet weights ...')
unet_path = hf_hub_download('ByteDance/SDXL-Lightning', 'sdxl_lightning_2step_unet.safetensors')

print('Loading Lightning weights into a fresh UNet2DConditionModel ...')
# Construct an empty UNet from the SDXL config, then overwrite its state dict.
# This avoids any mismatch from the base SDXL UNet's loaded weights bleeding in.
unet_config = UNet2DConditionModel.load_config(base, subfolder='unet')
unet = UNet2DConditionModel.from_config(unet_config).to(torch.float32)
state = load_file(unet_path, device='cpu')
missing, unexpected = unet.load_state_dict(state, strict=False)
if unexpected:
    print(f'WARNING: {len(unexpected)} unexpected keys in Lightning state dict (first 3: {unexpected[:3]})')
if missing:
    # Lightning publishes a complete UNet; a non-empty 'missing' list means
    # the SDXL UNet config drifted. Fail loudly rather than silently using
    # randomly-initialised weights.
    print(f'ERROR: {len(missing)} missing keys when loading Lightning weights.')
    print(f'  First 3: {missing[:3]}')
    sys.exit(2)

pipe.unet = unet

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
    Write-Host "ERROR: merge step failed (exit $mergeExit)." -ForegroundColor Red
    if (Test-Path $mergedDir) { Remove-Item -Recurse -Force $mergedDir }
    deactivate
    exit 1
}

# 5. Convert the merged pipeline to ONNX. See export-sdxl-turbo.ps1 for the
#    rationale on --device cuda / --no-post-process / --atol 0.1 / opset 14.
$dtypeLabel = if ($Fp16) { 'FP16 (~6 GB)' } else { 'FP32 (~12 GB)' }
Write-Host "Exporting fused pipeline to $OutputDirectory ($dtypeLabel) ..." -ForegroundColor Cyan
Write-Host "(this takes 15-25 minutes; UNet is the bottleneck)" -ForegroundColor DarkGray

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
    Write-Host "The output directory may contain a partial/mixed-precision dump. Delete it before retrying:" -ForegroundColor Red
    Write-Host "  Remove-Item -Recurse -Force '$OutputDirectory'" -ForegroundColor Yellow
    Remove-Item -Recurse -Force $mergedDir
    deactivate
    exit 1
}

# 6. Tidy up the temp merged directory.
Remove-Item -Recurse -Force $mergedDir

# 7. Convert the VAE decoder back to FP32 when the pipeline is FP16.
#
# Same rationale as export-sdxl-turbo.ps1: the SDXL VAE decoder's mid-block
# attention runs at 128x128 (sequence length 16384) where the QK^T dot products
# accumulate past log(65504)~=11, fp16 softmax overflows, and the decoded
# image becomes all-black. Keep the heavy UNet + text encoders in fp16 (the
# big VRAM saving) and convert only the small ~300 MB VAE decoder back to
# fp32. The mixed-precision export is numerically stable end-to-end.
if ($Fp16) {
    Write-Host "Converting VAE decoder to FP32 for numerical stability (fp16 softmax overflows at 128x128)..." -ForegroundColor Cyan

    $pyScript = @'
import sys, onnx, numpy as np
from onnx import TensorProto, numpy_helper, load, save

path = sys.argv[1]
model = load(path)

def fp16_to_fp32(tensor):
    """In-place rewrite of a TensorProto from FLOAT16 to FLOAT."""
    arr = numpy_helper.to_array(tensor).astype(np.float32)
    new = numpy_helper.from_array(arr, tensor.name if tensor.name else None)
    tensor.CopyFrom(new)

initializers = 0
constants = 0
attr_tensors = 0
casts = 0

for init in model.graph.initializer:
    if init.data_type == TensorProto.FLOAT16:
        fp16_to_fp32(init)
        initializers += 1

for container in [model.graph.input, model.graph.output, model.graph.value_info]:
    for vi in container:
        if vi.type.tensor_type.elem_type == TensorProto.FLOAT16:
            vi.type.tensor_type.elem_type = TensorProto.FLOAT

for node in model.graph.node:
    for attr in node.attribute:
        if attr.name == 'to' and attr.type == onnx.AttributeProto.INT and attr.i == TensorProto.FLOAT16:
            attr.i = TensorProto.FLOAT
            casts += 1
        if attr.type == onnx.AttributeProto.TENSOR and attr.t.data_type == TensorProto.FLOAT16:
            fp16_to_fp32(attr.t)
            if node.op_type == 'Constant':
                constants += 1
            else:
                attr_tensors += 1
        if attr.type == onnx.AttributeProto.TENSORS:
            for t in attr.tensors:
                if t.data_type == TensorProto.FLOAT16:
                    fp16_to_fp32(t)
                    attr_tensors += 1

try:
    model = onnx.shape_inference.infer_shapes(model, strict_mode=False)
except Exception as e:
    print(f"  (shape inference advisory: {e})")

save(model, path)
print(f"VAE decoder converted to FP32 (initializers={initializers}, "
      f"Constant nodes={constants}, other-attr tensors={attr_tensors}, "
      f"Cast(to=fp16)->Cast(to=fp32)={casts})")
'@

    $tmpPy = [System.IO.Path]::GetTempFileName() -replace '\.tmp$', '.py'
    Set-Content -Path $tmpPy -Value $pyScript -Encoding utf8
    & .\.venv\Scripts\python.exe $tmpPy "$OutputDirectory\vae_decoder\model.onnx"
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'WARNING: VAE decoder FP32 conversion failed. The fp16 VAE may produce NaN output (black images).' -ForegroundColor Yellow
    }
    Remove-Item $tmpPy -Force
}

# 8. Tidy up the venv.
deactivate

Write-Host ""
Write-Host "Done. ONNX files at $OutputDirectory" -ForegroundColor Green
if ($Fp16) {
    Write-Host "Mixed-precision export: UNet + text encoders (FP16), VAE decoder (FP32)." -ForegroundColor DarkGray
}
Write-Host "Verify with: Get-ChildItem -Recurse $OutputDirectory" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Next: register a model that loads this with steps=2, sample-prediction Euler update," -ForegroundColor DarkGray
Write-Host "      timestep_spacing='trailing', and CFG=1 (no negative prompt). The existing" -ForegroundColor DarkGray
Write-Host "      SdxlTurboModel needs a prediction-type branch before it can load this export." -ForegroundColor DarkGray
