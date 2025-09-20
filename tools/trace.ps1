param(
    [string]$Providers = "Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime:0x14C14FCCBD:5",
    [string]$Profile = "gc-collect"
)

$timestamp = Get-Date -Format "yyyy-MM-dd-HH-mm"
$outPath = "traces\$timestamp.nettrace"

# Disable ReadyToRun so the SampleProfiler can resolve BCL method names.
$env:DOTNET_ReadyToRun = "0"

$exe = "src\DatumIngest.Cli\bin\Release\net10.0\win-x64\DatumIngest.Cli.exe"
$sql = 'SELECT COUNT(*) AS row_count, COUNT(DISTINCT \"ID\") AS distinct_id_count, MIN(\"Date\") AS min_date, MAX(\"Date\") AS max_date, COUNT(*) - COUNT(\"Beat\") AS null_beat_rows, COUNT(*) - COUNT(\"Date\") AS null_date_rows FROM \"Crimes_-_2001_to_Present_20260331_csv\"'
$queryArgs = @('query', $sql, '--source', "C:\Users\Albert\AppData\Local\heliosoph-compute\compute-3\datasets\019d831c-885b-7ca9-a74f-356369395455")

Write-Host "Tracing -> $outPath (30s capture)"
& dotnet-trace collect --profile $Profile --providers $Providers --duration 00:00:30 -o $outPath -- $exe @queryArgs
