param(
    [string]$Providers = "Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime:0x14C14FCCBD:5",
    [string]$Profile = "gc-collect",
    [string]$Source = "E:\Datasets\Chicago Crimes Dataset\Crimes_-_2001_to_Present_20260331.csv",
    [string]$Dest = "",
    [string]$Duration = "00:00:30",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$timestamp = Get-Date -Format "yyyy-MM-dd-HH-mm"
$outPath = "traces\$timestamp.nettrace"

if (-not (Test-Path "traces")) {
    New-Item -ItemType Directory -Path "traces" | Out-Null
}

# Disable ReadyToRun so the SampleProfiler can resolve BCL method names.
$env:DOTNET_ReadyToRun = "0"

# Derive a default destination path alongside the source if not provided.
if ([string]::IsNullOrWhiteSpace($Dest)) {
    $Dest = [System.IO.Path]::ChangeExtension($Source, ".datum")
}

# Build the ingest harness first. dotnet-trace attaches reliably to a built
# executable but NOT to `dotnet run` — `dotnet run` spawns the real app in a
# child process that the trace never sees.
$project = "tools\IngestOnce\IngestOnce.csproj"
Write-Host "Building $project ($Configuration)..."
& dotnet build $project -c $Configuration --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

# Resolve the produced exe. The AssemblyName is 'ingest-once'.
$exe = "tools\IngestOnce\bin\$Configuration\net10.0\ingest-once.exe"
if (-not (Test-Path $exe)) {
    throw "Built exe not found at $exe"
}

Write-Host ""
Write-Host "Tracing ingestion -> $outPath"
Write-Host "  Source: $Source"
Write-Host "  Dest:   $Dest"
Write-Host "  Profile: $Profile (duration $Duration)"
Write-Host "  Exe:    $exe"
Write-Host ""

# Trace the built exe directly. `--` separates dotnet-trace args from the launched
# process's args. dotnet-trace sets the eventpipe env var on THIS process so all
# events from ingest-once.exe and the Ingester code show up in the trace.
& dotnet-trace collect `
    --profile $Profile `
    --providers $Providers `
    --duration $Duration `
    -o $outPath `
    -- $exe $Source $Dest
