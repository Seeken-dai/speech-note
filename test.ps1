param([string]$ExecutablePath, [string]$OutputDirectory = '.local/test-output')
$ErrorActionPreference = 'Stop'
if (-not $ExecutablePath) { $ExecutablePath = Join-Path $PSScriptRoot 'release/SpeechNote.exe' }
$ExecutablePath = (Resolve-Path -LiteralPath $ExecutablePath).Path
$reportDir = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $OutputDirectory))
New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
$reportPath = Join-Path $reportDir 'self-test.txt'
$testProcess = Start-Process -FilePath $ExecutablePath -ArgumentList "--self-test `"$reportPath`"" -WindowStyle Hidden -PassThru -Wait
if (Test-Path -LiteralPath $reportPath) { Get-Content -LiteralPath $reportPath }
if ($testProcess.ExitCode -ne 0) { throw "Self-test failed (exit $($testProcess.ExitCode)). See $reportPath" }
