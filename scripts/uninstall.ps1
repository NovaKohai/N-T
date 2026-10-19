[CmdletBinding(SupportsShouldProcess=$true)]
param()
$ErrorActionPreference = 'Stop'
foreach ($vendor in 'Google\Chrome','Microsoft\Edge','BraveSoftware\Brave-Browser') {
  foreach ($hostName in 'com.ndm.bridge','com.nt.bridge') {
    $key = "HKCU:\Software\$vendor\NativeMessagingHosts\$hostName"
    if ((Test-Path -LiteralPath $key) -and $PSCmdlet.ShouldProcess($key,'Remove N&T native host registration')) {
      Remove-Item -LiteralPath $key -Recurse
      if (Test-Path -LiteralPath $key) { throw "N&T registration removal failed for $vendor ($hostName)" }
    }
  }
}
Write-Host 'NeoDM registration removed (or was absent). No other host registrations, Chrome profiles or download files touched.'
