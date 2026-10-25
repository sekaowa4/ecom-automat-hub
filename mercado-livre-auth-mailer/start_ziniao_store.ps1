param(
    [Parameter(Mandatory = $true)]
    [string]$StoreNameContains
)

$ErrorActionPreference = 'Stop'
$cli = 'D:\application\ziniao\resources\app.asar.unpacked\envkit\cli\win32\x64\ziniao.exe'
if (-not (Test-Path -LiteralPath $cli)) { throw "ZiNiao CLI not found: $cli" }

$lines = @(& $cli browser list --all --json)
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$final = $lines | Where-Object { $_ -match '"ok"' } | Select-Object -Last 1 | ConvertFrom-Json
if (-not $final.ok) { throw $final.error }
$matches = @($final.data.items | Where-Object { $_.storeName -like "*$StoreNameContains*" })
if ($matches.Count -ne 1) { throw "Expected exactly one matching store; found $($matches.Count)" }

& $cli browser start --id $matches[0].storeId --mode agent --json
exit $LASTEXITCODE
