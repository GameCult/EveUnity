param(
  [Parameter(Mandatory = $true)]
  [string] $AdvertisementPath,
  [string] $SurfaceId = "",
  [string] $OutputPath = "artifacts\capture\request.json",
  [string] $Stamp = "latest"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$capabilityPath = Join-Path $repoRoot "packages\org.gamecult.eve.unity-scene\eve-runtime-capability.json"
$capability = Get-Content -Raw $capabilityPath | ConvertFrom-Json
$advertisement = Get-Content -Raw $AdvertisementPath | ConvertFrom-Json
$contract = $capability.lifecycle.capture.captureContract
$surface = @($advertisement.surfaces) | Where-Object {
  ([string]::IsNullOrWhiteSpace($SurfaceId) -or $_.surfaceId -eq $SurfaceId) -and
  @($_.worldInteraction.loweringTargets) -contains $contract.targetId
} | Select-Object -First 1
if ($null -eq $surface) {
  throw "Provider does not advertise a $($contract.targetId) surface: $SurfaceId"
}
$absoluteOutput = if ([IO.Path]::IsPathRooted($OutputPath)) { $OutputPath } else { Join-Path $repoRoot $OutputPath }

$request = [ordered]@{
  schema = $contract.requestSchema
  ownerRepo = $contract.ownerRepo
  runtimeId = $contract.runtimeId
  targetId = $contract.targetId
  providerId = $advertisement.providerId
  surfaceId = $surface.surfaceId
  projectionKind = $surface.worldInteraction.projectionKind
  commandBoundary = $surface.worldInteraction.commandBoundary
  receiptSchema = $surface.worldInteraction.receiptSchema
  captureKind = $contract.captureKind
  artifactKind = $contract.artifactKind
  artifactPath = $contract.artifactPattern.Replace('{stamp}', $Stamp)
  sourceAdvertisementPath = $AdvertisementPath
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $absoluteOutput) | Out-Null
$request | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $absoluteOutput
Write-Host $absoluteOutput

