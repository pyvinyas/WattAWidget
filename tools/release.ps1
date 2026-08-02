# One-command release: bumps the version, commits, tags, and pushes.
# CI then builds, publishes the GitHub release, and submits the winget update PR.
#
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Version 1.0.1
param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

if (git status --porcelain) { throw "Working tree not clean - commit or stash first." }

# bump assembly version in source
$src = Join-Path $root 'src\WattWidget.cs'
$code = Get-Content $src -Raw
$code = $code -replace 'AssemblyVersion\("[\d\.]+"\)', "AssemblyVersion(`"$Version.0`")"
$code = $code -replace 'AssemblyFileVersion\("[\d\.]+"\)', "AssemblyFileVersion(`"$Version.0`")"
Set-Content $src $code -Encoding utf8 -NoNewline

# keep the reference manifests in packaging/ current (CI's wingetcreate generates
# the authoritative ones, but these should not go stale)
foreach ($f in Get-ChildItem (Join-Path $root 'packaging\winget\*.yaml')) {
    (Get-Content $f) -replace 'PackageVersion: [\d\.]+', "PackageVersion: $Version" `
        -replace 'download/v[\d\.]+/WattAWidget-[\d\.]+-win-x64\.zip', "download/v$Version/WattAWidget-$Version-win-x64.zip" `
        -replace 'InstallerSha256: .+', 'InstallerSha256: <FILLED-BY-CI>' |
        Set-Content $f -Encoding utf8
}

# sanity build before tagging
& (Join-Path $root 'build.ps1')

git add src\WattWidget.cs packaging
git commit -m "Release $Version"
git tag "v$Version"
git push
git push origin "v$Version"

Write-Host ""
Write-Host "v$Version tagged and pushed."
Write-Host "CI will now: build -> publish GitHub release -> submit winget PR."
Write-Host "Watch: https://github.com/pyvinyas/WattAWidget/actions"
