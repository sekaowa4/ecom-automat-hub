param([string]$PackageSource='https://api.nuget.org/v3/index.json')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $taskRoot
& "$PSScriptRoot\check.ps1"
if($LASTEXITCODE){throw '回归检查未通过，禁止发布'}
& "$PSScriptRoot\dotnet.ps1" restore server/Repricing.Api.csproj -r win-x64 --source $PackageSource '-p:NuGetAudit=false' --verbosity quiet
if($LASTEXITCODE){throw '发布依赖恢复失败'}
$taskDist=Join-Path $taskRoot 'dist\MercadoRepricing-0.5.33'
$taskAssetDir=Join-Path $taskDist 'wwwroot\assets'
if(Test-Path -LiteralPath $taskAssetDir){
    $taskExpectedAssets=@(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'server\wwwroot\assets') -File | ForEach-Object {$_.Name})
    $taskAssetPrefix=[IO.Path]::GetFullPath($taskAssetDir)+[IO.Path]::DirectorySeparatorChar
    Get-ChildItem -LiteralPath $taskAssetDir -File | ForEach-Object {
        $taskAssetPath=[IO.Path]::GetFullPath($_.FullName)
        if(!$taskAssetPath.StartsWith($taskAssetPrefix,[StringComparison]::OrdinalIgnoreCase)){throw '资源清理路径越界'}
        $taskSourceName=$_.Name -replace '\.(br|gz)$',''
        if($taskSourceName -notin $taskExpectedAssets){Remove-Item -LiteralPath $taskAssetPath -Force}
    }
}
& "$PSScriptRoot\dotnet.ps1" publish server/Repricing.Api.csproj -c Release -r win-x64 --self-contained true --no-restore -o $taskDist --verbosity quiet '-p:DebugType=None' '-p:DebugSymbols=false'
if($LASTEXITCODE){throw '程序发布失败'}
Copy-Item -LiteralPath (Join-Path $taskRoot 'samples') -Destination $taskDist -Recurse -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\同事使用说明.txt') -Destination $taskDist -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\混合调价使用说明.md') -Destination $taskDist -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\标签剪裁使用说明.md') -Destination $taskDist -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\箱唛裁切与标签预览.md') -Destination $taskDist -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\周末活动报名与验证日志.md') -Destination $taskDist -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\数据看板使用说明.md') -Destination $taskDist -Force
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs\调价速度优化说明.md') -Destination $taskDist -Force
$taskLabelEngine=Join-Path $taskDist 'tools\labels\PDFLabelCropper.exe'
if(!(Test-Path -LiteralPath $taskLabelEngine) -or (Get-FileHash -LiteralPath $taskLabelEngine -Algorithm SHA256).Hash -ne 'DA8E7B7A324F8F1C7D0669738BE52605B86AB87002C155F2C56FAA8EE58BA5DD'){throw '标签剪裁组件缺失或与已验收版本不符'}
$taskCartonEngine=Join-Path $taskDist 'tools\labels\CartonLabelCropper.exe'
if(!(Test-Path -LiteralPath $taskCartonEngine) -or (Get-FileHash -LiteralPath $taskCartonEngine -Algorithm SHA256).Hash -ne '3BAAA54063F7B70EE5E71A2CF21322235C1BB9DEC362A193BFBD6CADF525F3F1'){throw '箱唛剪裁组件缺失或与已验收版本不符'}
$taskOldExperimentCopy=Join-Path $taskDist '降价实测复盘与固定流程.md'
if(Test-Path -LiteralPath $taskOldExperimentCopy){Remove-Item -LiteralPath $taskOldExperimentCopy -Force}
$taskFlow=[IO.File]::ReadAllText((Join-Path $taskRoot 'docs\降价实测复盘与固定流程.md'))
$taskExperimentAt=$taskFlow.IndexOf('## 三个 SKU')
$taskTabsAt=$taskFlow.IndexOf('## 标签数量与执行边界')
if($taskExperimentAt -lt 0 -or $taskTabsAt -lt 0){throw '流程说明分段不存在，禁止打包个人实验记录'}
[IO.File]::WriteAllText((Join-Path $taskDist '降价流程说明.md'),$taskFlow.Substring(0,$taskExperimentAt)+$taskFlow.Substring($taskTabsAt),[Text.Encoding]::UTF8)
$taskLaunch="@echo off`r`nstart `"`" `"%~dp0Repricing.Api.exe`"`r`n"
[IO.File]::WriteAllText((Join-Path $taskDist '启动美客多工作台.cmd'),$taskLaunch,[Text.Encoding]::ASCII)
$taskUnexpected=Get-ChildItem -LiteralPath $taskDist -Recurse -File | Where-Object {$_.Extension -in '.db','.db-wal','.pdb' -or $_.Name -in 'tasks.db','startup.log','secrets.json','credentials.json','.env' -or ($_.Extension -ne '.dll' -and $_.Name -match 'apikey|credential|secret')}
if($taskUnexpected){throw '分发目录出现不应打包的个人或调试数据，请检查 dist。'}
$taskZip=Join-Path $taskRoot 'dist\美客多工作台-0.5.33-win-x64.zip'
Compress-Archive -LiteralPath $taskDist -DestinationPath $taskZip -Force
Write-Output "发布完成：$taskZip"
