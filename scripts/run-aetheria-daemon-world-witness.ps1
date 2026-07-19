param(
  [string] $UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.4.2f1\Editor\Unity.exe",
  [string] $AetheriaRoot = "E:\Projects\Aetheria",
  [string] $CultLibRoot = "E:\Projects\CultLib-release",
  [string] $EveUnityRoot = "E:\Projects\EveUnity",
  [string] $YmirRoot = "E:\Projects\Ymir-aetheria-integration",
  [string] $ClientProject = "ReleaseConsumerProject",
  [int] $Port = 3076,
  [string] $OutputDirectory = "artifacts\aetheria-daemon",
  [string] $AssetCacheDirectory = "",
  [ValidateSet("auto", "cold", "warm")]
  [string] $CacheState = "warm",
  [ValidateSet("released-client-proof", "cargo-capacity-rejection-proof")]
  [string] $GameplayScenario = "released-client-proof",
  [switch] $PrimeWarmCacheFromProviderBundle,
  [switch] $SkipAssetBundleBuild
)

$ErrorActionPreference = "Stop"
$expectedEveUnityCommit = "edaf3b1e3bbb06a00b18b9faf508e27e33ae050b"
$expectedEveFieldsCommit = "c5a4a75c1b727499b16c2dae1895f29e2a9f72f0"
$expectedEveUnityUiToolkitCommit = "4d0cbe0185bdc4fc65eb63503a7c5cb578539669"
$expectedCultLibCommit = "49c43baa4c80b3bf8f5febae40baf99e1f2a0262"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectRoot = $ClientProject
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $repoRoot $OutputDirectory }
$assetCachePath = if ([string]::IsNullOrWhiteSpace($AssetCacheDirectory)) {
  Join-Path $outputRoot "asset-cache"
} elseif ([IO.Path]::IsPathRooted($AssetCacheDirectory)) {
  $AssetCacheDirectory
} else {
  Join-Path $repoRoot $AssetCacheDirectory
}
$resultsPath = Join-Path $outputRoot "results.xml"
$unityLogPath = Join-Path $outputRoot "unity.log"
$bundleBuildLogPath = Join-Path $outputRoot "asset-bundle-build.log"
$daemonLogPath = Join-Path $outputRoot "aetheria-daemon.log"
$capturePath = Join-Path $outputRoot "aetheria-daemon-world.png"
$mapCapturePath = Join-Path $outputRoot "aetheria-daemon-map.png"
$factsPath = Join-Path $outputRoot "witness-facts.json"
$providerReadyPath = Join-Path $outputRoot "provider-ready.txt"
$witnessPath = Join-Path $outputRoot "runtime-witness.json"
$replicaPath = Join-Path $outputRoot "eve-unity-replica.cc"
$statePath = Join-Path $outputRoot "aetheria-witness-state.cc"
$providerBundlePath = Join-Path $AetheriaRoot "Build\EveAssets\StandaloneWindows64\aetheria-world"
$daemonProject = Join-Path $AetheriaRoot "Aetheria.State.Daemon\Aetheria.State.Daemon.csproj"
$importProject = Join-Path $AetheriaRoot "Aetheria.State.Import\Aetheria.State.Import.csproj"

foreach ($required in @($UnityExe, (Join-Path $repoRoot $projectRoot), $daemonProject, $importProject, (Join-Path $YmirRoot "src\Ymir.Core\Ymir.Core.csproj"))) {
  if (-not (Test-Path -LiteralPath $required)) { throw "Required world witness path not found: $required" }
}
$canonicalWitnessTest = Join-Path $repoRoot "TestProject\Assets\Tests\PlayMode\GenericWorldCaptureTests.cs"
$releaseWitnessTest = Join-Path $repoRoot "ReleaseConsumerProject\Assets\Tests\PlayMode\GenericWorldCaptureTests.cs"
if ((Get-FileHash -LiteralPath $canonicalWitnessTest -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $releaseWitnessTest -Algorithm SHA256).Hash) {
  throw "Released consumer witness drifted from the canonical generic-client test."
}

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
foreach ($priorArtifact in @($resultsPath, $capturePath, $mapCapturePath, $factsPath, $providerReadyPath, $witnessPath)) {
  if (Test-Path -LiteralPath $priorArtifact) { Remove-Item -LiteralPath $priorArtifact -Force }
}
Get-ChildItem -LiteralPath $outputRoot -Filter "field-*" -File -ErrorAction SilentlyContinue |
  Remove-Item -Force
if ($CacheState -eq "cold" -and (Test-Path -LiteralPath $assetCachePath)) {
  Remove-Item -LiteralPath $assetCachePath -Recurse -Force
}
$initialBodyCount = @(Get-ChildItem -LiteralPath $assetCachePath -Filter *.body -File -Recurse -ErrorAction SilentlyContinue).Count
$initialPartialCount = @(Get-ChildItem -LiteralPath $assetCachePath -Filter *.partial -File -Recurse -ErrorAction SilentlyContinue).Count
if ($CacheState -eq "cold" -and ($initialBodyCount -ne 0 -or $initialPartialCount -ne 0)) {
  throw "Cold witness cache was not empty before Unity launch. bodies=$initialBodyCount partials=$initialPartialCount"
}
$cacheWasWarm = @(Get-ChildItem -LiteralPath $assetCachePath -Filter *.body -File -ErrorAction SilentlyContinue).Count -gt 0
$observedCacheState = if ($CacheState -eq "warm") { "warm" } elseif ($cacheWasWarm) { "warm" } else { "cold" }
$witnessProfile = if ($observedCacheState -eq "cold") {
  "cold-start-lowering"
} else {
  "full-session-gameplay"
}
$witnessStartedAt = [DateTimeOffset]::UtcNow
foreach ($ephemeralPath in @(
  $replicaPath,
  "$replicaPath.records",
  "$replicaPath.cultmesh",
  $statePath,
  "$statePath.records",
  "$statePath.cultmesh",
  "$statePath.ymir.cc",
  "$statePath.ymir.cc.records",
  "$statePath.ymir.cc.cultmesh"
)) {
  $resolved = [IO.Path]::GetFullPath($ephemeralPath)
  if (-not $resolved.StartsWith([IO.Path]::GetFullPath($outputRoot), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Witness cleanup escaped its artifact directory: $resolved"
  }
  if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

$importArguments = @(
  "run", "--project", $importProject,
  "-p:CultLibRoot=$CultLibRoot", "-p:EveUnityRoot=$EveUnityRoot", "-p:YmirRoot=$YmirRoot",
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
  if (-not $bundleBuilder.WaitForExit(360000)) {
    Stop-Process -Id $bundleBuilder.Id -Force -ErrorAction SilentlyContinue
    throw "Aetheria AssetBundle build exceeded 360 seconds. See $bundleBuildLogPath"
  }
  if ($bundleBuilder.ExitCode -ne 0) {
    Get-Content $bundleBuildLogPath -Tail 160
    throw "Aetheria AssetBundle build failed with exit code $($bundleBuilder.ExitCode)"
  }
}
if ($PrimeWarmCacheFromProviderBundle) {
  if ($CacheState -ne "warm") {
    throw "Local provider-bundle priming is only valid for an explicitly warm witness."
  }
  if (-not (Test-Path -LiteralPath $providerBundlePath)) {
    throw "Provider-owned Aetheria bundle is missing: $providerBundlePath"
  }
  New-Item -ItemType Directory -Force -Path $assetCachePath | Out-Null
  $primeHash = (Get-FileHash -LiteralPath $providerBundlePath -Algorithm SHA256).Hash.ToLowerInvariant()
  $primeTarget = Join-Path $assetCachePath "$primeHash.body"
  if (-not (Test-Path -LiteralPath $primeTarget)) {
    $primePartial = Join-Path $assetCachePath "$primeHash.partial.local-prime"
    Copy-Item -LiteralPath $providerBundlePath -Destination $primePartial -Force
    if ((Get-FileHash -LiteralPath $primePartial -Algorithm SHA256).Hash.ToLowerInvariant() -ne $primeHash) {
      Remove-Item -LiteralPath $primePartial -Force -ErrorAction SilentlyContinue
      throw "Locally primed provider bundle failed its content-hash check."
    }
    Move-Item -LiteralPath $primePartial -Destination $primeTarget -Force
  }
  Write-Host "Warm cache primed locally from the provider bundle; this is not CDN-transfer evidence: $primeTarget"
}
if ($CacheState -eq "warm") {
  if (-not (Test-Path -LiteralPath $providerBundlePath)) {
    throw "Provider-owned Aetheria bundle is missing: $providerBundlePath"
  }
  $currentBundleHash = (Get-FileHash -LiteralPath $providerBundlePath -Algorithm SHA256).Hash.ToLowerInvariant()
  $currentWarmBody = Get-ChildItem -LiteralPath $assetCachePath -Filter "$currentBundleHash.body" -File -Recurse -ErrorAction SilentlyContinue |
    Select-Object -First 1
  if ($null -eq $currentWarmBody) {
    throw "Warm witness cache does not contain the current provider bundle. expected=$currentBundleHash cache=$assetCachePath"
  }
}

$daemonBuildLogPath = Join-Path $outputRoot "aetheria-daemon-build.log"
$daemonBuildErrorLogPath = Join-Path $outputRoot "aetheria-daemon-build.error.log"
$daemonBuildArguments = @(
  "build", $daemonProject,
  "-p:CultLibRoot=$CultLibRoot", "-p:EveUnityRoot=$EveUnityRoot", "-p:YmirRoot=$YmirRoot"
)
$previousErrorActionPreference = $ErrorActionPreference
try {
  $ErrorActionPreference = "Continue"
  & dotnet @daemonBuildArguments 1> $daemonBuildLogPath 2> $daemonBuildErrorLogPath
  $daemonBuildExitCode = $LASTEXITCODE
}
finally {
  $ErrorActionPreference = $previousErrorActionPreference
}
if ($daemonBuildExitCode -ne 0) {
  throw "Aetheria daemon prebuild failed with exit code $daemonBuildExitCode. See $daemonBuildErrorLogPath"
}

$daemonArguments = @(
  "run", "--project", $daemonProject, "--no-build",
  "-p:CultLibRoot=$CultLibRoot", "-p:EveUnityRoot=$EveUnityRoot", "-p:YmirRoot=$YmirRoot",
  "--",
  "--root", $AetheriaRoot,
  "--state", $statePath,
  "--client-cultmesh-host", "127.0.0.1",
  "--client-cultmesh-advertise-host", "127.0.0.1",
  "--client-cultmesh-port", $Port,
  "--tick-interval-ms", 20,
  "--fixed-delta-ms", 20,
  "--terminus-scenario", $GameplayScenario,
  "--no-odin-announcements"
)
$env:AETHERIA_TRACE_EVE_SNAPSHOTS = "1"
$env:AETHERIA_TRACE_CLIENT_RUDP = "1"
$env:EVEUNITY_RENDEZVOUS_ENDPOINT = "rudp://127.0.0.1:$Port"
$env:EVEUNITY_PROVIDER_ID = "aetheria"
Remove-Item Env:EVEUNITY_SURFACE_ID -ErrorAction SilentlyContinue
$env:EVEUNITY_REPLICA_PATH = $replicaPath
$env:EVEUNITY_AETHERIA_CAPTURE_PATH = $capturePath
$env:EVEUNITY_AETHERIA_MAP_CAPTURE_PATH = $mapCapturePath
$env:EVEUNITY_DISABLE_AUTO_LAUNCHER = "1"
$env:EVEUNITY_ASSET_CACHE_PATH = $assetCachePath
$env:EVEUNITY_WITNESS_FACTS_PATH = $factsPath
$env:EVEUNITY_PROVIDER_READY_PATH = $providerReadyPath
$env:EVEUNITY_WITNESS_PROFILE = $witnessProfile
$env:EVEUNITY_WITNESS_GAMEPLAY_SCENARIO = $GameplayScenario
$arguments = @(
  "-batchmode", "-projectPath", $projectRoot,
  "-runTests", "-testPlatform", "PlayMode",
  "-assemblyNames", "GameCult.EveUnity.GenericClient.PlayModeTests",
  "-testFilter", "GenericCultMeshClientLowersAndMovesAdvertisedWorld",
  "-testResults", $resultsPath, "-logFile", $unityLogPath
)
$unity = Start-Process -FilePath $UnityExe -ArgumentList $arguments -WorkingDirectory $repoRoot -PassThru -WindowStyle Hidden
Write-Host "Unity witness PID: $($unity.Id)"
Write-Host "Unity log: $unityLogPath"
$clientReady = $false
for ($attempt = 0; $attempt -lt 240; $attempt++) {
  if ($unity.HasExited) { throw "Unity exited before reaching provider connection. See $unityLogPath" }
  if (Test-Path -LiteralPath $providerReadyPath) { $clientReady = $true; break }
  Start-Sleep -Milliseconds 500
}
if (-not $clientReady) {
  Stop-Process -Id $unity.Id -Force -ErrorAction SilentlyContinue
  throw "Unity did not reach provider connection within 120 seconds. See $unityLogPath"
}

$daemon = Start-Process -FilePath "dotnet" -ArgumentList $daemonArguments -PassThru -WindowStyle Hidden `
  -RedirectStandardOutput $daemonLogPath -RedirectStandardError (Join-Path $outputRoot "aetheria-daemon.error.log")
Write-Host "Aetheria daemon PID: $($daemon.Id)"
Write-Host "Daemon log: $daemonLogPath"
Write-Host "Poll: Select-String -Path '$daemonLogPath' -Pattern 'Aetheria client CultMesh endpoint'"

try {
  $ready = $false
  for ($attempt = 0; $attempt -lt 240; $attempt++) {
    if ($daemon.HasExited) { throw "Aetheria daemon exited before publishing CultMesh. See $daemonLogPath" }
    if ((Test-Path $daemonLogPath) -and
        (Select-String -Path $daemonLogPath -Pattern "Aetheria client CultMesh endpoint: rudp://127.0.0.1:$Port" -Quiet)) { $ready = $true; break }
    Start-Sleep -Milliseconds 500
  }
  if (-not $ready) { throw "Aetheria daemon did not open CultMesh port $Port within 120 seconds. See $daemonLogPath" }

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
  if ($facts.witnessProfile -ne $witnessProfile) {
    throw "Live witness ran the wrong proof profile. expected=$witnessProfile actual=$($facts.witnessProfile)"
  }
  if (-not $facts.providerAssets -or -not $facts.environmentPresentation -or
      -not $facts.movement -or -not $facts.pilotCameraExcludesMapChannel -or
      -not $facts.mapCameraIncludesMapChannel -or [int]$facts.fieldVolumeLayerCount -le 0 -or
      [long]$facts.fieldVolumeCompositeCount -le 0 -or
      [int]$facts.fieldParticleCount -ne 65536 -or
      [int]$facts.fieldParticleDispatchCount -le 0 -or
      [int]$facts.fieldParticleDrawCount -le 0 -or
      -not $facts.fieldParticleMapCameraIsolated) {
    throw "Live witness did not prove provider assets, movement, Fields lowering, Stardust dispatch/draw, and camera-channel separation."
  }
  $gridCenterXCells = [double]$facts.fieldParticleGridCenter.x / 6.0
  $gridCenterYCells = [double]$facts.fieldParticleGridCenter.y / 6.0
  if ([math]::Abs($gridCenterXCells - [math]::Truncate($gridCenterXCells)) -gt 0.000001 -or
      [math]::Abs($gridCenterYCells - [math]::Truncate($gridCenterYCells)) -gt 0.000001) {
    throw "Live Stardust grid center was not snapped to its six-unit spatial lattice. center=$($facts.fieldParticleGridCenter.x),$($facts.fieldParticleGridCenter.y)"
  }
  $presentedCelestials = @($facts.presentedEntities | Where-Object { $_.entityKind -like "celestial.*" })
  $presentedBodies = @($presentedCelestials | Where-Object {
    $_.entityKind -in @("celestial.sun", "celestial.planet", "celestial.gas-giant")
  })
  $presentedAsteroids = @($presentedCelestials | Where-Object { $_.entityKind -eq "celestial.asteroid" })
  $invalidCelestials = @($presentedCelestials | Where-Object {
    [string]::IsNullOrWhiteSpace($_.assetRef) -or
    [int]$_.rendererCount -le 0 -or
    [int]$_.enabledRendererCount -le 0
  })
  if ($presentedBodies.Count -le 0 -or $presentedAsteroids.Count -le 0 -or
      $invalidCelestials.Count -ne 0 -or
      @($presentedCelestials | Where-Object { $_.intersectsPilotFrustum }).Count -le 0) {
    throw "Live witness did not prove provider-owned celestial bodies and asteroids reached the generic scene and intersected the pilot frustum. bodies=$($presentedBodies.Count) asteroids=$($presentedAsteroids.Count) invalid=$($invalidCelestials.Count)"
  }
  if ($witnessProfile -eq "full-session-gameplay") {
    if (-not $facts.combatPresentation -or [string]::IsNullOrWhiteSpace($facts.shotId) -or
        [double]$facts.lockProgress -le 0.99 -or -not $facts.destructionLoot) {
      throw "Full-session witness did not prove combat and daemon-proximity destruction loot."
    }
    if ($GameplayScenario -eq "cargo-capacity-rejection-proof") {
      if ($facts.gameplayScenario -ne $GameplayScenario -or -not $facts.pickupRejection -or
          [int]$facts.pickupRejectionEventCount -lt 1 -or [int]$facts.pickupCollectionEventCount -ne 0 -or
          $facts.pickupRejectionReason -ne "cargo-capacity" -or
          [double]$facts.cargoQuantityBeforeRejection -ne [double]$facts.cargoQuantityAfterRejection) {
        throw "Full-session rejection witness did not prove player cargo-capacity refusal at daemon pickup proximity."
      }
    } elseif (-not $facts.pickupCollection -or [int]$facts.pickupCollectionEventCount -ne 1) {
      throw "Full-session collection witness did not prove exactly-once player destruction-loot collection at daemon pickup proximity."
    }
  }
  $releaseLock = Get-Content -LiteralPath (Join-Path $repoRoot "ReleaseConsumerProject\Packages\packages-lock.json") -Raw | ConvertFrom-Json
  $releasedPackageClient = $projectRoot -eq "ReleaseConsumerProject"
  $sceneLock = $releaseLock.dependencies.'org.gamecult.eve.unity-scene'
  $fieldsLock = $releaseLock.dependencies.'org.gamecult.eve.plugin-fields'
  $uiToolkitLock = $releaseLock.dependencies.'org.gamecult.eve.unity-uitoolkit'
  $cultLibLock = $releaseLock.dependencies.'org.gamecult.cultlib'
  if (-not $releasedPackageClient) { throw "Released witness must run ReleaseConsumerProject, got '$projectRoot'." }
  if ($sceneLock.source -ne "git" -or $fieldsLock.source -ne "git" -or $uiToolkitLock.source -ne "git" -or $cultLibLock.source -ne "git" -or
      $sceneLock.version -like "file:*" -or $fieldsLock.version -like "file:*" -or $uiToolkitLock.version -like "file:*" -or $cultLibLock.version -like "file:*") {
    throw "Released witness resolved a non-git/local package dependency."
  }
  if ($sceneLock.hash -ne $expectedEveUnityCommit -or
      $fieldsLock.hash -ne $expectedEveFieldsCommit -or
      $uiToolkitLock.hash -ne $expectedEveUnityUiToolkitCommit -or
      $cultLibLock.hash -ne $expectedCultLibCommit) {
    throw "Released package commits do not match the witnessed releases. scene=$($sceneLock.hash) fields=$($fieldsLock.hash) uitoolkit=$($uiToolkitLock.hash) cultlib=$($cultLibLock.hash)"
  }
  $finalBodies = @(Get-ChildItem -LiteralPath $assetCachePath -Filter *.body -File -Recurse -ErrorAction SilentlyContinue)
  $finalPartials = @(Get-ChildItem -LiteralPath $assetCachePath -Filter *.partial -File -Recurse -ErrorAction SilentlyContinue)
  if (-not (Test-Path -LiteralPath $providerBundlePath)) {
    throw "Provider-owned Aetheria bundle is missing: $providerBundlePath"
  }
  $bodyHash = (Get-FileHash -LiteralPath $providerBundlePath -Algorithm SHA256).Hash.ToLowerInvariant()
  $promotedBundleBodies = @($finalBodies | Where-Object { $_.BaseName -eq $bodyHash })
  if ($promotedBundleBodies.Count -ne 1 -or $finalPartials.Count -ne 0) {
    throw "Provider bundle promotion is incomplete. matchingBodies=$($promotedBundleBodies.Count) bodies=$($finalBodies.Count) partials=$($finalPartials.Count) hash=$bodyHash"
  }
  $promotedBodyHash = (Get-FileHash -LiteralPath $promotedBundleBodies[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($promotedBodyHash -ne $bodyHash -or $promotedBundleBodies[0].Length -ne (Get-Item -LiteralPath $providerBundlePath).Length) {
    throw "Promoted provider bundle does not match its authoritative body. expected=$bodyHash actual=$promotedBodyHash"
  }
  $facts | Add-Member -NotePropertyName releasedPackageClient -NotePropertyValue $releasedPackageClient
  $facts | Add-Member -NotePropertyName clientProject -NotePropertyValue $projectRoot
  $facts | Add-Member -NotePropertyName eveUnityPackageCommit -NotePropertyValue $sceneLock.hash
  $facts | Add-Member -NotePropertyName eveFieldsPackageCommit -NotePropertyValue $fieldsLock.hash
  $facts | Add-Member -NotePropertyName eveUnityUiToolkitPackageCommit -NotePropertyValue $uiToolkitLock.hash
  $facts | Add-Member -NotePropertyName cultLibPackageCommit -NotePropertyValue $cultLibLock.hash
  $facts | Add-Member -NotePropertyName contentDelivery -NotePropertyValue ([ordered]@{
    initialBodyCount = $initialBodyCount
    initialPartialCount = $initialPartialCount
    finalBodyCount = $finalBodies.Count
    finalPartialCount = $finalPartials.Count
    contentHash = $bodyHash
    sizeBytes = $promotedBundleBodies[0].Length
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
      profile = $witnessProfile
      durationMs = $durationMs
      testDurationMs = $testDurationMs
    }
    assertions = $facts
    receipts = @($facts.receipts)
    screenshotMetrics = [ordered]@{
      width = 1280
      height = 720
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
        width = 1280
        height = 720
      },
      [ordered]@{
        kind = "unity-map-channel-png"
        path = $mapCapture.Name
        sha256 = (Get-FileHash -LiteralPath $mapCapture.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        sizeBytes = $mapCapture.Length
        width = 1280
        height = 720
      },
      [ordered]@{
        kind = "unity-test-results"
        path = $resultsArtifact.Name
        sha256 = (Get-FileHash -LiteralPath $resultsArtifact.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        sizeBytes = $resultsArtifact.Length
      }
    )
    authority = if ($witnessProfile -eq "full-session-gameplay") {
      if ($GameplayScenario -eq "cargo-capacity-rejection-proof") {
        "released-generic-runtime-observes-provider-assets-authoritative-gameplay-receipts-ymir-contact-capacity-rejection-and-camera-channel-separation"
      } else {
        "released-generic-runtime-observes-provider-assets-authoritative-gameplay-receipts-ymir-contact-collection-and-camera-channel-separation"
      }
    } else {
      "released-generic-runtime-cold-loads-provider-assets-lowers-playable-world-and-preserves-camera-channel-separation"
    }
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
  if ($null -ne $unity -and -not $unity.HasExited) { Stop-Process -Id $unity.Id -Force -ErrorAction SilentlyContinue }
  if ($null -ne $daemon -and -not $daemon.HasExited) { Stop-Process -Id $daemon.Id -Force }
  Remove-Item Env:EVEUNITY_RENDEZVOUS_ENDPOINT, Env:EVEUNITY_PROVIDER_ENDPOINT, Env:EVEUNITY_PROVIDER_ID, Env:EVEUNITY_SURFACE_ID, Env:EVEUNITY_REPLICA_PATH, Env:EVEUNITY_AETHERIA_CAPTURE_PATH, Env:EVEUNITY_AETHERIA_MAP_CAPTURE_PATH, Env:EVEUNITY_DISABLE_AUTO_LAUNCHER, Env:EVEUNITY_ASSET_CACHE_PATH, Env:EVEUNITY_WITNESS_FACTS_PATH, Env:EVEUNITY_PROVIDER_READY_PATH, Env:EVEUNITY_WITNESS_PROFILE, Env:EVEUNITY_WITNESS_GAMEPLAY_SCENARIO -ErrorAction SilentlyContinue
  Remove-Item Env:AETHERIA_TRACE_EVE_SNAPSHOTS -ErrorAction SilentlyContinue
}
