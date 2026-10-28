$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskVersion=([xml][IO.File]::ReadAllText((Join-Path $taskRoot 'server\Repricing.Api.csproj'))).Project.PropertyGroup.Version
$taskExe=Join-Path $taskRoot 'dist\MercadoRepricing-$taskVersion\Repricing.Api.exe'
if(!(Test-Path -LiteralPath $taskExe)){throw '请先运行 scripts/publish.ps1 生成程序包。'}
Start-Process -FilePath $taskExe -WorkingDirectory (Split-Path $taskExe -Parent) -WindowStyle Hidden
