param(
  [string] $UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.4.2f1\Editor\Unity.exe",
  [string] $CultLibRoot = "E:\Projects\CultLib",
  [string] $OutputDirectory = "artifacts\playmode"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Join-Path $repoRoot "TestProject"
$outputRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }
$resultsPath = Join-Path $outputRoot "results.xml"
$logPath = Join-Path $outputRoot "unity.log"
$capturePath = Join-Path $outputRoot "world-smoke.png"

foreach ($required in @($UnityExe, $projectRoot, (Join-Path $CultLibRoot "scripts\build-unity-package.ps1"))) {
  if (-not (Test-Path -LiteralPath $required)) { throw "Required EveUnity PlayMode path not found: $required" }
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
powershell -ExecutionPolicy Bypass -File (Join-Path $CultLibRoot "scripts\build-unity-package.ps1")
if ($LASTEXITCODE -ne 0) { throw "CultLib Unity package build failed with exit code $LASTEXITCODE" }

$env:EVEUNITY_CAPTURE_PATH = $capturePath
$arguments = @(
  "-batchmode", "-projectPath", $projectRoot,
  "-runTests", "-testPlatform", "PlayMode",
  "-assemblyNames", "GameCult.EveUnity.GenericClient.PlayModeTests",
  "-testResults", $resultsPath, "-logFile", $logPath
)
$process = Start-Process -FilePath $UnityExe -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) {
  Get-Content -LiteralPath $logPath -Tail 120
  throw "EveUnity PlayMode capture failed with exit code $($process.ExitCode)"
}

[xml] $results = Get-Content -LiteralPath $resultsPath -Raw
$run = $results.SelectSingleNode("//test-run")
if ($null -eq $run -or [int] $run.total -eq 0 -or [int] $run.failed -gt 0) {
  throw "EveUnity PlayMode tests did not pass: $resultsPath"
}
if (-not (Test-Path -LiteralPath $capturePath) -or (Get-Item -LiteralPath $capturePath).Length -lt 1024) {
  throw "EveUnity PlayMode capture is missing or empty: $capturePath"
}

Write-Host "EveUnity PlayMode tests: $($run.passed) passed"
Write-Host "Capture: $capturePath"
