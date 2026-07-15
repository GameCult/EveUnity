param(
  [string] $UnityExe = "C:\Program Files\Unity\Hub\Editor\6000.4.2f1\Editor\Unity.exe",
  [string] $AetheriaRoot = "E:\Projects\Aetheria",
  [string] $CultLibRoot = "E:\Projects\CultLib-codex-cultmesh-reliability",
  [string] $EveUnityRoot = "E:\Projects\EveUnity",
  [string] $YmirRoot = "E:\Projects\Ymir",
  [int] $Port = 3076,
  [string] $OutputDirectory = "artifacts\interactive",
  [switch] $SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "ReleaseConsumerProject"
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $root $OutputDirectory }
$buildLog = Join-Path $outputRoot "build.log"
$bundleBuildLog = Join-Path $outputRoot "asset-bundle-build.log"
$daemonLog = Join-Path $outputRoot "daemon.log"
$importLog = Join-Path $outputRoot "import.log"
$importErrorLog = Join-Path $outputRoot "import.error.log"
$state = Join-Path $outputRoot "aetheria.cc"
$replica = Join-Path $outputRoot "eve-unity-replica.cc"
$assetCache = Join-Path $outputRoot "asset-cache"
$exe = Join-Path $project "Build\Windows\EveUnity.exe"
$importProject = Join-Path $AetheriaRoot "Aetheria.State.Import\Aetheria.State.Import.csproj"
$daemonProject = Join-Path $AetheriaRoot "Aetheria.State.Daemon\Aetheria.State.Daemon.csproj"

foreach ($required in @($UnityExe, $project, $importProject, $daemonProject, (Join-Path $YmirRoot "src\Ymir.Core\Ymir.Core.csproj"))) {
  if (-not (Test-Path -LiteralPath $required)) { throw "Required interactive witness path not found: $required" }
}
if (Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue) {
  throw "UDP port $Port is already occupied."
}
New-Item -ItemType Directory -Force $outputRoot, $assetCache | Out-Null
foreach ($ephemeralPath in @($state, "$state.records", "$state.cultmesh", $replica, "$replica.records", "$replica.cultmesh")) {
  $resolved = [IO.Path]::GetFullPath($ephemeralPath)
  if (-not $resolved.StartsWith([IO.Path]::GetFullPath($outputRoot), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Interactive cleanup escaped its artifact directory: $resolved"
  }
  if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

if (-not $SkipBuild -or -not (Test-Path $exe)) {
  $build = Start-Process $UnityExe -ArgumentList @("-batchmode", "-quit", "-projectPath", $project, "-executeMethod", "GenericEveUnityBuild.BuildWindows", "-logFile", $buildLog) -PassThru -WindowStyle Hidden
  Write-Host "Build PID: $($build.Id)"
  Write-Host "Build log: $buildLog"
  if (-not $build.WaitForExit(300000)) { Stop-Process $build.Id -Force; throw "EveUnity build timed out." }
  if ($build.ExitCode -ne 0 -or -not (Test-Path $exe) -or (Select-String $buildLog -Pattern "Scripts have compiler errors|Build completed with a result of 'Failed'" -Quiet)) { Get-Content $buildLog -Tail 120; throw "EveUnity build failed." }
}

if (-not $SkipBuild) {
  $bundleBuilder = Start-Process $UnityExe -ArgumentList @(
    "-batchmode", "-quit", "-projectPath", ".",
    "-executeMethod", "Aetheria.Editor.EveAssetBundleBuilder.BuildWindows",
    "-logFile", $bundleBuildLog
  ) -WorkingDirectory $AetheriaRoot -PassThru -WindowStyle Hidden
  Write-Host "AssetBundle build PID: $($bundleBuilder.Id)"
  Write-Host "AssetBundle build log: $bundleBuildLog"
  if (-not $bundleBuilder.WaitForExit(240000)) { Stop-Process $bundleBuilder.Id -Force; throw "Aetheria AssetBundle build timed out." }
  if ($bundleBuilder.ExitCode -ne 0) { Get-Content $bundleBuildLog -Tail 120; throw "Aetheria AssetBundle build failed." }
}

$importArguments = @(
  "run", "--project", $importProject,
  "-p:CultLibRoot=$CultLibRoot", "-p:EveUnityRoot=$EveUnityRoot", "-p:YmirRoot=$YmirRoot",
  "--", $AetheriaRoot, $state
)
$previousErrorActionPreference = $ErrorActionPreference
try {
  $ErrorActionPreference = "Continue"
  & dotnet @importArguments 1> $importLog 2> $importErrorLog
  $importExitCode = $LASTEXITCODE
}
finally {
  $ErrorActionPreference = $previousErrorActionPreference
}
if ($importExitCode -ne 0) { throw "Aetheria state import failed. See $importErrorLog" }

$daemon = Start-Process dotnet -ArgumentList @(
  "run", "--project", $daemonProject,
  "-p:CultLibRoot=$CultLibRoot", "-p:EveUnityRoot=$EveUnityRoot", "-p:YmirRoot=$YmirRoot",
  "--",
  "--root", $AetheriaRoot,
  "--state", $state,
  "--client-cultmesh-host", "127.0.0.1",
  "--client-cultmesh-advertise-host", "127.0.0.1",
  "--client-cultmesh-port", $Port,
  "--tick-interval-ms", 250,
  "--fixed-delta-ms", 20,
  "--no-odin-announcements"
) -PassThru -WindowStyle Hidden -RedirectStandardOutput $daemonLog -RedirectStandardError "$daemonLog.error"
Write-Host "Daemon PID: $($daemon.Id)"
Write-Host "Daemon log: $daemonLog"
try {
  $ready = $false
  for ($i=0; $i -lt 240; $i++) {
    if ($daemon.HasExited) { throw "Aetheria daemon exited. See $daemonLog" }
    if ((Test-Path $daemonLog) -and
        (Select-String $daemonLog -Pattern "Aetheria client CultMesh endpoint: rudp://127.0.0.1:$Port" -Quiet)) {
      $ready = $true
      break
    }
    Start-Sleep -Milliseconds 500
  }
  if (-not $ready) { throw "Aetheria daemon did not open CultMesh port $Port within 120 seconds. See $daemonLog" }

  $env:EVEUNITY_RENDEZVOUS_ENDPOINT = "rudp://127.0.0.1:$Port"
  $env:EVEUNITY_PROVIDER_ID = "aetheria"
  $env:EVEUNITY_REPLICA_PATH = $replica
  $env:EVEUNITY_ASSET_CACHE_PATH = $assetCache
  Remove-Item Env:EVEUNITY_SURFACE_ID -ErrorAction SilentlyContinue
  $client = Start-Process $exe -ArgumentList "-force-d3d11" -PassThru
  Write-Host "Client PID: $($client.Id)"
  Write-Host "Asset cache: $assetCache"
  Write-Host "Close the EveUnity window when finished."
  $client.WaitForExit()
}
finally {
  if ($null -ne $daemon -and -not $daemon.HasExited) { Stop-Process $daemon.Id -Force }
  Remove-Item Env:EVEUNITY_RENDEZVOUS_ENDPOINT, Env:EVEUNITY_PROVIDER_ENDPOINT, Env:EVEUNITY_PROVIDER_ID, Env:EVEUNITY_SURFACE_ID, Env:EVEUNITY_REPLICA_PATH, Env:EVEUNITY_ASSET_CACHE_PATH -ErrorAction SilentlyContinue
}
