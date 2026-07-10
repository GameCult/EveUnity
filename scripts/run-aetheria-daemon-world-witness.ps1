param(
  [string] $UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.4.2f1\Editor\Unity.exe",
  [string] $AetheriaRoot = "E:\Projects\Aetheria",
  [string] $CultLibRoot = "E:\Projects\CultLib",
  [int] $Port = 3076,
  [string] $OutputDirectory = "artifacts\aetheria-daemon"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = Join-Path $repoRoot "TestProject"
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }
$resultsPath = Join-Path $outputRoot "results.xml"
$unityLogPath = Join-Path $outputRoot "unity.log"
$daemonLogPath = Join-Path $outputRoot "aetheria-daemon.log"
$capturePath = Join-Path $outputRoot "aetheria-daemon-world.png"
$replicaPath = Join-Path $outputRoot "eve-unity-replica.cc"
$statePath = Join-Path $outputRoot "aetheria-witness-state.cc"
$daemonProject = Join-Path $AetheriaRoot "Aetheria.State.Daemon\Aetheria.State.Daemon.csproj"

foreach ($required in @($UnityExe, $projectRoot, $daemonProject, (Join-Path $CultLibRoot "scripts\build-unity-package.ps1"))) {
  if (-not (Test-Path -LiteralPath $required)) { throw "Required world witness path not found: $required" }
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
foreach ($ephemeralPath in @($replicaPath, $statePath)) {
  $resolved = [IO.Path]::GetFullPath($ephemeralPath)
  if (-not $resolved.StartsWith([IO.Path]::GetFullPath($outputRoot), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Witness cleanup escaped its artifact directory: $resolved"
  }
  if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
powershell -ExecutionPolicy Bypass -File (Join-Path $CultLibRoot "scripts\build-unity-package.ps1")
if ($LASTEXITCODE -ne 0) { throw "CultLib Unity package build failed with exit code $LASTEXITCODE" }

$daemonArguments = @(
  "run", "--project", $daemonProject, "--",
  "--root", $AetheriaRoot,
  "--state", $statePath,
  "--client-cultmesh-host", "127.0.0.1",
  "--client-cultmesh-advertise-host", "127.0.0.1",
  "--client-cultmesh-port", $Port,
  "--no-odin-announcements"
)
$daemon = Start-Process -FilePath "dotnet" -ArgumentList $daemonArguments -PassThru -WindowStyle Hidden `
  -RedirectStandardOutput $daemonLogPath -RedirectStandardError (Join-Path $outputRoot "aetheria-daemon.error.log")
Write-Host "Aetheria daemon PID: $($daemon.Id)"
Write-Host "Daemon log: $daemonLogPath"
Write-Host "Poll: Select-String -Path '$daemonLogPath' -Pattern 'Aetheria client CultMesh endpoint'"

try {
  $ready = $false
  for ($attempt = 0; $attempt -lt 60; $attempt++) {
    if ($daemon.HasExited) { throw "Aetheria daemon exited before publishing CultMesh. See $daemonLogPath" }
    if ((Test-Path $daemonLogPath) -and
        (Select-String -Path $daemonLogPath -Pattern "Aetheria client CultMesh endpoint: rudp://127.0.0.1:$Port" -Quiet)) { $ready = $true; break }
    Start-Sleep -Milliseconds 500
  }
  if (-not $ready) { throw "Aetheria daemon did not open CultMesh port $Port. See $daemonLogPath" }

  $env:EVEUNITY_PROVIDER_ENDPOINT = "rudp://127.0.0.1:$Port"
  $env:EVEUNITY_PROVIDER_ID = "aetheria.daemon"
  $env:EVEUNITY_SURFACE_ID = "aetheria.game"
  $env:EVEUNITY_REPLICA_PATH = $replicaPath
  $env:EVEUNITY_AETHERIA_CAPTURE_PATH = $capturePath
  $arguments = @(
    "-batchmode", "-projectPath", $projectRoot,
    "-runTests", "-testPlatform", "PlayMode",
    "-assemblyNames", "GameCult.EveUnity.GenericClient.PlayModeTests",
    "-testFilter", "GenericCultMeshClientLowersAndMovesAdvertisedWorld",
    "-testResults", $resultsPath, "-logFile", $unityLogPath
  )
  $unity = Start-Process -FilePath $UnityExe -ArgumentList $arguments -PassThru -WindowStyle Hidden
  if (-not $unity.WaitForExit(90000)) {
    Stop-Process -Id $unity.Id -Force -ErrorAction SilentlyContinue
    throw "Unity witness exceeded 90 seconds. See $unityLogPath"
  }
  if ($unity.ExitCode -ne 0) { Get-Content $unityLogPath -Tail 160; throw "Unity witness failed with exit code $($unity.ExitCode)" }
  [xml] $results = Get-Content $resultsPath -Raw
  $run = $results.SelectSingleNode("//test-run")
  if ($null -eq $run -or [int]$run.passed -ne 1 -or [int]$run.failed -ne 0) { throw "Live world witness did not pass: $resultsPath" }
  if (-not (Test-Path $capturePath) -or (Get-Item $capturePath).Length -lt 1024) { throw "Live world capture is missing: $capturePath" }
  Write-Host "Aetheria daemon world witness: passed"
  Write-Host "Capture: $capturePath"
}
finally {
  if ($null -ne $daemon -and -not $daemon.HasExited) { Stop-Process -Id $daemon.Id -Force }
  Remove-Item Env:EVEUNITY_PROVIDER_ENDPOINT, Env:EVEUNITY_PROVIDER_ID, Env:EVEUNITY_SURFACE_ID, Env:EVEUNITY_REPLICA_PATH, Env:EVEUNITY_AETHERIA_CAPTURE_PATH -ErrorAction SilentlyContinue
}
