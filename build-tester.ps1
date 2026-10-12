<#
.SYNOPSIS
  Builds a personalised, time-limited test copy of TimerResTuner for one tester.

.DESCRIPTION
  Stamps the tester's name and an expiry date into the exe, publishes a self-contained single-file
  build (the tester does not need .NET installed), zips it, and records what was issued
  (tester-builds\issued.csv) so a copy that turns up somewhere it shouldn't can be traced back
  (match the SHA-256, or read the name in the title bar / banner).

  After the expiry date the app can no longer benchmark or apply anything, but it can still UNDO:
  stop the background holder, remove the logon task, and turn GlobalTimerResolutionRequests /
  disabled dynamic tick back off. A logon task that fires after expiry removes itself.
  This is a deterrent, not DRM.

.EXAMPLE
  .\build-tester.ps1 -Tester Alex                      # expires in 7 days
  .\build-tester.ps1 -Tester "Sam K" -Days 3
  .\build-tester.ps1 -Tester Jo -ExpiresOn 2026-11-01  # fixed date (UTC, valid through that day)
#>
[CmdletBinding(DefaultParameterSetName = 'Days')]
param(
    [Parameter(Mandatory)][string] $Tester,
    [Parameter(ParameterSetName = 'Days')][ValidateRange(1, 365)][int] $Days = 7,
    [Parameter(Mandatory, ParameterSetName = 'Date')][string] $ExpiresOn,
    [string] $OutRoot = (Join-Path $PSScriptRoot 'tester-builds'),
    [switch] $FrameworkDependent,   # smaller exe, but the tester needs the .NET 8 Desktop Runtime
    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'

if ($Tester -notmatch '^[A-Za-z0-9][A-Za-z0-9 ._-]{0,39}$') {
    throw "Tester name must be 1-40 characters: letters, digits, space, '.', '_' or '-'."
}

if ($PSCmdlet.ParameterSetName -eq 'Date') {
    $parsed = [datetime]::MinValue
    if (-not [datetime]::TryParseExact($ExpiresOn, 'yyyy-MM-dd', [cultureinfo]::InvariantCulture, 'None', [ref]$parsed)) {
        throw "ExpiresOn must look like 2026-11-01."
    }
    if ($parsed.Date -lt (Get-Date).ToUniversalTime().Date) { throw "ExpiresOn is in the past." }
    $expires = $parsed.ToString('yyyy-MM-dd')
} else {
    $expires = (Get-Date).ToUniversalTime().AddDays($Days).ToString('yyyy-MM-dd')
}

$safe = ($Tester -replace '[^A-Za-z0-9._-]', '_')
$outDir = Join-Path $OutRoot $safe
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
New-Item -ItemType Directory -Force $outDir | Out-Null

$mode = if ($FrameworkDependent) { 'false' } else { 'true' }
Write-Host "Building for '$Tester', valid through $expires (UTC), self-contained=$mode..." -ForegroundColor Cyan
dotnet publish (Join-Path $PSScriptRoot 'TimerResTuner.csproj') -c Release -r win-x64 --self-contained $mode `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $outDir `
    "-p:TesterId=$Tester" "-p:ExpiresOn=$expires" -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# Only the exe is meant to be handed out.
Get-ChildItem $outDir -File | Where-Object { $_.Name -ne 'TimerResTuner.exe' } | Remove-Item -Force
$exe = Join-Path $outDir 'TimerResTuner.exe'
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash

$deliverable = $exe
if (-not $NoZip) {
    $deliverable = Join-Path $OutRoot "TimerResTuner-$safe.zip"
    if (Test-Path $deliverable) { Remove-Item $deliverable -Force }
    Compress-Archive -Path $exe -DestinationPath $deliverable
}

$log = Join-Path $OutRoot 'issued.csv'
if (-not (Test-Path $log)) { 'IssuedUtc,Tester,ExpiresOn,Sha256' | Set-Content $log -Encoding utf8 }
('{0},"{1}",{2},{3}' -f (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm'), $Tester, $expires, $hash) |
    Add-Content $log -Encoding utf8

Write-Host ''
Write-Host "Send this file : $deliverable" -ForegroundColor Green
Write-Host "Tester         : $Tester"
Write-Host "Valid through  : $expires (UTC)"
Write-Host "SHA-256        : $hash"
Write-Host "Issue log      : $log"
