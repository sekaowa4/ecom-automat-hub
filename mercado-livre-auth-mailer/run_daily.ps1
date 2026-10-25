$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$logDir = Join-Path $projectDir 'data'
$logPath = Join-Path $logDir 'scheduled-task.log'

New-Item -ItemType Directory -Path $logDir -Force | Out-Null
Set-Location -LiteralPath $projectDir

$timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
Add-Content -LiteralPath $logPath -Value "[$timestamp] scheduled run started"

try {
    & (Join-Path $projectDir '.venv\Scripts\python.exe') `
        -m shop_auth_codes.cli `
        --config (Join-Path $projectDir 'config.toml') `
        run *>> $logPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
catch {
    Add-Content -LiteralPath $logPath -Value "ERROR: $($_.Exception.Message)"
    exit 1
}
