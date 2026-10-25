$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location -LiteralPath $projectDir
& (Join-Path $projectDir '.venv\Scripts\python.exe') -u tools\collect_agent_six_codes.py
exit $LASTEXITCODE
