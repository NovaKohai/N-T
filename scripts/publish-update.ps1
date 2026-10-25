[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipTests = $true,
    [switch]$NoGit,
    [string]$GitHubToken
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $root

Write-Host "====================================================================" -ForegroundColor Cyan
Write-Host "             N&T All-in-One Release & Update Publisher             " -ForegroundColor Cyan
Write-Host "====================================================================" -ForegroundColor Cyan
Write-Host ""

# 1. Read current version
$propsPath = Join-Path $root 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $propsPath)) {
    throw "Directory.Build.props not found at: $propsPath"
}
$propsContent = Get-Content -LiteralPath $propsPath -Raw
$verMatch = [regex]::Match($propsContent, '<Version>\s*(\d+)\.(\d+)\.(\d+)\s*</Version>')
if (-not $verMatch.Success) {
    throw "Could not parse <Version> from Directory.Build.props"
}

$major = [int]$verMatch.Groups[1].Value
$minor = [int]$verMatch.Groups[2].Value
$patch = [int]$verMatch.Groups[3].Value
$currentVersion = "$major.$minor.$patch"
$nextVersion = "$major.$minor.$($patch + 1)"

$targetVersion = $Version

if ([string]::IsNullOrWhiteSpace($targetVersion)) {
    Write-Host "Current version:   " -NoNewline
    Write-Host "$currentVersion" -ForegroundColor Yellow
    Write-Host "Suggested version: " -NoNewline
    Write-Host "$nextVersion" -ForegroundColor Green
    Write-Host ""
    Write-Host "Select an option:"
    Write-Host "  [1] Bump to $nextVersion and publish (Press ENTER)" -ForegroundColor Green
    Write-Host "  [2] Enter a custom version number"
    Write-Host "  [3] Re-publish current version ($currentVersion)"
    Write-Host "  [4] Cancel"
    Write-Host ""
    $choice = Read-Host "Choice [1-4] (Default 1)"

    if ([string]::IsNullOrWhiteSpace($choice) -or $choice -eq '1') {
        $targetVersion = $nextVersion
    } elseif ($choice -eq '2') {
        $custom = Read-Host "Enter new version (e.g. 1.0.3)"
        if ($custom -match '^\d+\.\d+\.\d+$') {
            $targetVersion = $custom
        } else {
            throw "Invalid version format: '$custom'. Must be major.minor.patch (e.g. 1.0.3)"
        }
    } elseif ($choice -eq '3') {
        $targetVersion = $currentVersion
    } else {
        Write-Host "Cancelled by user." -ForegroundColor Yellow
        exit 0
    }
}

Write-Host ""
Write-Host "Publishing target version: " -NoNewline
Write-Host "$targetVersion" -ForegroundColor Cyan
Write-Host ""

# 2. Verify or add changelog entry
$changelogPath = Join-Path $root 'CHANGELOG.md'
$changelog = Get-Content -LiteralPath $changelogPath -Raw
if ($changelog -notmatch "## \[$([regex]::Escape($targetVersion))\]") {
    Write-Host "CHANGELOG.md does not have release notes for [$targetVersion]." -ForegroundColor Yellow
    Write-Host "Enter a short summary of changes for this release (or press Enter to open CHANGELOG.md):" -ForegroundColor Cyan
    $notes = Read-Host "Release notes"
    if ([string]::IsNullOrWhiteSpace($notes)) {
        Write-Host "Opening CHANGELOG.md in editor... Save, close, and press Enter to continue." -ForegroundColor Yellow
        Start-Process "notepad.exe" -ArgumentList "`"$changelogPath`"" -Wait
        $changelog = Get-Content -LiteralPath $changelogPath -Raw
        if ($changelog -notmatch "## \[$([regex]::Escape($targetVersion))\]") {
            throw "CHANGELOG.md still has no '## [$targetVersion]' section."
        }
    } else {
        $today = (Get-Date).ToString("yyyy-MM-dd")
        $newSection = "## [$targetVersion] - $today`r`n`r`n### Changed`r`n- $notes`r`n`r`n"
        if ($changelog -match "(?ms)^(# Changelog\r?\n.*?\r?\n)(## \[)") {
            $changelog = [regex]::Replace($changelog, "(?ms)^(# Changelog\r?\n.*?\r?\n)(## \[)", "`${1}$newSection`${2}", 1)
        } else {
            $changelog = $newSection + $changelog
        }
        [IO.File]::WriteAllText($changelogPath, $changelog, [Text.Encoding]::UTF8)
        Write-Host "--> Added release section for $targetVersion to CHANGELOG.md" -ForegroundColor Green
    }
}

# Update Directory.Build.props
if ($targetVersion -ne $currentVersion) {
    Write-Host "--> Updating Directory.Build.props to version $targetVersion..." -ForegroundColor Gray
    $newProps = $propsContent -replace '<Version>\s*\d+\.\d+\.\d+\s*</Version>', "<Version>$targetVersion</Version>"
    $newProps = $newProps -replace '<AssemblyVersion>\s*\d+\.\d+\.\d+\.\d+\s*</AssemblyVersion>', "<AssemblyVersion>$targetVersion.0</AssemblyVersion>"
    $newProps = $newProps -replace '<FileVersion>\s*\d+\.\d+\.\d+\.\d+\s*</FileVersion>', "<FileVersion>$targetVersion.0</FileVersion>"
    $newProps = $newProps -replace '<InformationalVersion>\s*[^<]+\s*</InformationalVersion>', "<InformationalVersion>$targetVersion</InformationalVersion>"
    [IO.File]::WriteAllText($propsPath, $newProps, [Text.Encoding]::UTF8)
}

# 3. Run build gate
Write-Host ""
Write-Host "--> Building N&T ($targetVersion)..." -ForegroundColor Cyan
if ($SkipTests) {
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1') -SkipTests
} else {
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
}
if ($LASTEXITCODE -ne 0) {
    throw "build.ps1 failed with exit code $LASTEXITCODE"
}

# 4. Build MSI Installer and Sync Assets
Write-Host ""
Write-Host "--> Building Windows Installer (MSI) and generating release checksums..." -ForegroundColor Cyan
powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build-installer.ps1')
if ($LASTEXITCODE -ne 0) {
    throw "build-installer.ps1 failed with exit code $LASTEXITCODE"
}

# 5. Deploy to Cloudflare
Write-Host ""
Write-Host "--> Deploying to Cloudflare (Worker & Static Assets)..." -ForegroundColor Cyan
npx wrangler deploy
if ($LASTEXITCODE -ne 0) {
    throw "Cloudflare deployment (wrangler deploy) failed with exit code $LASTEXITCODE"
}
Write-Host "--> Cloudflare deployment successful!" -ForegroundColor Green

# 6. Git commit, tag, and push
if (-not $NoGit) {
    Write-Host ""
    Write-Host "--> Committing and pushing to Git repository..." -ForegroundColor Cyan
    git add .
    git commit -m "N&T ${targetVersion}: release and Cloudflare update deployment"
    git push origin main
    git tag -a "v$targetVersion" -m "N&T $targetVersion"
    git push origin "v$targetVersion"
    Write-Host "--> Git release commit and tag v$targetVersion pushed!" -ForegroundColor Green
}

# 7. GitHub Release Asset Deployment
$versionedMsi = Join-Path $root "artifacts\N&T-Setup-$targetVersion.msi"
$token = $GitHubToken
if ([string]::IsNullOrWhiteSpace($token)) {
    $token = $env:GITHUB_TOKEN
}

$uploadedViaApi = $false
if (-not [string]::IsNullOrWhiteSpace($token) -and (Test-Path -LiteralPath $versionedMsi)) {
    try {
        Write-Host ""
        Write-Host "--> Creating GitHub Release and uploading MSI asset via API..." -ForegroundColor Cyan
        $headers = @{
            "Authorization" = "Bearer $token"
            "Accept" = "application/vnd.github+json"
            "X-GitHub-Api-Version" = "2022-11-28"
            "User-Agent" = "NT-Release-Publisher"
        }
        
        $clMatch = [regex]::Match($changelog, "(?ms)## \[$([regex]::Escape($targetVersion))\].*?(?=(?:`r?`n## \[|\Z))")
        $body = if ($clMatch.Success) { $clMatch.Value.Trim() } else { "Release N&T $targetVersion" }

        $releaseBody = @{
            tag_name = "v$targetVersion"
            name = "N&T $targetVersion"
            body = $body
            draft = $false
            prerelease = $false
        } | ConvertTo-Json

        $relResponse = Invoke-RestMethod -Uri "https://api.github.com/repos/NovaKohai/N-T/releases" -Method Post -Headers $headers -Body $releaseBody
        $uploadUrlTemplate = $relResponse.upload_url
        if ($uploadUrlTemplate) {
            $uploadBase = $uploadUrlTemplate -replace '\{.*\}', ''
            
            $msiName = [IO.Path]::GetFileName($versionedMsi)
            $msiBytes = [IO.File]::ReadAllBytes($versionedMsi)
            $uploadUri = "$uploadBase?name=" + [Uri]::EscapeDataString($msiName)
            $msiHeaders = @{
                "Authorization" = "Bearer $token"
                "Content-Type" = "application/octet-stream"
                "User-Agent" = "NT-Release-Publisher"
            }
            Write-Host "--> Uploading $msiName ($( [math]::Round($msiBytes.Length / 1MB, 2) ) MB)..." -ForegroundColor Gray
            $null = Invoke-RestMethod -Uri $uploadUri -Method Post -Headers $msiHeaders -Body $msiBytes
            
            $sumsFile = Join-Path $root "artifacts\SHA256SUMS.txt"
            if (Test-Path -LiteralPath $sumsFile) {
                $sumBytes = [IO.File]::ReadAllBytes($sumsFile)
                $sumUri = "$uploadBase?name=" + [Uri]::EscapeDataString("SHA256SUMS.txt")
                $null = Invoke-RestMethod -Uri $sumUri -Method Post -Headers $msiHeaders -Body $sumBytes
            }
            
            $uploadedViaApi = $true
            Write-Host "--> Successfully published GitHub Release v$targetVersion with installer asset!" -ForegroundColor Green
        }
    } catch {
        Write-Host "Could not upload automatically via API: $($_.Exception.Message)" -ForegroundColor Yellow
        $uploadedViaApi = $false
    }
}

if (-not $uploadedViaApi) {
    Write-Host ""
    Write-Host "====================================================================" -ForegroundColor Cyan
    Write-Host "                    Final Step: GitHub Release                      " -ForegroundColor Cyan
    Write-Host "====================================================================" -ForegroundColor Cyan
    Write-Host "1. Browser opened to GitHub Release creation page for tag v$targetVersion." -ForegroundColor Yellow
    Write-Host "2. Windows Explorer opened with the installer MSI file selected." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "--> Drag & drop the highlighted MSI file into GitHub, then click 'Publish release'!" -ForegroundColor Green
    Write-Host ""

    $newReleaseWebUrl = "https://github.com/NovaKohai/N-T/releases/new?tag=v$targetVersion&title=N%26T%20$targetVersion"
    Start-Process $newReleaseWebUrl

    if (Test-Path -LiteralPath $versionedMsi) {
        Start-Process "explorer.exe" -ArgumentList "/select,`"$versionedMsi`""
    }
}

# 8. Summary
Write-Host ""
Write-Host "====================================================================" -ForegroundColor Green
Write-Host "   SUCCESS: Version v$targetVersion has been published!             " -ForegroundColor Green
Write-Host "====================================================================" -ForegroundColor Green
Write-Host "Update endpoints:" -ForegroundColor Gray
Write-Host "  * Version metadata: https://n-t.amouhamed1998.workers.dev/version.json" -ForegroundColor Gray
Write-Host "  * Web landing page: https://n-t.amouhamed1998.workers.dev/" -ForegroundColor Gray
Write-Host ""
