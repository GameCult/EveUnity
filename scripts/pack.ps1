param(
  [string] $OutputRoot = "artifacts\packages"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $repoRoot "packages\org.gamecult.eve.unity-scene"
$output = if ([System.IO.Path]::IsPathRooted($OutputRoot)) {
  $OutputRoot
} else {
  Join-Path $repoRoot $OutputRoot
}

New-Item -ItemType Directory -Force -Path $output | Out-Null
$packed = npm pack $packageRoot --pack-destination $output
if ($LASTEXITCODE -ne 0) {
  throw "EveUnity UPM package failed to pack"
}

$artifact = Join-Path $output ($packed | Select-Object -Last 1)
if (-not (Test-Path -LiteralPath $artifact)) {
  throw "EveUnity package artifact was not produced: $artifact"
}

Write-Host "EveUnity package: $artifact"

