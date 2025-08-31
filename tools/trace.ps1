param(
    [string]$Providers = "Microsoft-DotNETCore-SampleProfiler,Microsoft-Windows-DotNETRuntime:0x14C14FCCBD:5",
    [string]$Profile = "gc-collect"
)

$timestamp = Get-Date -Format "yyyy-MM-dd-HH-mm"
$outPath = "traces\$timestamp.nettrace"

$exe = "src\DatumIngest.Cli\bin\Release\net10.0\win-x64\DatumIngest.Cli.exe"
$query = 'query "SELECT orders_csv.user_id, order_products__prior_csv.product_id, COUNT(*) FROM orders_csv LEFT JOIN order_products__prior_csv ON orders_csv.order_id = order_products__prior_csv.order_id GROUP BY orders_csv.user_id, order_products__prior_csv.product_id LIMIT 100" --source "E:\Datasets\019d5610-9245-70c1-9fbf-038e7addb609"'

$proc = Start-Process -FilePath $exe -ArgumentList $query -PassThru
Write-Host "Tracing PID $($proc.Id) -> $outPath"
dotnet-trace collect -p $proc.Id --profile $Profile --providers $Providers -o $outPath
