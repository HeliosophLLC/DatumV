# Generic ONNX exporter for cszn/KAIR models.
#
# KAIR (github.com/cszn/KAIR, Apache-2.0) is Kai Zhang's umbrella repo
# consolidating most of his image-restoration models. Each model lives at
# models/network_<name>.py; the matching main_test_<name>.py inference
# script documents the constructor args used for each published checkpoint.
#
# This script provides:
#   1. A built-in registry of well-known single-input image-to-image
#      KAIR models (-Model <name>). These are the easy cases where the
#      forward pass is image -> image with no auxiliary inputs.
#   2. A JSON-config escape hatch (-ConfigJsonPath) for any other KAIR
#      model whose architecture isn't in the built-in registry.
#
# What this does NOT support (yet):
#   - Multi-input models (FFDNet, DPIR take a noise-map auxiliary input;
#     USRNet takes kernel + noise + scale factor). Possible but each one
#     needs custom dummy-input construction; use the JSON escape hatch
#     and supply an inputBuilder Python expression if you must.
#
# Usage:
#   # Built-in: convert a known KAIR model from a .pth on disk
#   ./scripts/export-kair.ps1 -Model dncnn -CheckpointPath C:\downloads\dncnn_color_25.pth
#   ./scripts/export-kair.ps1 -Model bsrgan -CheckpointPath C:\downloads\BSRGAN.pth
#
#   # JSON escape hatch: convert any KAIR model with a custom config
#   ./scripts/export-kair.ps1 -ConfigJsonPath .\my-model.json -CheckpointPath C:\downloads\foo.pth
#
#   # Download-by-URL alternative to -CheckpointPath
#   ./scripts/export-kair.ps1 -Model dncnn -CheckpointUrl https://example.com/dncnn.pth
#
# Output: $env:DATUMV_MODELS/kair-staging/<basename>.onnx (where basename
# is the .pth filename minus extension). Override with -OutputFilename.

[CmdletBinding(DefaultParameterSetName = 'BuiltIn')]
param(
    # Built-in model name. See $KnownModels below for the registry.
    [Parameter(ParameterSetName = 'BuiltIn', Mandatory = $true)]
    [ValidateSet('dncnn', 'bsrgan', 'scunet-color', 'scunet-gray')]
    [string]$Model,

    # Path to a JSON config file describing a custom KAIR model.
    # See "JSON config schema" below for the expected shape.
    [Parameter(ParameterSetName = 'Custom', Mandatory = $true)]
    [string]$ConfigJsonPath,

    # Local path to the .pth checkpoint. Use this when you've downloaded
    # the file manually (e.g. from Google Drive).
    [Parameter()]
    [string]$CheckpointPath,

    # URL to download the .pth from. Used only when -CheckpointPath isn't
    # set. One of -CheckpointPath or -CheckpointUrl must be provided.
    [Parameter()]
    [string]$CheckpointUrl,

    # Output directory. Defaults to $env:DATUMV_MODELS/kair-staging.
    [Parameter()]
    [string]$OutputDirectory = $(
        if ($env:DATUMV_MODELS) {
            Join-Path $env:DATUMV_MODELS 'kair-staging'
        } else {
            throw 'Set $env:DATUMV_MODELS or pass -OutputDirectory <path>.'
        }
    ),

    # Output ONNX filename. Defaults to "<pth basename>.onnx" so the
    # output tracks the input filename naturally.
    [Parameter()]
    [string]$OutputFilename,

    # Local path to a KAIR clone (or any repo with the matching
    # models/network_*.py layout — e.g. the standalone SCUNet repo).
    # When set, skips the KAIR clone step. Use this when you already
    # have the source on disk.
    [Parameter()]
    [string]$SourcePath
)

$ErrorActionPreference = 'Stop'

# Built-in registry. Each entry is a recipe matching one published
# checkpoint family from KAIR's main_test_<name>.py inference scripts.
# Constructor kwargs are copied verbatim from those scripts — bumping a
# value here without bumping the matching script in KAIR will produce a
# state_dict load failure.
# Hashtable keys are camelCase to match the canonical JSON config schema
# (see comment block below) — so the Python side, which deserializes the
# recipe from a JSON env-var, can use the same keys regardless of whether
# the recipe came from this registry or from a user-supplied .json file.
$KnownModels = @{
    'dncnn' = @{
        # Classical CNN denoiser (Zhang et al., 2017). Color denoising
        # variants ship as dncnn_color_{15,25,50}.pth — same architecture,
        # different training noise level.
        moduleFile  = 'models/network_dncnn.py'
        className   = 'DnCNN'
        kwargs      = @{ in_nc = 3; out_nc = 3; nc = 64; nb = 20; act_mode = 'R' }
        inputShape  = @(1, 3, 256, 256)
        inputName   = 'image'
        outputName  = 'denoised'
        dynamicH_W  = $true
        description = 'DnCNN: classical CNN denoiser. RGB float32 [0,1] in/out. Dynamic H/W.'
    }
    'bsrgan' = @{
        # Blind real-world 4x super-resolution (Zhang et al., 2021).
        # Uses the RRDBNet backbone (ESRGAN architecture).
        moduleFile  = 'models/network_rrdbnet.py'
        className   = 'RRDBNet'
        kwargs      = @{ in_nc = 3; out_nc = 3; nf = 64; nb = 23; gc = 32; sf = 4 }
        inputShape  = @(1, 3, 256, 256)
        inputName   = 'image'
        outputName  = 'upscaled'
        dynamicH_W  = $true
        description = 'BSRGAN: blind real-world 4x super-resolution. RGB float32 [0,1] in/out. Dynamic H/W.'
    }
    'scunet-color' = @{
        # Swin-Conv-UNet, color variants. Covers scunet_color_{15,25,50}
        # (Gaussian) AND scunet_color_real_{psnr,gan} (blind real-world)
        # — same architecture, different training data / loss.
        # Confirmed kwargs from main_test_scunet_color_gaussian.py and
        # main_test_scunet_real_application.py.
        moduleFile  = 'models/network_scunet.py'
        className   = 'SCUNet'
        kwargs      = @{ in_nc = 3; config = @(4,4,4,4,4,4,4); dim = 64 }
        inputShape  = @(1, 3, 256, 256)
        inputName   = 'image'
        outputName  = 'denoised'
        dynamicH_W  = $true
        description = 'SCUNet (color): blind/Gaussian color denoising. RGB float32 [0,1] in/out. H/W must be /8.'
    }
    'scunet-gray' = @{
        # SCUNet grayscale variants (scunet_gray_{15,25,50}).
        # Single difference vs color: in_nc=1.
        # Confirmed kwargs from main_test_scunet_gray_gaussian.py.
        moduleFile  = 'models/network_scunet.py'
        className   = 'SCUNet'
        kwargs      = @{ in_nc = 1; config = @(4,4,4,4,4,4,4); dim = 64 }
        inputShape  = @(1, 1, 256, 256)
        inputName   = 'image'
        outputName  = 'denoised'
        dynamicH_W  = $true
        description = 'SCUNet (gray): Gaussian grayscale denoising. Y float32 [0,1] in/out. H/W must be /8.'
    }
}
# JSON config schema (for -ConfigJsonPath):
#   {
#     "moduleFile":  "models/network_<name>.py",   # KAIR-relative
#     "className":   "ClassName",
#     "kwargs":      { "key": value, ... },        # passed to ClassName(**kwargs)
#     "inputShape":  [1, 3, 256, 256],             # NCHW for dummy input
#     "inputName":   "image",
#     "outputName":  "denoised",
#     "dynamicH_W":  true                          # set false to pin spatial dims
#   }

# Resolve the recipe (built-in or JSON).
if ($PSCmdlet.ParameterSetName -eq 'BuiltIn') {
    $recipe = $KnownModels[$Model]
    Write-Host "Recipe: $($recipe.description)" -ForegroundColor Cyan
} else {
    if (-not (Test-Path $ConfigJsonPath)) {
        throw "ConfigJsonPath '$ConfigJsonPath' does not exist."
    }
    $recipe = Get-Content $ConfigJsonPath -Raw | ConvertFrom-Json -AsHashtable
    Write-Host "Recipe: custom config from $ConfigJsonPath" -ForegroundColor Cyan
}

# Resolve the checkpoint.
$repoRoot   = Resolve-Path "$PSScriptRoot\.."
$venvPython = Join-Path $repoRoot '.venv\Scripts\python.exe'
$sourceDir  = Join-Path $repoRoot '.cache\kair-source'
$weightsDir = Join-Path $repoRoot '.cache\kair-weights'

New-Item -ItemType Directory -Force -Path $weightsDir | Out-Null

if ($CheckpointPath) {
    if (-not (Test-Path $CheckpointPath)) {
        throw "CheckpointPath '$CheckpointPath' does not exist."
    }
    $ckptBasename = [IO.Path]::GetFileName($CheckpointPath)
    $ckptDest     = Join-Path $weightsDir $ckptBasename
    if (-not (Test-Path $ckptDest) -or
        (Get-Item $CheckpointPath).LastWriteTime -gt (Get-Item $ckptDest).LastWriteTime) {
        Write-Host "Caching checkpoint -> $ckptDest" -ForegroundColor Cyan
        Copy-Item -Path $CheckpointPath -Destination $ckptDest -Force
    } else {
        Write-Host "Checkpoint already cached at $ckptDest" -ForegroundColor DarkGray
    }
} elseif ($CheckpointUrl) {
    $ckptBasename = [IO.Path]::GetFileName(([Uri]$CheckpointUrl).LocalPath)
    if ([string]::IsNullOrWhiteSpace($ckptBasename)) {
        throw "Could not derive a filename from CheckpointUrl '$CheckpointUrl'. Pass -CheckpointPath instead."
    }
    $ckptDest = Join-Path $weightsDir $ckptBasename
    if (-not (Test-Path $ckptDest)) {
        Write-Host "Downloading $CheckpointUrl ..." -ForegroundColor Cyan
        try {
            Invoke-WebRequest -Uri $CheckpointUrl -OutFile $ckptDest
        } catch {
            throw "Failed to download checkpoint: $($_.Exception.Message)"
        }
    } else {
        Write-Host "$ckptBasename already cached." -ForegroundColor DarkGray
    }
} else {
    throw "One of -CheckpointPath or -CheckpointUrl is required."
}

# Default output filename = <pth basename without ext>.onnx
if (-not $OutputFilename) {
    $OutputFilename = [IO.Path]::GetFileNameWithoutExtension($ckptBasename) + '.onnx'
}

# 1. Shared project venv.
if (-not (Test-Path $venvPython)) {
    Write-Host 'Creating Python 3.10 virtual environment at .venv\ ...' -ForegroundColor Cyan
    py -3.10 -m venv (Join-Path $repoRoot '.venv')
} else {
    Write-Host '.venv exists, reusing.' -ForegroundColor DarkGray
}
& (Join-Path $repoRoot '.venv\Scripts\Activate.ps1')

# 2. Dependencies. KAIR's model classes pull from a small set:
#    torch + numpy + onnx are universal; timm + einops are needed by the
#    Swin-based models (SCUNet uses both); thop is imported at module
#    load time by network_scunet.py for parameter counting (we don't
#    actually use it but the import would fail without it).
Write-Host 'Ensuring torch / timm / einops / thop / onnx are installed ...' -ForegroundColor Cyan
pip install --quiet 'torch<2.5' --index-url https://download.pytorch.org/whl/cu124
pip install --quiet timm einops thop numpy onnx 'onnxruntime>=1.17'

# 3. Resolve source root. If the user passed -SourcePath, use it as-is;
#    otherwise clone KAIR (shallow — we only need network_*.py files).
if ($SourcePath) {
    if (-not (Test-Path (Join-Path $SourcePath $recipe['moduleFile']))) {
        throw "SourcePath '$SourcePath' does not contain '$($recipe['moduleFile'])'. " +
              "Pass the repo root that holds models/network_<name>.py at that relative path."
    }
    $sourceDir = (Resolve-Path $SourcePath).Path
    Write-Host "Using local source at $sourceDir" -ForegroundColor DarkGray
} elseif (-not (Test-Path (Join-Path $sourceDir 'models'))) {
    Write-Host "Cloning cszn/KAIR to $sourceDir ..." -ForegroundColor Cyan
    git clone --depth 1 https://github.com/cszn/KAIR.git $sourceDir
} else {
    Write-Host 'KAIR source already cached.' -ForegroundColor DarkGray
}

# 4. Stage output.
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$outPath = Join-Path $OutputDirectory $OutputFilename

# 5. Marshal the recipe to JSON for the Python side. Keeping the recipe
#    in one canonical form means the same Python template handles both
#    built-in and custom paths — no branching in the Python script.
$recipeJson = $recipe | ConvertTo-Json -Depth 10 -Compress

$env:KAIR_SOURCE      = $sourceDir
$env:KAIR_RECIPE_JSON = $recipeJson
$env:KAIR_CKPT_PATH   = $ckptDest
$env:KAIR_OUTPUT_PATH = $outPath

# 6. Python export. Reads the recipe JSON, imports the class, instantiates,
#    loads the .pth, exports to ONNX.
$exportScript = @'
import importlib.util
import inspect
import json
import os
import sys
import torch

# Force the legacy TorchScript-based ONNX exporter in torch >=2.5 where
# dynamo became the default. The dynamo path can't always reconcile
# tensor-name lineage through dtype-coercion ops like .type_as(), which
# several KAIR networks use for normalization buffers — and raises
# misleading errors like "Key 'c_mean' does not match value 'type_as'".
# The legacy exporter handles these cleanly. On older torch (2.4 and
# below) the `dynamo` kwarg doesn't exist, so feature-detect.
_extra_export_kwargs = {}
if 'dynamo' in inspect.signature(torch.onnx.export).parameters:
    _extra_export_kwargs['dynamo'] = False

src       = os.environ['KAIR_SOURCE']
recipe    = json.loads(os.environ['KAIR_RECIPE_JSON'])
ckpt_path = os.environ['KAIR_CKPT_PATH']
out_path  = os.environ['KAIR_OUTPUT_PATH']

# Import the model class from KAIR's models/ subtree. Using importlib
# rather than sys.path manipulation so module collisions across KAIR
# subpackages (they re-export many names) can't bite us.
module_file = os.path.join(src, recipe['moduleFile'])
if not os.path.exists(module_file):
    print(f"ERROR: model module not found at {module_file}", file=sys.stderr)
    print(f"  KAIR clone is at: {src}", file=sys.stderr)
    print(f"  recipe.moduleFile: {recipe['moduleFile']}", file=sys.stderr)
    sys.exit(2)

# KAIR's internal cross-imports (e.g. utils_image) assume the repo root
# is on sys.path. Add it before the dynamic import.
sys.path.insert(0, src)

spec = importlib.util.spec_from_file_location('kair_target_model', module_file)
mod  = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)

cls = getattr(mod, recipe['className'])
print(f"Instantiating {recipe['className']}(**{recipe['kwargs']}) ...")
model = cls(**recipe['kwargs'])

# Load weights. Try common state_dict wrappers in order; KAIR checkpoints
# are usually flat but some pass through outer training scripts that wrap.
print(f"Loading checkpoint {ckpt_path} ...")
state = torch.load(ckpt_path, map_location='cpu')
if isinstance(state, dict):
    for key in ('params_ema', 'params', 'state_dict', 'model'):
        if key in state and isinstance(state[key], dict):
            print(f"  unwrapping outer key '{key}'")
            state = state[key]
            break

try:
    model.load_state_dict(state, strict=True)
except RuntimeError as ex:
    # strict=True failures usually mean kwargs don't match the checkpoint
    # (wrong channel count, wrong depth). Re-raise with a hint.
    msg = str(ex)
    raise RuntimeError(
        f"state_dict load failed (strict=True). This usually means the "
        f"recipe's constructor kwargs don't match the checkpoint's "
        f"architecture. Check main_test_{recipe['className'].lower()}.py "
        f"in the KAIR clone at {src} for the canonical kwargs.\n\n{msg}"
    )
model.eval()

# Build dummy input from the recipe's shape.
dummy = torch.randn(*recipe['inputShape'])

# Dynamic-axes: by convention, axis 0 is always batch. If dynamicH_W is
# set, axes 2 and 3 (height and width for NCHW) are also dynamic.
axes_in  = {0: 'batch'}
axes_out = {0: 'batch'}
if recipe.get('dynamicH_W', False):
    axes_in.update({2: 'height', 3: 'width'})
    axes_out.update({2: 'height', 3: 'width'})

print(f"Exporting -> {out_path}")
torch.onnx.export(
    model,
    dummy,
    out_path,
    input_names=[recipe['inputName']],
    output_names=[recipe['outputName']],
    dynamic_axes={
        recipe['inputName']:  axes_in,
        recipe['outputName']: axes_out,
    },
    **_extra_export_kwargs,
    opset_version=17,
    do_constant_folding=True,
)
print('Done.')
'@

$tmpPy = Join-Path $env:TEMP "kair-export-$(Get-Random).py"
Set-Content -Path $tmpPy -Value $exportScript -Encoding UTF8

try {
    Write-Host "Running ONNX export -> $outPath ..." -ForegroundColor Cyan
    & $venvPython $tmpPy
    if ($LASTEXITCODE -ne 0) {
        throw "ONNX export failed with exit code $LASTEXITCODE - check output above for traceback."
    }
} finally {
    Remove-Item -Path $tmpPy -ErrorAction SilentlyContinue
    deactivate
}

Write-Host ""
Write-Host "Done. ONNX file at $outPath" -ForegroundColor Green
Write-Host "Verify with: Get-ChildItem $OutputDirectory" -ForegroundColor DarkGray
