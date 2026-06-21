param(
    [ValidateSet("IndexOnce", "ExecuteOnce")]
    [string]$Tool = "ExecuteOnce",
    [string]$Providers = "Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime:0x14C14FCCBD:5",
    # gc-verbose emits GCAllocationTick every ~100 KB (with type + stack) so PerfView's
    # "GC Heap Alloc Ignore Free (Coarse Sampling) Stacks" view can attribute allocations.
    # gc-collect is the lower-overhead alternative that only shows collection events.
    [string]$Profile = "gc-verbose",
    [string]$Source = "E:\Datasets\COCO2017\annotations_trainval2017\captions_train2017.json",
    [string]$Dest = "",
    [string]$Sql = "SELECT * LIMIT 5000",
    [string]$Duration = "00:20:30",
    [string]$Configuration = "Release",
    # When set, enables the per-object AllocationSampledKeyword (0x80000) so every
    # managed allocation produces an event -- much larger traces (GBs on long runs),
    # but no sampling gaps. Use after the default gc-verbose run points you at a hot
    # call site and you need finer detail to see which line is allocating.
    [switch]$DeepAlloc
)

$ErrorActionPreference = "Stop"

# When -DeepAlloc is set, override the default providers with a focused set that
# includes AllocationSampledKeyword (0x80000). 0x80001 = GC (0x1) + AllocationSampled.
# Pairs with CPU sampling so you can cross-reference hot alloc sites against hot CPU
# sites. Unset -DeepAlloc keeps the default broad-bitmask providers.
if ($DeepAlloc) {
    $Providers = "Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime:0x80001:5"
    Write-Host "DeepAlloc enabled: every managed allocation will be recorded (large trace expected)." -ForegroundColor Yellow
}

$timestamp = Get-Date -Format "yyyy-MM-dd-HH-mm"
$outPath = "traces\$timestamp-$Tool.nettrace"

if (-not (Test-Path "traces")) {
    New-Item -ItemType Directory -Path "traces" | Out-Null
}

# Disable ReadyToRun so the SampleProfiler can resolve BCL method names.
$env:DOTNET_ReadyToRun = "0"

# Per-tool config: project path, exe name, whether the tool accepts a dest arg,
# whether the tool accepts a SQL arg, and the output extension used when deriving
# a default dest path.
$needsSql = $false
switch ($Tool) {
    "IndexOnce" {
        $project = "tools\IndexOnce\IndexOnce.csproj"
        $exeName = "index-once.exe"
        $needsDest = $true
        $destExt = ".datum-index"
    }
    "ExecuteOnce" {
        $project = "tools\ExecuteOnce\ExecuteOnce.csproj"
        $exeName = "execute-once.exe"
        $needsDest = $false
        $destExt = ""
        $needsSql = $true
    }
}

# Derive a default destination path alongside the source if the tool needs one.
if ($needsDest -and [string]::IsNullOrWhiteSpace($Dest)) {
    $Dest = [System.IO.Path]::ChangeExtension($Source, $destExt)
}

# Build the chosen harness. dotnet-trace attaches reliably to a built executable
# but NOT to `dotnet run` -- that spawns the real app in a child process the trace
# never sees.
Write-Host "Building $project ($Configuration)..."
& dotnet build $project -c $Configuration --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$exeDir = Split-Path $project -Parent
$exe = "$exeDir\bin\$Configuration\net10.0\$exeName"
if (-not (Test-Path $exe)) {
    throw "Built exe not found at $exe"
}

# Build process-arg list: source always, plus dest or SQL depending on the tool.
# SQL is routed through a temp file with `--sql-file` rather than passed as a
# positional argument. Passing SQL inline loses embedded double quotes (used for
# escaping identifiers like "Primary Type") somewhere in the VS-Code -> PowerShell
# -> dotnet-trace argument chain; the file path is a plain string that survives.
$procArgs = @($Source)
if ($needsDest) { $procArgs += $Dest }

$sqlFile = $null
if ($needsSql) {
    # Save the SQL alongside the trace file with a matching stem. Keeping the exact
    # SQL that was passed to the exe makes post-hoc verification trivial -- no temp-
    # file scavenging, and the file sticks around so you can diff it against what
    # you typed at the prompt.
    $sqlFile = [System.IO.Path]::ChangeExtension($outPath, ".sql")
    [System.IO.File]::WriteAllText($sqlFile, $Sql, [System.Text.UTF8Encoding]::new($false))
    $procArgs += @("--sql-file", $sqlFile)
    Write-Host "SQL written to: $sqlFile"
}

Write-Host ""
Write-Host "Tracing $Tool -> $outPath"
Write-Host "  Source: $Source"
if ($needsDest) { Write-Host "  Dest:   $Dest" }
if ($needsSql)  { Write-Host "  SQL:    $Sql" }
Write-Host "  Profile: $Profile (duration $Duration)"
Write-Host "  Exe:    $exe"
Write-Host ""

# Echo each arg on its own line so space-containing strings (like SQL) are visible
# as a single token rather than silently split by the shell.
Write-Host "Args passed to exe (one per line):"
foreach ($a in $procArgs) {
    Write-Host "  [$a]"
}
Write-Host ""
Write-Host "Manual-repro command (paste into a PowerShell prompt):"
$quotedArgs = ($procArgs | ForEach-Object { "`"$_`"" }) -join " "
Write-Host "  & `"$exe`" $quotedArgs"
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

$traceExit = $LASTEXITCODE
Write-Host ""
if ($traceExit -ne 0) {
    Write-Host ("=" * 70) -ForegroundColor Red
    Write-Host "dotnet-trace exited with code $traceExit." -ForegroundColor Red
    Write-Host "Common causes:"
    Write-Host "  - Target exe failed: try running the manual-repro command above in a plain"
    Write-Host "    terminal to see its stdout/stderr without trace forwarding."
    Write-Host "  - Bad args: check the 'Args passed to exe' list -- each bracketed item is one arg."
    Write-Host "    A SQL string split across multiple brackets means PowerShell lost the quotes."
    Write-Host "  - dotnet-trace not installed: run ``dotnet tool install -g dotnet-trace`` if missing."
    if ($sqlFile -and (Test-Path $sqlFile)) {
        Write-Host "  SQL file retained for inspection: $sqlFile" -ForegroundColor Yellow
    }
    Write-Host ("=" * 70) -ForegroundColor Red
    Read-Host "Press Enter to close"
    exit $traceExit
}

Write-Host "Trace written to $outPath" -ForegroundColor Green
if ($sqlFile) { Write-Host "SQL retained at: $sqlFile" -ForegroundColor Green }
