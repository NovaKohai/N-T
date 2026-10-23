# N&T browser extension setup: allows the user to pick which browser(s) to configure,
# opens the extension page for the selected browser, and copies the folder path for 1-click loading.

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$baseDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$unpacked = if (Test-Path "$baseDir\extension\unpacked") { "$baseDir\extension\unpacked" }
            elseif (Test-Path "$baseDir\..\extension") { [IO.Path]::GetFullPath("$baseDir\..\extension") }
            elseif (Test-Path "$baseDir\unpacked") { "$baseDir\unpacked" }
            else { "$baseDir\extension" }

$crx = if (Test-Path "$baseDir\extension\NeoDM.crx") { "$baseDir\extension\NeoDM.crx" }
       elseif (Test-Path "$baseDir\..\dist\NeoDM.crx") { [IO.Path]::GetFullPath("$baseDir\..\dist\NeoDM.crx") }
       else { $null }

$iconPath = if (Test-Path "$baseDir\Assets\NDM.ico") { "$baseDir\Assets\NDM.ico" }
            elseif (Test-Path "$baseDir\..\src\NDM.App\Assets\NDM.ico") { [IO.Path]::GetFullPath("$baseDir\..\src\NDM.App\Assets\NDM.ico") }
            else { $null }

# Browser catalog: detect installed Chromium browsers
$browserCatalog = @(
  @{ Id = 'chrome'; Name = 'Google Chrome'; Exe = 'chrome.exe'; Url = 'chrome://extensions' },
  @{ Id = 'edge';   Name = 'Microsoft Edge'; Exe = 'msedge.exe'; Url = 'edge://extensions' },
  @{ Id = 'brave';  Name = 'Brave Browser';  Exe = 'brave.exe';  Url = 'brave://extensions' },
  @{ Id = 'firefox'; Name = 'Mozilla Firefox'; Exe = 'firefox.exe'; Url = 'about:debugging#/runtime/this-firefox' }
)

$detected = @()
foreach ($b in $browserCatalog) {
  $path = (Get-ItemProperty ('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\' + $b.Exe) -ErrorAction SilentlyContinue).'(default)'
  if (-not $path) { $path = (Get-ItemProperty ('HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\' + $b.Exe) -ErrorAction SilentlyContinue).'(default)' }
  if ($path -and (Test-Path -LiteralPath $path)) {
    $b.Path = $path
    $detected += $b
  }
}

# If no browser was detected in registry, fallback to standard path detection
if ($detected.Count -eq 0) {
  $fallbacks = @(
    @{ Id = 'chrome'; Path = "$env:ProgramFiles\Google\Chrome\Application\chrome.exe" },
    @{ Id = 'chrome86'; Path = "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"; Target = 'chrome' },
    @{ Id = 'edge'; Path = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe" },
    @{ Id = 'brave'; Path = "$env:ProgramFiles\BraveSoftware\Brave-Browser\Application\brave.exe" },
    @{ Id = 'firefox'; Path = "$env:ProgramFiles\Mozilla Firefox\firefox.exe" },
    @{ Id = 'firefox86'; Path = "${env:ProgramFiles(x86)}\Mozilla Firefox\firefox.exe"; Target = 'firefox' }
  )
  foreach ($fb in $fallbacks) {
    if (Test-Path -LiteralPath $fb.Path) {
      $targetId = if ($fb.Target) { $fb.Target } else { $fb.Id }
      $match = $browserCatalog | Where-Object { $_.Id -eq $targetId } | Select-Object -First 1
      if ($match -and ($detected | Where-Object { $_.Id -eq $match.Id }).Count -eq 0) {
        $match.Path = $fb.Path
        $detected += $match
      }
    }
  }
}

# Build Form
$form = New-Object System.Windows.Forms.Form
$form.Text = "N&T - Browser Extension Setup"
$form.Size = New-Object System.Drawing.Size(490, 400)
$form.StartPosition = "CenterScreen"
$form.FormBorderStyle = "FixedDialog"
$form.MaximizeBox = $false
$form.MinimizeBox = $false
$form.BackColor = [System.Drawing.Color]::White

if ($iconPath -and (Test-Path -LiteralPath $iconPath)) {
  try { $form.Icon = New-Object System.Drawing.Icon($iconPath) } catch {}
}

# Header Banner
$headerPanel = New-Object System.Windows.Forms.Panel
$headerPanel.Dock = "Top"
$headerPanel.Height = 72
$headerPanel.BackColor = [System.Drawing.Color]::FromArgb(17, 24, 39) # Dark slate 900
$form.Controls.Add($headerPanel)

$titleLabel = New-Object System.Windows.Forms.Label
$titleLabel.UseMnemonic = $false
$titleLabel.Text = "N&T Browser Extension"
$titleLabel.Font = New-Object System.Drawing.Font("Segoe UI", 13, [System.Drawing.FontStyle]::Bold)
$titleLabel.ForeColor = [System.Drawing.Color]::White
$titleLabel.Location = New-Object System.Drawing.Point(18, 12)
$titleLabel.AutoSize = $true
$headerPanel.Controls.Add($titleLabel)

$subtitleLabel = New-Object System.Windows.Forms.Label
$subtitleLabel.UseMnemonic = $false
$subtitleLabel.Text = "Choose which browser(s) you want to use with N&T:"
$subtitleLabel.Font = New-Object System.Drawing.Font("Segoe UI", 9.5)
$subtitleLabel.ForeColor = [System.Drawing.Color]::FromArgb(209, 213, 219) # Slate 300
$subtitleLabel.Location = New-Object System.Drawing.Point(19, 40)
$subtitleLabel.AutoSize = $true
$headerPanel.Controls.Add($subtitleLabel)

# Main Body Panel
$bodyPanel = New-Object System.Windows.Forms.Panel
$bodyPanel.Location = New-Object System.Drawing.Point(20, 84)
$bodyPanel.Size = New-Object System.Drawing.Size(435, 210)
$form.Controls.Add($bodyPanel)

$infoLabel = New-Object System.Windows.Forms.Label
$infoLabel.Text = "Select your browser(s) below. The extension management page will open, and the folder path will be copied to your clipboard so you can easily click 'Load unpacked' and paste."
$infoLabel.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$infoLabel.ForeColor = [System.Drawing.Color]::FromArgb(75, 85, 99)
$infoLabel.Location = New-Object System.Drawing.Point(0, 4)
$infoLabel.Size = New-Object System.Drawing.Size(430, 44)
$bodyPanel.Controls.Add($infoLabel)

$checkBoxes = @()
$yPos = 52

# Render checkboxes for all browsers (installed ones enabled and checked by default)
foreach ($b in $browserCatalog) {
  $isInstalled = ($detected | Where-Object { $_.Id -eq $b.Id }).Count -gt 0
  $cb = New-Object System.Windows.Forms.CheckBox
  $cb.Text = if ($isInstalled) { "$($b.Name)  (Installed)" } else { "$($b.Name)  (Not detected)" }
  $cb.Font = New-Object System.Drawing.Font("Segoe UI", 9.5)
  $cb.Location = New-Object System.Drawing.Point(10, $yPos)
  $cb.Size = New-Object System.Drawing.Size(390, 26)
  $cb.Checked = $isInstalled -and ($b.Id -eq 'chrome' -or $detected.Count -eq 1)
  $cb.Enabled = $isInstalled
  $cb.Tag = $b
  $bodyPanel.Controls.Add($cb)
  $checkBoxes += $cb
  $yPos += 30
}

# If none was checked by default (e.g. Chrome not installed), check the first installed one
if (($checkBoxes | Where-Object { $_.Checked }).Count -eq 0) {
  $first = $checkBoxes | Where-Object { $_.Enabled } | Select-Object -First 1
  if ($first) { $first.Checked = $true }
}

# Extra Options
$cbClipboard = New-Object System.Windows.Forms.CheckBox
$cbClipboard.Text = "Copy unpacked extension folder path to clipboard"
$cbClipboard.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$cbClipboard.ForeColor = [System.Drawing.Color]::FromArgb(55, 65, 81)
$cbClipboard.Location = [System.Drawing.Point]::new(10, ($yPos + 8))
$cbClipboard.Size = New-Object System.Drawing.Size(390, 22)
$cbClipboard.Checked = $true
$bodyPanel.Controls.Add($cbClipboard)

# Bottom Actions
$btnPanel = New-Object System.Windows.Forms.Panel
$btnPanel.Dock = "Bottom"
$btnPanel.Height = 55
$btnPanel.BackColor = [System.Drawing.Color]::FromArgb(249, 250, 251)
$form.Controls.Add($btnPanel)

$btnContinue = New-Object System.Windows.Forms.Button
$btnContinue.UseMnemonic = $false
$btnContinue.Text = "Open Selected & Setup"
$btnContinue.Font = New-Object System.Drawing.Font("Segoe UI", 9.5, [System.Drawing.FontStyle]::Bold)
$btnContinue.Size = New-Object System.Drawing.Size(185, 34)
$btnContinue.Location = New-Object System.Drawing.Point(180, 10)
$btnContinue.BackColor = [System.Drawing.Color]::FromArgb(16, 185, 129) # Emerald 500
$btnContinue.ForeColor = [System.Drawing.Color]::White
$btnContinue.FlatStyle = [System.Windows.Forms.FlatStyle]::Flat
$btnContinue.FlatAppearance.BorderSize = 0
$btnContinue.DialogResult = [System.Windows.Forms.DialogResult]::OK
$btnPanel.Controls.Add($btnContinue)

$btnCancel = New-Object System.Windows.Forms.Button
$btnCancel.Text = "Cancel"
$btnCancel.Font = New-Object System.Drawing.Font("Segoe UI", 9)
$btnCancel.Size = New-Object System.Drawing.Size(80, 34)
$btnCancel.Location = New-Object System.Drawing.Point(375, 10)
$btnCancel.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
$btnPanel.Controls.Add($btnCancel)

$form.AcceptButton = $btnContinue
$form.CancelButton = $btnCancel

$result = $form.ShowDialog()

if ($result -eq [System.Windows.Forms.DialogResult]::OK) {
  # 1. Copy path to clipboard
  if ($cbClipboard.Checked -and (Test-Path -LiteralPath $unpacked)) {
    try { [System.Windows.Forms.Clipboard]::SetText($unpacked) } catch {}
  }

  # 2. Open explorer to the unpacked folder
  if (Test-Path -LiteralPath $unpacked) {
    Start-Process explorer.exe "`"$unpacked`""
  }
  if ($crx -and (Test-Path -LiteralPath $crx)) {
    Start-Process explorer.exe "/select,`"$crx`""
  }

  # 3. Open chosen browsers
  $selectedBrowsers = $checkBoxes | Where-Object { $_.Checked } | ForEach-Object { $_.Tag }
  foreach ($b in $selectedBrowsers) {
    if ($b.Path -and (Test-Path -LiteralPath $b.Path)) {
      Start-Process -FilePath $b.Path -ArgumentList $b.Url
    }
  }

  # 4. Show friendly guide
  $msg = "Next steps in your browser:`n`n" +
         "1. Enable 'Developer mode' (toggle in the top-right corner).`n" +
         "2. Click the 'Load unpacked' button.`n" +
         "3. Select the folder (the path is already copied to your clipboard: Ctrl+V).`n`n" +
         "N&T Integration will be active immediately!"
  [void][System.Windows.Forms.MessageBox]::Show($msg, "N&T Extension Setup", [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information)
}

$form.Dispose()
