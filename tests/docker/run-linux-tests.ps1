<#
.SYNOPSIS
  Builds the Linux test rig image and runs CodeMap's storage / lock / concurrency suites in it
  (PHASE-21-04 T01). Requires Docker Desktop (Linux containers) running.

.EXAMPLE
  .\tests\docker\run-linux-tests.ps1                      # all suites
  .\tests\docker\run-linux-tests.ps1 storage mcp          # selected suites
  .\tests\docker\run-linux-tests.ps1 storage -- --filter-class CodeMap.Storage.Engine.Tests.OverlayWriterExclusionTests
#>
# Plain $args (not a param block): suite names and a literal "--" pass straight through to the
# container, which a bound parameter would not guarantee under `powershell -File`.
# @(...) keeps a single argument an array — otherwise splatting "all" passes 'a','l','l'.
$Suites = @(if ($args.Count -gt 0) { $args } else { 'all' })

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = Join-Path ([IO.Path]::GetTempPath()) 'codemap-linux-out'
$image = 'codemap-linux-tests'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory $out | Out-Null

docker build -f (Join-Path $repo 'tests\docker\linux-tests.Dockerfile') -t $image $repo
if ($LASTEXITCODE -ne 0) { throw "docker build failed ($LASTEXITCODE)" }

docker run --rm -v "${out}:/out" $image @Suites
$rc = $LASTEXITCODE
Write-Host "Logs: $out"
exit $rc
