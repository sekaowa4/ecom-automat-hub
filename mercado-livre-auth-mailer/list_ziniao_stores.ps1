$ErrorActionPreference = 'Stop'
$cli = 'D:\application\ziniao\resources\app.asar.unpacked\envkit\cli\win32\x64\ziniao.exe'
if (-not (Test-Path -LiteralPath $cli)) { throw "ZiNiao CLI not found: $cli" }
& $cli browser list --all --json
exit $LASTEXITCODE
