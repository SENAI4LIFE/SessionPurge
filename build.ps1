param([switch]$Test)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$compiler = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x x64 compiler is required.' }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Filter '*.cs' | ForEach-Object { $_.FullName })
$references = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll')
& $compiler /nologo /optimize+ /debug- /warn:4 /warnaserror+ /platform:x64 /target:winexe "/win32manifest:$projectRoot\src\app.manifest" "/out:$projectRoot\SessionPurge.exe" @references @sources
if ($LASTEXITCODE -ne 0) { throw 'Production build failed.' }
Write-Output 'Production executable built.'
if ($Test) {
    foreach ($directory in @('build','dev')) { New-Item -ItemType Directory -Path (Join-Path $projectRoot $directory) -Force | Out-Null }
    $testSources = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'tests') -Filter '*.cs' | ForEach-Object { $_.FullName })
    & $compiler /nologo /optimize+ /debug- /warn:4 /warnaserror+ /platform:x64 /target:exe /main:SessionPurge.Tests.TestMain "/out:$projectRoot\build\SessionPurge.Tests.exe" @references @sources @testSources
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    & (Join-Path $projectRoot 'build\SessionPurge.Tests.exe') (Join-Path $projectRoot 'dev')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
