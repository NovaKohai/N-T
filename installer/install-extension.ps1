# Extension setup (unpacked folder + CRX): opens the extension folder and each installed browser's extensions page.
$dir = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'extension'
Start-Process explorer.exe "/select,`"$dir\NeoDM.crx`""
Start-Process explorer.exe "`"$dir\unpacked`""
$browsers = @(
  @{ Exe = 'chrome.exe'; Url = 'chrome://extensions' },
  @{ Exe = 'msedge.exe'; Url = 'edge://extensions' },
  @{ Exe = 'brave.exe';  Url = 'brave://extensions' })
foreach ($b in $browsers) {
  $p = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\$($b.Exe)" -ErrorAction SilentlyContinue).'(default)'
  if ($p -and (Test-Path -LiteralPath $p)) { Start-Process -FilePath $p -ArgumentList $b.Url }
}
Add-Type -AssemblyName System.Windows.Forms
[void][Windows.Forms.MessageBox]::Show("1. Turn on Developer mode on the extensions page.`n2. Click 'Load unpacked' and choose the 'unpacked' folder that was just opened.`n   (Or drag NeoDM.crx onto the page; some browsers refuse off-store CRX files.)", 'NeoDM browser extension')
