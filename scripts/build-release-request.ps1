param(
  [string] $OutputPath = "artifacts\release\request.json"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$capabilityPath = Join-Path $repoRoot "packages\org.gamecult.eve.unity-scene\eve-runtime-capability.json"
$packagePath = Join-Path $repoRoot "packages\org.gamecult.eve.unity-scene\package.json"
$capability = Get-Content -Raw $capabilityPath | ConvertFrom-Json
$package = Get-Content -Raw $packagePath | ConvertFrom-Json
$contract = $capability.lifecycle.release.releaseContract
$absoluteOutput = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repoRoot $OutputPath }

$request = [ordered]@{
  schema = $contract.requestSchema
  ownerRepo = $contract.ownerRepo
  repository = "GameCult/EveUnity"
  packageName = $contract.packageName
  version = $package.version
  packageRoot = $contract.packageRoot
  versionSource = $contract.versionSource
  tagName = $contract.tagPattern.Replace('{version}', $package.version)
  artifactKind = $contract.artifactKind
  artifactPath = $contract.artifactPattern.Replace('{version}', $package.version)
  dependencies = $package.dependencies
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $absoluteOutput) | Out-Null
$request | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $absoluteOutput
Write-Host $absoluteOutput

