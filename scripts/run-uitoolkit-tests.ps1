param(
  [string] $AetheriaRoot = "E:\Projects\Aetheria",
  [string] $UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.4.2f1\Editor\Unity.exe",
  [string] $OutputRoot = "artifacts\uitoolkit-consumer"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $AetheriaRoot "Packages\manifest.json"
$output = if ([IO.Path]::IsPathRooted($OutputRoot)) { $OutputRoot } else { Join-Path $repoRoot $OutputRoot }
foreach ($required in @($manifestPath, $UnityExe, (Join-Path $repoRoot "packages\org.gamecult.eve.unity-uitoolkit"))) {
  if (-not (Test-Path -LiteralPath $required)) { throw "Required EveUnity UI Toolkit test path not found: $required" }
}

$runRoot = Join-Path $output (Get-Date -Format "yyyyMMddTHHmmss")
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
$resultsPath = Join-Path $runRoot "unity-editmode-results.xml"
$logPath = Join-Path $runRoot "unity-editmode.log"
$originalManifest = Get-Content -LiteralPath $manifestPath -Raw
try {
  $manifest = $originalManifest | ConvertFrom-Json
  $testables = @($manifest.testables) | Where-Object { $_ }
  if ($testables -notcontains "org.gamecult.eve.unity-uitoolkit") { $testables += "org.gamecult.eve.unity-uitoolkit" }
  if ($manifest.PSObject.Properties.Name -contains "testables") { $manifest.testables = $testables }
  else { $manifest | Add-Member -NotePropertyName testables -NotePropertyValue $testables }
  $manifest | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $manifestPath
  $arguments = @(
    "-batchmode", "-projectPath", $AetheriaRoot,
    "-runTests", "-testPlatform", "EditMode",
    "-assemblyNames", "GameCult.Eve.UnityUIToolkit.Tests",
    "-testResults", $resultsPath, "-logFile", $logPath
  )
  $process = Start-Process -FilePath $UnityExe -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
  if ($process.ExitCode -ne 0) {
    Get-Content -LiteralPath $logPath -Tail 120
    throw "EveUnity UI Toolkit tests failed with exit code $($process.ExitCode)"
  }
} finally {
  Set-Content -NoNewline -LiteralPath $manifestPath -Value $originalManifest
}

[xml]$results = Get-Content -LiteralPath $resultsPath -Raw
$run = $results.SelectSingleNode("//test-run")
if ($null -eq $run -or [int]$run.total -eq 0 -or [int]$run.failed -gt 0) { throw "EveUnity UI Toolkit tests did not pass: $resultsPath" }
Write-Host "EveUnity UI Toolkit tests: $($run.passed) passed, $($run.total) total"
