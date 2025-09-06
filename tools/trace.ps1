param(
    [string]$Providers = "Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime:0x14C14FCCBD:5",
    [string]$Profile = "gc-collect"
)

$timestamp = Get-Date -Format "yyyy-MM-dd-HH-mm"
$outPath = "traces\$timestamp.nettrace"

# Disable ReadyToRun so the SampleProfiler can resolve BCL method names.
$env:DOTNET_ReadyToRun = "0"

#$exe = "src\DatumIngest.Cli\bin\Release\net10.0\win-x64\DatumIngest.Cli.exe"
#$queryArgs = @('query', "SELECT orders_csv.user_id, order_products__prior_csv.product_id, COUNT(*) FROM orders_csv LEFT JOIN order_products__prior_csv ON orders_csv.order_id = order_products__prior_csv.order_id GROUP BY orders_csv.user_id, order_products__prior_csv.product_id LIMIT 100", '--source', "E:\Datasets\019d5610-9245-70c1-9fbf-038e7addb609")
#$queryArgs = @('ingest', '--source', "E:\Datasets\Instacart\order_products__prior.csv", '--output-dir', "E:\Datasets\Instacart\datum")
#$queryArgs = @('index', '--source', "E:\Datasets\Instacart\datum\orders_csv.datum", '--auto-index')

$exe = "tools\IndexFormatTrace\bin\Release\net10.0\IndexFormatTrace.exe"
$queryArgs = @("E:\Datasets\Instacart\datum\order_products__prior_csv.datum-index")

Write-Host "Tracing -> $outPath"
& dotnet-trace collect --profile $Profile --providers $Providers -o $outPath -- $exe @queryArgs
