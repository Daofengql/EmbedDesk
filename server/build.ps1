# SPDX-License-Identifier: GPL-3.0-only
param(
    [ValidateSet('windows', 'linux')][string]$TargetOS = 'linux',
    [ValidateSet('amd64', 'arm64')][string]$TargetArch = 'amd64',
    [string]$Output
)
$ErrorActionPreference = 'Stop'
$serverRoot = $PSScriptRoot
if (-not $Output) {
    $extension = if ($TargetOS -eq 'windows') { '.exe' } else { '' }
    $Output = Join-Path $serverRoot "../artifacts/server/$TargetOS-$TargetArch/embeddesk-server$extension"
}
$Output = [IO.Path]::GetFullPath($Output)
$oldGoOS, $oldGoArch, $oldCgo = $env:GOOS, $env:GOARCH, $env:CGO_ENABLED
try {
    Push-Location (Join-Path $serverRoot 'www')
    try {
        npm ci
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'Console build failed' }
    } finally { Pop-Location }
    if (-not (Test-Path -LiteralPath (Join-Path $serverRoot 'www/dist/index.html'))) { throw 'Console output missing' }
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $Output) -Force
    $env:GOOS, $env:GOARCH, $env:CGO_ENABLED = $TargetOS, $TargetArch, '0'
    Push-Location $serverRoot
    try {
        go build -trimpath -ldflags '-s -w' -o $Output .
        if ($LASTEXITCODE -ne 0) { throw 'Server build failed' }
    } finally { Pop-Location }
    Write-Host "Built $Output (console and licenses embedded)"
} finally {
    $env:GOOS, $env:GOARCH, $env:CGO_ENABLED = $oldGoOS, $oldGoArch, $oldCgo
}
