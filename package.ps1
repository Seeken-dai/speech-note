param([ValidatePattern('^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$')][string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$releaseDir = Join-Path $PSScriptRoot 'release'
& (Join-Path $PSScriptRoot 'test.ps1') -ExecutablePath (Join-Path $releaseDir 'SpeechNote.exe')
$archivePath = Join-Path $releaseDir "SpeechNote-v$Version.zip"
# Allowlist package contents: never include settings.ini or arbitrary local manuscripts.
$packageFiles = @('SpeechNote.exe', 'SpeechNote.exe.config') | ForEach-Object { Join-Path $releaseDir $_ }
$packageFiles += Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'docs') -Filter '*.txt' -File | ForEach-Object { $_.FullName }
$packageFiles += Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'examples') -Filter '*.md' -File | ForEach-Object { $_.FullName }
Compress-Archive -LiteralPath $packageFiles -DestinationPath $archivePath -Force
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([IO.Path]::GetFileName($archivePath))" | Set-Content -LiteralPath (Join-Path $releaseDir 'SHA256SUMS.txt') -Encoding Ascii
Get-Item -LiteralPath $archivePath | Select-Object FullName,Length
