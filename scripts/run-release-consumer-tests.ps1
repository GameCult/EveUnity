param(
  [string] $UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.4.2f1\Editor\Unity.exe",
  [string] $ResultsPath = "artifacts\tests\release-consumer-editmode.xml"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = "ReleaseConsumerProject"
$results = if ([IO.Path]::IsPathRooted($ResultsPath)) { $ResultsPath } else { Join-Path $repoRoot $ResultsPath }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $results) | Out-Null
$logPath = [IO.Path]::ChangeExtension($results, ".log")
$arguments = @(
  "-batchmode", "-projectPath", $projectPath,
  "-runTests", "-testPlatform", "EditMode",
  "-testResults", $results, "-logFile", $logPath
)

$process = Start-Process -FilePath $UnityExe -ArgumentList $arguments -WorkingDirectory $repoRoot -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0) {
  if (Test-Path $logPath) { Get-Content -LiteralPath $logPath -Tail 120 }
  throw "Released EveUnity package consumer tests failed with exit code $($process.ExitCode)"
}
if (-not (Test-Path $results)) { throw "Unity did not produce release consumer test results: $results" }

[xml] $report = Get-Content -LiteralPath $results -Raw
$result = $report.'test-run'.result
if ($result -ne "Passed") { throw "Released EveUnity package consumer result was '$result'." }
Write-Host "Released EveUnity package consumer passed: $results"
