param(
  [string] $UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.4.2f1\Editor\Unity.exe",
  [string] $ResultsPath = "artifacts\tests\release-consumer-editmode.xml"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceProjectPath = Join-Path $repoRoot "ReleaseConsumerProject"
$results = if ([IO.Path]::IsPathRooted($ResultsPath)) { $ResultsPath } else { Join-Path $repoRoot $ResultsPath }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $results) | Out-Null
$runRoot = Join-Path (Split-Path -Parent $results) ("release-consumer-" + (Get-Date -Format "yyyyMMddTHHmmss"))
$projectPath = Join-Path $runRoot "consumer"
New-Item -ItemType Directory -Force -Path (Join-Path $projectPath "Packages") | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceProjectPath "Assets") -Destination (Join-Path $projectPath "Assets") -Recurse
Copy-Item -LiteralPath (Join-Path $sourceProjectPath "ProjectSettings") -Destination (Join-Path $projectPath "ProjectSettings") -Recurse
Copy-Item -LiteralPath (Join-Path $sourceProjectPath "Packages\manifest.json") `
  -Destination (Join-Path $projectPath "Packages\manifest.json")
$logPath = [IO.Path]::ChangeExtension($results, ".log")
$arguments = @(
  "-batchmode", "-projectPath", $projectPath,
  "-runTests", "-testPlatform", "EditMode",
  "-testResults", $results, "-logFile", $logPath
)

$process = Start-Process -FilePath $UnityExe -ArgumentList $arguments -WorkingDirectory $repoRoot -PassThru -WindowStyle Hidden
if (-not $process.WaitForExit([int][TimeSpan]::FromMinutes(10).TotalMilliseconds)) {
  Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
  if (Test-Path $logPath) { Get-Content -LiteralPath $logPath -Tail 120 }
  throw "Released EveUnity package consumer tests timed out after 10 minutes (Unity PID $($process.Id))."
}
if ($process.ExitCode -ne 0) {
  if (Test-Path $logPath) { Get-Content -LiteralPath $logPath -Tail 120 }
  throw "Released EveUnity package consumer tests failed with exit code $($process.ExitCode)"
}
if (-not (Test-Path $results)) { throw "Unity did not produce release consumer test results: $results" }

[xml] $report = Get-Content -LiteralPath $results -Raw
$result = $report.'test-run'.result
if ($result -ne "Passed") { throw "Released EveUnity package consumer result was '$result'." }
Write-Host "Released EveUnity package consumer passed: $results"
Write-Host "Isolated git consumer: $projectPath"
