$taskRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $taskRoot '.tools\home'
$env:NUGET_PACKAGES = Join-Path $taskRoot '.tools\packages'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $taskRoot '.tools\nuget-http'
$env:NUGET_SCRATCH = Join-Path $taskRoot '.tools\nuget-scratch'
$env:NUGET_CERT_REVOCATION_MODE = 'offline'
$env:APPDATA = Join-Path $taskRoot '.tools\appdata'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$taskDotnet = Join-Path $taskRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = 'dotnet' }
& $taskDotnet @args
exit $LASTEXITCODE
