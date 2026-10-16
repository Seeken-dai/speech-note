param([string]$OutputDirectory = 'release')
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 4.x is required.' }
$outputDir = Join-Path $projectRoot $OutputDirectory
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$executable = Join-Path $outputDir 'SpeechNote.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 "/out:$executable" "/win32manifest:$projectRoot\app.manifest" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "$projectRoot\src\SpeechNote.cs" "$projectRoot\tests\SelfTest.cs"
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $projectRoot 'SpeechNote.exe.config') -Destination $outputDir -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/使用说明.txt') -Destination $outputDir -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'examples/示例讲稿.md') -Destination $outputDir -Force
Get-Item -LiteralPath $executable | Select-Object FullName,Length
