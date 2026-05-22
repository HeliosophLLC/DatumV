# Exports Stability AI's SDXL-Turbo from PyTorch (HuggingFace) to ONNX.
#
# stabilityai/sdxl-turbo is a 1-4 step text-to-image model. Compared to
# SD-Turbo: dramatically better quality, 1024×1024 output (vs 512×512),
# dual text encoders (CLIP-L + OpenCLIP-G), and a much larger UNet
# (~2.6B params vs SD-Turbo's ~860M). Same diffusers folder layout
# convention.
#
# Why convert instead of downloading a pre-built ONNX? Same reason as
# SD-Turbo: most published SDXL-Turbo ONNX builds are optimised for
# DirectML and use NhwcConv operators that the standard CPU/CUDA
# execution providers don't handle. optimum-cli produces a portable
# build with standard Conv ops that works everywhere ONNX Runtime runs.
#
# Requirements:
#   - Python 3.10 venv at .venv\ (the existing one from
#     export-vit-gpt-image-captioning.ps1 / export-sd-turbo.ps1 works)
#   - ~12 GB free disk for the FP32 ONNX output (~6 GB for FP16)
#   - ~7 GB free disk for the PyTorch download (cached temporarily)
#   - Internet connection
#   - 15-25 minutes of patience: the UNet is ~2.6B params and ONNX
#     export traces every operation
#
# Idempotent: safe to rerun.
#
# Usage:
#   ./scripts/export-sdxl-turbo.ps1
#       — exports to $env:DATUMV_MODELS\sdxl-turbo-onnx (FP32)
#   ./scripts/export-sdxl-turbo.ps1 -Fp16
#       — exports to FP16 (~half the disk + VRAM)
#   ./scripts/export-sdxl-turbo.ps1 -OutputDirectory C:\foo
#       — exports to a specific directory

[CmdletBinding()]
param(
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUMV_MODELS) {
            Join-Path $env:DATUMV_MODELS 'sdxl-turbo-onnx'
        } else {
            throw 'Set $env:DATUMV_MODELS or pass -OutputDirectory <path>.'
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
# --device cuda   Forces both export and post-export validation to run on
#                 GPU. Without it, optimum-cli sometimes falls back to CPU
#                 for the validation pass, turning a 25-minute job into
#                 2+ hours.
#
# --no-post-process  Skips ONNX graph post-processing (constant folding,
#                    shape inference optimisation). NOT the same as skipping
#                    validation -- that is controlled by --atol below.
#
# --atol 0.1      Validation tolerance for FP16 exports. optimum compares
#                 the ONNX output against the fp32 PyTorch reference; fp16
#                 has inherent rounding error of ~0.001 per op and after
#                 hundreds of UNet layers the accumulated max-diff is
#                 typically 0.01-0.05. The default atol of 1e-5 is fp32-
#                 specific and causes a false-positive validation failure
#                 that aborts the script even though the files are valid.
#                 0.1 is tight enough to catch genuine corruption while
#                 passing normal fp16 precision noise.
$dtypeLabel = if ($Fp16) { 'FP16 (~6 GB)' } else { 'FP32 (~12 GB)' }
Write-Host "Exporting SDXL-Turbo to $OutputDirectory ($dtypeLabel) ..." -ForegroundColor Cyan
Write-Host "(this takes 15-25 minutes; UNet is the bottleneck)" -ForegroundColor DarkGray

#
# Opset is left at optimum's default (14). With torch 2.4's legacy
# TorchScript exporter, LayerNorm/GroupNorm/etc. decompose into opset-14
# primitives — the well-tested SDXL export path. Forcing --opset 18
# pushes the legacy exporter into op variants that misbehave in fp16
# (the UNet returns all-NaN even though text encoders look correct).
if ($Fp16) {
    optimum-cli export onnx `
        --model stabilityai/sdxl-turbo `
        --task text-to-image `
        --dtype fp16 `
        --device cuda `
        --no-post-process `
        --atol 0.1 `
        $OutputDirectory
} else {
    optimum-cli export onnx `
        --model stabilityai/sdxl-turbo `
        --task text-to-image `
        --device cuda `
        --no-post-process `
        $OutputDirectory
}

# Trap optimum-cli failure. PowerShell's $ErrorActionPreference doesn't
# stop on native-exe non-zero exits; without this guard a partial /
# corrupt export silently prints "Done." below.
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host "ERROR: optimum-cli exited with code $LASTEXITCODE. The export failed." -ForegroundColor Red
    Write-Host "The output directory may contain a partial/mixed-precision dump. Delete it before retrying:" -ForegroundColor Red
    Write-Host "  Remove-Item -Recurse -Force '$OutputDirectory'" -ForegroundColor Yellow
    deactivate
    exit 1
}

# 5. Convert VAE decoder to FP32 when the rest of the pipeline is FP16.
#
# The SDXL VAE decoder's mid-block attention operates at 128x128 spatial
# resolution (sequence length 16384). The QK^T dot products accumulate to
# values > log(65504) ~= 11, at which point fp16 softmax overflows and
# produces NaN, turning the entire decoded image black.
#
# Fix: keep the heavy UNet and text encoders in fp16 (the big VRAM saving),
# convert only the small VAE decoder (~300 MB) back to fp32. The resulting
# mixed-precision export is numerically stable end-to-end.
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

# 1. Initializers (model weights/biases stored as graph initializers).
for init in model.graph.initializer:
    if init.data_type == TensorProto.FLOAT16:
        fp16_to_fp32(init)
        initializers += 1

# 2. Type annotations on inputs, outputs, and intermediate value_info.
for container in [model.graph.input, model.graph.output, model.graph.value_info]:
    for vi in container:
        if vi.type.tensor_type.elem_type == TensorProto.FLOAT16:
            vi.type.tensor_type.elem_type = TensorProto.FLOAT

# 3. Walk every node attribute. Three things matter here:
#    - `Cast` nodes with `to=FLOAT16`: rewrite to FLOAT (identity for fp32 in/out).
#    - `Constant` nodes (and any node) with a tensor `value` attribute that is
#      FLOAT16: convert the bytes. THIS is what's needed for InstanceNormalization
#      / GroupNormalization scale & bias, which optimum's exporter wires through
#      Constant nodes rather than initializers.
#    - Repeated-tensor attributes (rare but possible).
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

# 4. Re-run shape inference so any cached intermediate type info is consistent.
#    If the result still has fp16 anywhere, ORT will tell us at load time.
try:
    model = onnx.shape_inference.infer_shapes(model, strict_mode=False)
except Exception as e:
    print(f"  (shape inference advisory: {e})")

save(model, path)
print(f"VAE decoder converted to FP32 (initializers={initializers}, "
      f"Constant nodes={constants}, other-attr tensors={attr_tensors}, "
      f"Cast(to=fp16)→Cast(to=fp32)={casts})")
'@

    $tmpPy = [System.IO.Path]::GetTempFileName() -replace '\.tmp$', '.py'
    Set-Content -Path $tmpPy -Value $pyScript -Encoding utf8
    & .\.venv\Scripts\python.exe $tmpPy "$OutputDirectory\vae_decoder\model.onnx"
    if ($LASTEXITCODE -ne 0) {
        Write-Host 'WARNING: VAE decoder FP32 conversion failed. The fp16 VAE may produce NaN output (black images).' -ForegroundColor Yellow
    }
    Remove-Item $tmpPy -Force
}

# 6. Tidy up.
deactivate

Write-Host ""
Write-Host "Done. ONNX files at $OutputDirectory" -ForegroundColor Green
if ($Fp16) {
    Write-Host "Mixed-precision export: UNet + text encoders (FP16), VAE decoder (FP32)." -ForegroundColor DarkGray
}
Write-Host "Verify with: Get-ChildItem -Recurse $OutputDirectory" -ForegroundColor DarkGray
