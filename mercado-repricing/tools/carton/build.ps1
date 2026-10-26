param([string]$Python='python')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path $PSScriptRoot -Parent
$taskRoot=Split-Path $taskRoot -Parent
Set-Location -LiteralPath $taskRoot
$taskVenv=Join-Path $taskRoot '.tools/carton-build'
if(!(Test-Path -LiteralPath "$taskVenv/Scripts/python.exe")){& $Python -m venv $taskVenv;if($LASTEXITCODE){throw '创建构建环境失败'}}
& "$taskVenv/Scripts/python.exe" -m pip install -r tools/carton/requirements-build.txt
if($LASTEXITCODE){throw '依赖安装失败'}
& "$taskVenv/Scripts/python.exe" tests/fixtures/test_carton.py
if($LASTEXITCODE){throw '箱唛规则测试失败'}
& "$taskVenv/Scripts/python.exe" -m PyInstaller --noconfirm --onefile --name CartonLabelCropper --distpath server/tools/labels --workpath .tools/carton-pyinstaller --specpath .tools/carton-pyinstaller tools/carton/carton_crop.py
if($LASTEXITCODE){throw '打包箱唛组件失败'}
Get-FileHash -LiteralPath server/tools/labels/CartonLabelCropper.exe -Algorithm SHA256
Write-Output '重新验收后同步 scripts/publish.ps1 中的组件哈希。第三方许可保存在 server/tools/labels/carton-licenses。'
