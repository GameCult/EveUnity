param(
  [string] $UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.4.2f1\Editor\Unity.exe",
  [string] $CultLibRoot = "E:\Projects\CultLib",
  [string] $ProjectRoot = "TestProject",
  [string] $OutputRoot = "artifacts\package-tests"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = if ([IO.Path]::IsPathRooted($ProjectRoot)) { $ProjectRoot } else { Join-Path $repoRoot $ProjectRoot }
$output = if ([IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot } else { Join-Path $repoRoot $OutputRoot }
$cultLibBuilder = Join-Path $CultLibRoot "scripts\build-unity-package.ps1"
foreach ($required in @($UnityExe, $projectPath, $cultLibBuilder)) {
  if (-not (Test-Path -LiteralPath $required)) { throw "Required EveUnity package-test path not found: $required" }
}

powershell -ExecutionPolicy Bypass -File $cultLibBuilder
if ($LASTEXITCODE -ne 0) { throw "CultLib Unity package build failed with exit code $LASTEXITCODE" }

$runRoot = Join-Path $output (Get-Date -Format "yyyyMMddTHHmmss")
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
$resultsPath = Join-Path $runRoot "unity-editmode-results.xml"
$logPath = Join-Path $runRoot "unity-editmode.log"
$arguments = @(
  "-batchmode", "-projectPath", $projectPath,
  "-runTests", "-testPlatform", "EditMode",
  "-assemblyNames", "GameCult.Eve.UnityScene.Tests",
  "-testResults", $resultsPath, "-logFile", $logPath
)
$process = Start-Process -FilePath $UnityExe -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) {
  if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Tail 120 }
  throw "EveUnity package tests failed with exit code $($process.ExitCode)"
}

[xml]$results = Get-Content -LiteralPath $resultsPath -Raw
$run = $results.SelectSingleNode("//test-run")
if ($null -eq $run -or [int]$run.total -eq 0 -or [int]$run.failed -gt 0) {
  throw "EveUnity package tests did not pass: $resultsPath"
}
Write-Host "EveUnity generic package tests: $($run.passed) passed, $($run.total) total"
Write-Host "Consumer: $projectPath"
