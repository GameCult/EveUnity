param(
  [string] $UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.4.2f1\Editor\Unity.exe",
  [string] $AetheriaRoot = "E:\Projects\Aetheria",
  [string] $CultLibRoot = "E:\Projects\CultLib",
  [string] $EveUnityRoot = "E:\Projects\EveUnity",
  [string] $ClientProject = "ReleaseConsumerProject",
  [int] $Port = 3076,
  [string] $OutputDirectory = "artifacts\aetheria-daemon",
  [ValidateSet("auto", "cold", "warm")]
  [string] $CacheState = "auto",
  [switch] $SkipAssetBundleBuild
)

$ErrorActionPreference = "Stop"
$expectedEveUnityCommit = "ed21479e467faea8ff624f2d2f5a7d2fecf913f4"
$expectedCultLibCommit = "feb5c71513e71d681699f462fe3682b3168c6f73"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = $ClientProject
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }
$resultsPath = Join-Path $outputRoot "results.xml"
$unityLogPath = Join-Path $outputRoot "unity.log"
$bundleBuildLogPath = Join-Path $outputRoot "asset-bundle-build.log"
$daemonLogPath = Join-Path $outputRoot "aetheria-daemon.log"
$capturePath = Join-Path $outputRoot "aetheria-daemon-world.png"
$mapCapturePath = Join-Path $outputRoot "aetheria-daemon-map.png"
$factsPath = Join-Path $outputRoot "witness-facts.json"
$witnessPath = Join-Path $outputRoot "runtime-witness.json"
$replicaPath = Join-Path $outputRoot "eve-unity-replica.cc"
$assetCachePath = Join-Path $outputRoot "asset-cache"
$statePath = Join-Path $outputRoot "aetheria-witness-state.cc"
$daemonProject = Join-Path $AetheriaRoot "Aetheria.State.Daemon\Aetheria.State.Daemon.csproj"
$importProject = Join-Path $AetheriaRoot "Aetheria.State.Import\Aetheria.State.Import.csproj"

foreach ($required in @($UnityExe, (Join-Path $repoRoot $projectRoot), $daemonProject, $importProject)) {
  if (-not (Test-Path -LiteralPath $required)) { throw "Required world witness path not found: $required" }
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
foreach ($priorArtifact in @($resultsPath, $capturePath, $mapCapturePath, $factsPath, $witnessPath)) {
  if (Test-Path -LiteralPath $priorArtifact) { Remove-Item -LiteralPath $priorArtifact -Force }
}
if ($CacheState -eq "cold" -and (Test-Path -LiteralPath $assetCachePath)) {
  Remove-Item -LiteralPath $assetCachePath -Recurse -Force
}
$initialBodyCount = @(Get-ChildItem -LiteralPath $assetCachePath -Filter *.body -File -Recurse -ErrorAction SilentlyContinue).Count
$initialPartialCount = @(Get-ChildItem -LiteralPath $assetCachePath -Filter *.partial -File -Recurse -ErrorAction SilentlyContinue).Count
if ($CacheState -eq "cold" -and ($initialBodyCount -ne 0 -or $initialPartialCount -ne 0)) {
  throw "Cold witness cache was not empty before Unity launch. bodies=$initialBodyCount partials=$initialPartialCount"
}
$cacheWasWarm = @(Get-ChildItem -LiteralPath $assetCachePath -Filter *.body -File -ErrorAction SilentlyContinue).Count -gt 0
if ($CacheState -eq "warm" -and -not $cacheWasWarm) {
  throw "Warm witness requested without an existing verified bundle cache at $assetCachePath"
}
$observedCacheState = if ($cacheWasWarm) { "warm" } else { "cold" }
$witnessStartedAt = [DateTimeOffset]::UtcNow
foreach ($ephemeralPath in @(
  $replicaPath,
  "$replicaPath.records",
  "$replicaPath.cultmesh",
  $statePath,
  "$statePath.records",
  "$statePath.cultmesh"
)) {
  $resolved = [IO.Path]::GetFullPath($ephemeralPath)
  if (-not $resolved.StartsWith([IO.Path]::GetFullPath($outputRoot), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Witness cleanup escaped its artifact directory: $resolved"
  }
  if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

$importArguments = @(
  "run", "--project", $importProject,
  "-p:CultLibRoot=$CultLibRoot", "-p:EveUnityRoot=$EveUnityRoot",
  "--", $AetheriaRoot, $statePath
)
$importLogPath = Join-Path $outputRoot "aetheria-import.log"
$importErrorLogPath = Join-Path $outputRoot "aetheria-import.error.log"
$previousErrorActionPreference = $ErrorActionPreference
try {
  $ErrorActionPreference = "Continue"
  & dotnet @importArguments 1> $importLogPath 2> $importErrorLogPath
  $importExitCode = $LASTEXITCODE
}
finally {
  $ErrorActionPreference = $previousErrorActionPreference
}
if ($importExitCode -ne 0) {
  throw "Aetheria state import failed with exit code $importExitCode. See $importErrorLogPath"
}
if (-not $SkipAssetBundleBuild) {
  $bundleBuilder = Start-Process -FilePath $UnityExe -ArgumentList @(
    "-batchmode", "-quit", "-projectPath", ".",
    "-executeMethod", "Aetheria.Editor.EveAssetBundleBuilder.BuildWindows",
    "-logFile", $bundleBuildLogPath
  ) -WorkingDirectory $AetheriaRoot -PassThru -WindowStyle Hidden
  Write-Host "AssetBundle builder PID: $($bundleBuilder.Id)"
  Write-Host "AssetBundle build log: $bundleBuildLogPath"
  Write-Host "Poll: Get-Content '$bundleBuildLogPath' -Tail 20"
  if (-not $bundleBuilder.WaitForExit(240000)) {
    Stop-Process -Id $bundleBuilder.Id -Force -ErrorAction SilentlyContinue
    throw "Aetheria AssetBundle build exceeded 240 seconds. See $bundleBuildLogPath"
  }
  if ($bundleBuilder.ExitCode -ne 0) {
    Get-Content $bundleBuildLogPath -Tail 160
    throw "Aetheria AssetBundle build failed with exit code $($bundleBuilder.ExitCode)"
  }
}

$daemonArguments = @(
  "run", "--project", $daemonProject,
  "-p:CultLibRoot=$CultLibRoot", "-p:EveUnityRoot=$EveUnityRoot",
  "--",
  "--root", $AetheriaRoot,
  "--state", $statePath,
  "--client-cultmesh-host", "127.0.0.1",
  "--client-cultmesh-advertise-host", "127.0.0.1",
  "--client-cultmesh-port", $Port,
  "--tick-interval-ms", 250,
  "--fixed-delta-ms", 20,
  "--no-odin-announcements"
)
$env:AETHERIA_TRACE_EVE_SNAPSHOTS = "1"
$env:AETHERIA_TRACE_CLIENT_RUDP = "1"
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

  $env:EVEUNITY_RENDEZVOUS_ENDPOINT = "rudp://127.0.0.1:$Port"
  $env:EVEUNITY_PROVIDER_ID = "aetheria"
  Remove-Item Env:EVEUNITY_SURFACE_ID -ErrorAction SilentlyContinue
  $env:EVEUNITY_REPLICA_PATH = $replicaPath
  $env:EVEUNITY_AETHERIA_CAPTURE_PATH = $capturePath
  $env:EVEUNITY_AETHERIA_MAP_CAPTURE_PATH = $mapCapturePath
  $env:EVEUNITY_DISABLE_AUTO_LAUNCHER = "1"
  $env:EVEUNITY_ASSET_CACHE_PATH = $assetCachePath
  $env:EVEUNITY_WITNESS_FACTS_PATH = $factsPath
  $arguments = @(
    "-batchmode", "-projectPath", $projectRoot,
    "-runTests", "-testPlatform", "PlayMode",
    "-assemblyNames", "GameCult.EveUnity.GenericClient.PlayModeTests",
    "-testFilter", "GenericCultMeshClientLowersAndMovesAdvertisedWorld",
    "-testResults", $resultsPath, "-logFile", $unityLogPath
  )
  $unity = Start-Process -FilePath $UnityExe -ArgumentList $arguments -WorkingDirectory $repoRoot -PassThru -WindowStyle Hidden
  if (-not $unity.WaitForExit(300000)) {
    Stop-Process -Id $unity.Id -Force -ErrorAction SilentlyContinue
    throw "Unity witness exceeded 300 seconds. See $unityLogPath"
  }
  if ($unity.ExitCode -ne 0) { Get-Content $unityLogPath -Tail 160; throw "Unity witness failed with exit code $($unity.ExitCode)" }
  [xml] $results = Get-Content $resultsPath -Raw
  $run = $results.SelectSingleNode("//test-run")
  if ($null -eq $run -or [int]$run.passed -ne 1 -or [int]$run.failed -ne 0) { throw "Live world witness did not pass: $resultsPath" }
  if (-not (Test-Path $capturePath) -or (Get-Item $capturePath).Length -lt 1024) { throw "Live world capture is missing: $capturePath" }
  if (-not (Test-Path $mapCapturePath) -or (Get-Item $mapCapturePath).Length -lt 1024) { throw "Live map-channel capture is missing: $mapCapturePath" }
  if (-not (Test-Path $factsPath)) { throw "Live world witness facts are missing: $factsPath" }
  foreach ($freshArtifact in @($resultsPath, $capturePath, $mapCapturePath, $factsPath)) {
    if ((Get-Item -LiteralPath $freshArtifact).LastWriteTimeUtc -lt $witnessStartedAt.UtcDateTime) {
      throw "Live witness artifact predates this run: $freshArtifact"
    }
  }
  $facts = Get-Content -LiteralPath $factsPath -Raw | ConvertFrom-Json
  $releaseLock = Get-Content -LiteralPath (Join-Path $repoRoot "ReleaseConsumerProject\Packages\packages-lock.json") -Raw | ConvertFrom-Json
  $releasedPackageClient = $projectRoot -eq "ReleaseConsumerProject"
  $sceneLock = $releaseLock.dependencies.'org.gamecult.eve.unity-scene'
  $cultLibLock = $releaseLock.dependencies.'org.gamecult.cultlib'
  if (-not $releasedPackageClient) { throw "Released witness must run ReleaseConsumerProject, got '$projectRoot'." }
  if ($sceneLock.source -ne "git" -or $cultLibLock.source -ne "git" -or
      $sceneLock.version -like "file:*" -or $cultLibLock.version -like "file:*") {
    throw "Released witness resolved a non-git/local package dependency."
  }
  if ($sceneLock.hash -ne $expectedEveUnityCommit -or $cultLibLock.hash -ne $expectedCultLibCommit) {
    throw "Released package commits do not match the witnessed releases. scene=$($sceneLock.hash) cultlib=$($cultLibLock.hash)"
  }
  $finalBodies = @(Get-ChildItem -LiteralPath $assetCachePath -Filter *.body -File -Recurse -ErrorAction SilentlyContinue)
  $finalPartials = @(Get-ChildItem -LiteralPath $assetCachePath -Filter *.partial -File -Recurse -ErrorAction SilentlyContinue)
  if ($finalBodies.Count -ne 1 -or $finalPartials.Count -ne 0) {
    throw "Content promotion is incomplete. bodies=$($finalBodies.Count) partials=$($finalPartials.Count)"
  }
  $bodyHash = (Get-FileHash -LiteralPath $finalBodies[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($finalBodies[0].BaseName -ne $bodyHash) {
    throw "Promoted content body name does not match its SHA-256. name=$($finalBodies[0].BaseName) hash=$bodyHash"
  }
  $facts | Add-Member -NotePropertyName releasedPackageClient -NotePropertyValue $releasedPackageClient
  $facts | Add-Member -NotePropertyName clientProject -NotePropertyValue $projectRoot
  $facts | Add-Member -NotePropertyName eveUnityPackageCommit -NotePropertyValue $sceneLock.hash
  $facts | Add-Member -NotePropertyName cultLibPackageCommit -NotePropertyValue $cultLibLock.hash
  $facts | Add-Member -NotePropertyName contentDelivery -NotePropertyValue ([ordered]@{
    initialBodyCount = $initialBodyCount
    initialPartialCount = $initialPartialCount
    finalBodyCount = $finalBodies.Count
    finalPartialCount = $finalPartials.Count
    contentHash = $bodyHash
    sizeBytes = $finalBodies[0].Length
  })
  $capture = Get-Item -LiteralPath $capturePath
  $mapCapture = Get-Item -LiteralPath $mapCapturePath
  $resultsArtifact = Get-Item -LiteralPath $resultsPath
  $durationMs = [Math]::Round(([DateTimeOffset]::UtcNow - $witnessStartedAt).TotalMilliseconds, 3)
  $testDurationMs = [Math]::Round(([double]$run.duration) * 1000, 3)
  $witness = [ordered]@{
    schema = "gamecult.eve.runtime_witness.v1"
    witnessId = "eveunity.aetheria.game.$observedCacheState"
    runtimeId = "unity-scene"
    runtimeOwnerRepo = "EveUnity"
    providerId = "aetheria"
    surfaceId = "aetheria.game"
    projectionKind = "provider-authored-world-surface"
    status = "pass"
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    execution = [ordered]@{
      cacheState = $observedCacheState
      durationMs = $durationMs
      testDurationMs = $testDurationMs
    }
    assertions = $facts
    receipts = @($facts.receipts)
    screenshotMetrics = [ordered]@{
      width = 640
      height = 360
      encodedSizeBytes = $capture.Length
      sha256 = (Get-FileHash -LiteralPath $capture.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
      mapEncodedSizeBytes = $mapCapture.Length
      mapSha256 = (Get-FileHash -LiteralPath $mapCapture.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
      mapChangedPixels = $facts.mapChangedPixels
    }
    artifacts = @(
      [ordered]@{
        kind = "unity-frame-png"
        path = $capture.Name
        sha256 = (Get-FileHash -LiteralPath $capture.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        sizeBytes = $capture.Length
        width = 640
        height = 360
      },
      [ordered]@{
        kind = "unity-map-channel-png"
        path = $mapCapture.Name
        sha256 = (Get-FileHash -LiteralPath $mapCapture.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        sizeBytes = $mapCapture.Length
        width = 640
        height = 360
      },
      [ordered]@{
        kind = "unity-test-results"
        path = $resultsArtifact.Name
        sha256 = (Get-FileHash -LiteralPath $resultsArtifact.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        sizeBytes = $resultsArtifact.Length
      }
    )
    authority = "released-generic-runtime-observes-provider-advertisement-assets-command-receipts-and-republished-surface-versions"
  }
  $witness | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $witnessPath -Encoding UTF8
  $stateWitnessPath = Join-Path $outputRoot "runtime-witness.$observedCacheState.json"
  $witness | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $stateWitnessPath -Encoding UTF8
  Write-Host "Aetheria daemon world witness: passed"
  Write-Host "Capture: $capturePath"
  Write-Host "Map capture: $mapCapturePath"
  Write-Host "Runtime witness: $witnessPath"
  Write-Host "Cache-state witness: $stateWitnessPath"
}
finally {
  if ($null -ne $daemon -and -not $daemon.HasExited) { Stop-Process -Id $daemon.Id -Force }
  Remove-Item Env:EVEUNITY_RENDEZVOUS_ENDPOINT, Env:EVEUNITY_PROVIDER_ENDPOINT, Env:EVEUNITY_PROVIDER_ID, Env:EVEUNITY_SURFACE_ID, Env:EVEUNITY_REPLICA_PATH, Env:EVEUNITY_AETHERIA_CAPTURE_PATH, Env:EVEUNITY_AETHERIA_MAP_CAPTURE_PATH, Env:EVEUNITY_DISABLE_AUTO_LAUNCHER, Env:EVEUNITY_ASSET_CACHE_PATH, Env:EVEUNITY_WITNESS_FACTS_PATH -ErrorAction SilentlyContinue
  Remove-Item Env:AETHERIA_TRACE_EVE_SNAPSHOTS -ErrorAction SilentlyContinue
}
