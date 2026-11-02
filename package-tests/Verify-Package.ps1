param([string]$CacheSource)
$ErrorActionPreference = 'Stop'
$solutionRoot = Split-Path $PSScriptRoot -Parent
$library = Join-Path $solutionRoot 'GraphFlow.Hosting.AspNetCore/GraphFlow.Hosting.AspNetCore.csproj'
$consumer = Join-Path $PSScriptRoot 'PackageConsumer.csproj'
$artifacts = Join-Path $solutionRoot 'artifacts'
# Use a fresh cache so a previously packed preview version cannot mask changes.
$cache = Join-Path $artifacts ('consumer-cache-' + [Guid]::NewGuid().ToString('N'))
$additionalSources = $artifacts
if ($CacheSource) { $additionalSources += "%3B$CacheSource" }
dotnet pack $library --no-restore -c Release -o $artifacts
if ($LASTEXITCODE) { throw 'Packing failed. Restore the solution first.' }
dotnet restore $consumer "-p:RestoreAdditionalProjectSources=$additionalSources" --packages $cache -p:NuGetAudit=false
if ($LASTEXITCODE) { throw 'Package consumer restore failed.' }
dotnet run --project $consumer --no-restore --no-launch-profile
if ($LASTEXITCODE) { throw 'Package consumer validation failed.' }
