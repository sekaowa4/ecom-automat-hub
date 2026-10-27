$ErrorActionPreference='Stop'
$source=[IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../server/Jobs/VerificationPrompt.ps1'))
foreach($choice in @('continue','later','close','automatic')) {
 $click=switch($choice){'continue'{'$continue.PerformClick()'} 'later'{'$later.PerformClick()'} 'close'{'$form.Close()'} 'automatic'{'if ($continue.Visible -or !$heading.Text.Contains("检测到店铺验证")) { throw "自动验证窗口不正确" }; $later.PerformClick()'}}
 $probe=@'
if (!$form.TopMost -or !$form.ShowInTaskbar -or $form.Width -lt 590 -or $continue.Text -ne '我已验证，继续调价') { throw '弹窗配置不正确' }
$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = 300
$timer.Add_Tick({ $timer.Stop(); CLICK })
$form.Add_Shown({ $timer.Start() })
'@
 $script=$source.Replace('$result = $form.ShowDialog()',($probe.Replace('CLICK',$click)+"`n"+'$result = $form.ShowDialog()'))
 $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
 $env:REPRICING_VERIFY_STORE='弹窗界面验收（不连接店铺）'
 $env:REPRICING_VERIFY_SKU='TEST-ONLY'
 $env:REPRICING_VERIFY_AUTOMATIC=if($choice -eq 'automatic'){'1'}else{'0'}
 $process=Start-Process -FilePath "$env:WINDIR/System32/WindowsPowerShell/v1.0/powershell.exe" -ArgumentList @('-NoProfile','-STA','-EncodedCommand',$encoded) -WindowStyle Hidden -PassThru
 if(!$process.WaitForExit(20000)){$process.Kill();throw '弹窗验收超时'}
 $expected=if($choice -eq 'continue'){0}else{2}
 if($process.ExitCode -ne $expected){throw "弹窗动作 $choice 返回错误 $($process.ExitCode)"}
 Write-Output "Windows 独立弹窗：$choice 通过"
}
