param(
    [ValidateSet("IngestOnce", "ReadCsv")]
    [string]$Tool = "IngestOnce",
    [string]$Providers = "Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime:0x14C14FCCBD:5",
    [string]$Profile = "gc-collect",
    [string]$Source = "E:\Datasets\COCO2017\test2017.zip",
    [string]$Dest = "",
    [string]$Duration = "00:01:30",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$timestamp = Get-Date -Format "yyyy-MM-dd-HH-mm"
$outPath = "traces\$timestamp-$Tool.nettrace"

if (-not (Test-Path "traces")) {
    New-Item -ItemType Directory -Path "traces" | Out-Null
}

# Disable ReadyToRun so the SampleProfiler can resolve BCL method names.
$env:DOTNET_ReadyToRun = "0"

# Per-tool config: project path, exe name, and whether the tool accepts a dest arg.
switch ($Tool) {
    "IngestOnce" {
        $project = "tools\IngestOnce\IngestOnce.csproj"
        $exeName = "ingest-once.exe"
        $needsDest = $true
    }
    "ReadCsv" {
        $project = "tools\ReadCsv\ReadCsv.csproj"
        $exeName = "read-csv.exe"
        $needsDest = $false
    }
}

# Derive a default destination path alongside the source if IngestOnce and none provided.
if ($needsDest -and [string]::IsNullOrWhiteSpace($Dest)) {
    $Dest = [System.IO.Path]::ChangeExtension($Source, ".datum")
}

# Build the chosen harness. dotnet-trace attaches reliably to a built executable
# but NOT to `dotnet run` — that spawns the real app in a child process the trace
# never sees.
Write-Host "Building $project ($Configuration)..."
& dotnet build $project -c $Configuration --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$exeDir = Split-Path $project -Parent
$exe = "$exeDir\bin\$Configuration\net10.0\$exeName"
if (-not (Test-Path $exe)) {
    throw "Built exe not found at $exe"
}

# Build process-arg list: source always, dest only for IngestOnce.
$procArgs = @($Source)
if ($needsDest) { $procArgs += $Dest }

Write-Host ""
Write-Host "Tracing $Tool -> $outPath"
Write-Host "  Source: $Source"
if ($needsDest) { Write-Host "  Dest:   $Dest" }
Write-Host "  Profile: $Profile (duration $Duration)"
Write-Host "  Exe:    $exe"
Write-Host ""

# Trace the built exe directly. `--` separates dotnet-trace args from the launched
# process's args. dotnet-trace sets the eventpipe env var on THIS process so all
# events from the target exe show up in the trace.
& dotnet-trace collect `
    --profile $Profile `
    --providers $Providers `
    --duration $Duration `
    -o $outPath `
    -- $exe @procArgs
