param(
  [string] $AetheriaRoot = "E:\Projects\Aetheria",
  [string] $UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.4.2f1\Editor\Unity.exe",
  [string] $OutputRoot = "artifacts\aetheria-consumer"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $repoRoot "packages\org.gamecult.eve.unity-scene"
$manifestPath = Join-Path $AetheriaRoot "Packages\manifest.json"
$output = if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
  $OutputRoot
} else {
  Join-Path $repoRoot $OutputRoot
}

foreach ($required in @($packageRoot, $manifestPath, $UnityExe)) {
  if (-not (Test-Path -LiteralPath $required)) {
    throw "Required EveUnity consumer-test path not found: $required"
  }
}

$stamp = Get-Date -Format "yyyyMMddTHHmmss"
$runRoot = Join-Path $output $stamp
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null
$resultsPath = Join-Path $runRoot "unity-editmode-results.xml"
$logPath = Join-Path $runRoot "unity-editmode.log"
$originalManifest = Get-Content -Raw -LiteralPath $manifestPath

try {
  $manifest = $originalManifest | ConvertFrom-Json
  $manifestUri = [Uri] (($AetheriaRoot.TrimEnd('\\') + '\\'))
  $packageUri = [Uri] (($packageRoot.TrimEnd('\\') + '\\'))
  $relativePackageRoot = [Uri]::UnescapeDataString(
    $manifestUri.MakeRelativeUri($packageUri).ToString()).TrimEnd('/')
  $dependency = "file:" + ($relativePackageRoot -replace '\\', '/')
  $manifest.dependencies.'org.gamecult.eve.unity-scene' = $dependency
  $testables = @($manifest.testables) | Where-Object { $_ }
  if ($testables -notcontains 'org.gamecult.eve.unity-scene') {
    $testables += 'org.gamecult.eve.unity-scene'
  }
  if ($manifest.PSObject.Properties.Name -contains 'testables') {
    $manifest.testables = $testables
  } else {
    $manifest | Add-Member -NotePropertyName testables -NotePropertyValue $testables
  }
  $manifest | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $manifestPath

  $arguments = @(
    '-batchmode',
    '-projectPath', $AetheriaRoot,
    '-runTests',
    '-testPlatform', 'EditMode',
    '-assemblyNames', 'GameCult.Eve.UnityScene.Tests',
    '-testResults', $resultsPath,
    '-logFile', $logPath
  )
  $process = Start-Process -FilePath $UnityExe -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
  if ($process.ExitCode -ne 0) {
    if (Test-Path -LiteralPath $logPath) {
      Get-Content -LiteralPath $logPath -Tail 120
    }
    throw "EveUnity consumer tests failed with exit code $($process.ExitCode)"
  }
} finally {
  Set-Content -NoNewline -LiteralPath $manifestPath -Value $originalManifest
}

[xml] $results = Get-Content -Raw -LiteralPath $resultsPath
$run = $results.SelectSingleNode('//test-run')
if ($null -eq $run -or [int] $run.failed -gt 0) {
  throw "EveUnity consumer tests did not pass: $resultsPath"
}

Write-Host "EveUnity consumer tests: $($run.passed) passed, $($run.total) total"
Write-Host "Results: $resultsPath"
