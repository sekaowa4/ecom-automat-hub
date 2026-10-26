Add-Type -AssemblyName System.Windows.Forms,System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()
$form = New-Object System.Windows.Forms.Form
$form.Text = '美客多工作台 · 调价需要验证'
$form.ClientSize = New-Object System.Drawing.Size(600,350)
$form.StartPosition = 'CenterScreen'
$form.FormBorderStyle = 'FixedDialog'
$form.MaximizeBox = $false
$form.TopMost = $true
$form.ShowInTaskbar = $true
$form.Icon = [System.Drawing.SystemIcons]::Warning
$form.BackColor = [System.Drawing.Color]::FromArgb(255,248,225)
$form.Font = New-Object System.Drawing.Font('Microsoft YaHei UI',11)
$heading = New-Object System.Windows.Forms.Label
$heading.Text = '调价已暂停，请先完成店铺验证'
$heading.Location = New-Object System.Drawing.Point(24,24)
$heading.Size = New-Object System.Drawing.Size(552,42)
$heading.Font = New-Object System.Drawing.Font('Microsoft YaHei UI',18,[System.Drawing.FontStyle]::Bold)
$heading.ForeColor = [System.Drawing.Color]::FromArgb(170,65,0)
$detail = New-Object System.Windows.Forms.Label
$detail.Location = New-Object System.Drawing.Point(24,85)
$detail.Size = New-Object System.Drawing.Size(552,150)
$detail.Text = "店铺：$env:REPRICING_VERIFY_STORE`r`nSKU：$env:REPRICING_VERIFY_SKU`r`n`r`n请在紫鸟浏览器完成验证或重新登录。完成后点击下方继续，程序会先检查页面和实际价格。"
$continue = New-Object System.Windows.Forms.Button
$continue.Text = '我已验证，继续调价'
$continue.Location = New-Object System.Drawing.Point(24,268)
$continue.Size = New-Object System.Drawing.Size(315,56)
$continue.BackColor = [System.Drawing.Color]::FromArgb(255,196,0)
$continue.Font = New-Object System.Drawing.Font('Microsoft YaHei UI',12,[System.Drawing.FontStyle]::Bold)
$continue.DialogResult = [System.Windows.Forms.DialogResult]::OK
$later = New-Object System.Windows.Forms.Button
$later.Text = '稍后处理'
$later.Location = New-Object System.Drawing.Point(359,268)
$later.Size = New-Object System.Drawing.Size(217,56)
$later.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
$form.CancelButton = $later
$form.Controls.AddRange(@($heading,$detail,$continue,$later))
[System.Media.SystemSounds]::Exclamation.Play()
$result = $form.ShowDialog()
$form.Dispose()
if ($result -eq [System.Windows.Forms.DialogResult]::OK) { exit 0 }
exit 2
