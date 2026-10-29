$taskRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $taskRoot
$ErrorActionPreference='Stop'
& python (Join-Path $taskRoot 'tests\test_dashboard_package.py')
if($LASTEXITCODE){exit $LASTEXITCODE}
& "$PSScriptRoot\dotnet.ps1" test tests/Repricing.Tests/Repricing.Tests.csproj --no-restore --logger 'trx;LogFileName=regression.trx' --verbosity quiet
if($LASTEXITCODE){exit $LASTEXITCODE}
& npm.cmd --prefix web run test -- --run --configLoader runner
if($LASTEXITCODE){exit $LASTEXITCODE}
& npm.cmd --prefix web run build -- --configLoader runner
if($LASTEXITCODE){exit $LASTEXITCODE}
$taskManifest=Get-Content -LiteralPath (Join-Path $taskRoot 'tests\regression-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if(!$taskManifest.browser){throw 'Browser regression manifest is empty'}
$taskScripts=@(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'tests\browser') -Filter '*.cjs' -File | Select-Object -ExpandProperty Name)
if(Compare-Object $taskScripts @($taskManifest.browser.script)){throw 'Browser regression manifest does not match the test files'}
foreach($taskCase in $taskManifest.browser){
    if($taskCase.script -notmatch '^[a-z0-9-]+\.cjs$'){throw 'Invalid browser regression filename'}
    & node (Join-Path $taskRoot ('tests\browser\'+$taskCase.script))
    if($LASTEXITCODE){exit $LASTEXITCODE}
}
Write-Output ('Regression suite passed: '+$taskManifest.browser.Count+' browser scenarios and all backend/frontend tests.')
