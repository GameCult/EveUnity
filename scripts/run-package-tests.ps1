param(
  [string] $UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.4.2f1\Editor\Unity.exe",
  [string] $CultLibRoot = "",
  [string] $EveRoot = "",
  [string] $EvePluginsRoot = "",
  [string] $ProjectRoot = "TestProject",
  [string] $OutputRoot = "artifacts\package-tests"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $repoRoot
if ([string]::IsNullOrWhiteSpace($CultLibRoot)) { $CultLibRoot = Join-Path $workspaceRoot "CultLib" }
if ([string]::IsNullOrWhiteSpace($EveRoot)) { $EveRoot = Join-Path $workspaceRoot "Eve" }
if ([string]::IsNullOrWhiteSpace($EvePluginsRoot)) { $EvePluginsRoot = Join-Path $workspaceRoot "EvePlugins" }
$projectPath = if ([IO.Path]::IsPathRooted($ProjectRoot)) { $ProjectRoot } else { Join-Path $repoRoot $ProjectRoot }
$output = if ([IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot } else { Join-Path $repoRoot $OutputRoot }
$cultLibBuilder = Join-Path $CultLibRoot "scripts\build-unity-package.ps1"
$obsoleteSurfacePackage = Join-Path $repoRoot "packages\org.gamecult.eve.surface"
$obsoleteSurfaceSource = @("package.json", "Runtime", "Editor") |
  ForEach-Object { Join-Path $obsoleteSurfacePackage $_ } |
  Where-Object { Test-Path -LiteralPath $_ }
if ($obsoleteSurfaceSource.Count -gt 0) {
  throw "EveUnity must consume the Eve-owned surface package; duplicate source exists at $obsoleteSurfacePackage"
}
foreach ($required in @(
  $UnityExe,
  $projectPath,
  $cultLibBuilder,
  (Join-Path $EveRoot "packages\org.gamecult.eve.surface\package.json"),
  (Join-Path $EvePluginsRoot "plugins\eve-plugin-fields\unity\org.gamecult.eve.plugin-fields\package.json")
)) {
  if (-not (Test-Path -LiteralPath $required)) { throw "Required EveUnity package-test path not found: $required" }
}

powershell -ExecutionPolicy Bypass -File $cultLibBuilder
if ($LASTEXITCODE -ne 0) { throw "CultLib Unity package build failed with exit code $LASTEXITCODE" }

$builtCultLibPackage = Join-Path $CultLibRoot "artifacts\unity\org.gamecult.cultlib"
if (-not (Test-Path -LiteralPath $builtCultLibPackage)) {
  throw "CultLib Unity package build did not produce: $builtCultLibPackage"
}

$runRoot = Join-Path $output (Get-Date -Format "yyyyMMddTHHmmss")
$consumerPath = Join-Path $runRoot "consumer"
$consumerPackagesPath = Join-Path $consumerPath "Packages"
$stagedDependenciesRoot = Join-Path $runRoot "dependencies"
New-Item -ItemType Directory -Force -Path $consumerPath, $consumerPackagesPath, $stagedDependenciesRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $projectPath "Assets") -Destination (Join-Path $consumerPath "Assets") -Recurse
Copy-Item -LiteralPath (Join-Path $projectPath "ProjectSettings") -Destination (Join-Path $consumerPath "ProjectSettings") -Recurse

$dependencySources = [ordered]@{
  "org.gamecult.cultlib" = $builtCultLibPackage
  "org.gamecult.eve.plugin-fields" = Join-Path $EvePluginsRoot "plugins\eve-plugin-fields\unity\org.gamecult.eve.plugin-fields"
  "org.gamecult.eve.surface" = Join-Path $EveRoot "packages\org.gamecult.eve.surface"
  "org.gamecult.eve.unity-scene" = Join-Path $repoRoot "packages\org.gamecult.eve.unity-scene"
  "org.gamecult.eve.unity-uitoolkit" = Join-Path $repoRoot "packages\org.gamecult.eve.unity-uitoolkit"
}
foreach ($entry in $dependencySources.GetEnumerator()) {
  Copy-Item -LiteralPath $entry.Value -Destination (Join-Path $stagedDependenciesRoot $entry.Key) -Recurse
}

$manifest = Get-Content -LiteralPath (Join-Path $projectPath "Packages\manifest.json") -Raw | ConvertFrom-Json
foreach ($packageId in $dependencySources.Keys) {
  $manifest.dependencies.$packageId = "file:../../dependencies/$packageId"
}
$manifestJson = $manifest | ConvertTo-Json -Depth 20
[IO.File]::WriteAllText(
  (Join-Path $consumerPackagesPath "manifest.json"),
  $manifestJson,
  [Text.UTF8Encoding]::new($false))

$resultsPath = Join-Path $runRoot "unity-editmode-results.xml"
$logPath = Join-Path $runRoot "unity-editmode.log"
$arguments = @(
  "-batchmode", "-projectPath", $consumerPath,
  "-runTests", "-testPlatform", "EditMode",
  "-assemblyNames", "GameCult.Eve.UnityScene.Tests",
  "-testResults", $resultsPath, "-logFile", $logPath
)
$process = Start-Process -FilePath $UnityExe -ArgumentList $arguments -PassThru -WindowStyle Hidden
if (-not $process.WaitForExit([int][TimeSpan]::FromMinutes(10).TotalMilliseconds)) {
  Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
  if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Tail 120 }
  throw "EveUnity package tests timed out after 10 minutes (Unity PID $($process.Id))."
}
$unityExitCode = $process.ExitCode
if ($unityExitCode -ne 0) {
  if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Tail 120 }
  throw "EveUnity package tests failed with exit code $unityExitCode"
}

if (-not (Test-Path -LiteralPath $resultsPath)) {
  if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath -Tail 120 }
  throw "EveUnity package tests produced no results: $resultsPath"
}

[xml]$results = Get-Content -LiteralPath $resultsPath -Raw
$run = $results.SelectSingleNode("//test-run")
if ($null -eq $run -or [int]$run.total -eq 0 -or [int]$run.failed -gt 0) {
  throw "EveUnity package tests did not pass: $resultsPath"
}
Write-Host "EveUnity generic package tests: $($run.passed) passed, $($run.total) total"
Write-Host "Isolated consumer: $consumerPath"
Write-Host "Staged dependencies: $stagedDependenciesRoot"
