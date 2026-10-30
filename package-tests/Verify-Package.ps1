param([string]$CacheSource)
$ErrorActionPreference = 'Stop'
$solutionRoot = Split-Path $PSScriptRoot -Parent
$library = Join-Path $solutionRoot 'GraphFlow.Hosting.AspNetCore/GraphFlow.Hosting.AspNetCore.csproj'
$consumer = Join-Path $PSScriptRoot 'PackageConsumer.csproj'
$artifacts = Join-Path $solutionRoot 'artifacts'
# Use a fresh cache so a previously packed preview version cannot mask changes.
$cache = Join-Path $artifacts ('consumer-cache-' + [Guid]::NewGuid().ToString('N'))
$sources = @('--source', $artifacts, '--source', (Join-Path $solutionRoot 'packages'))
if ($CacheSource) { $sources += @('--source', $CacheSource) }
else { $sources += @('--source', 'https://api.nuget.org/v3/index.json') }
dotnet pack $library --no-restore -c Release -o $artifacts
if ($LASTEXITCODE) { throw 'Packing failed. Restore the solution first.' }
dotnet restore $consumer --configfile (Join-Path $solutionRoot 'NuGet.Config') @sources --packages $cache -p:NuGetAudit=false
if ($LASTEXITCODE) { throw 'Package consumer restore failed.' }
dotnet run --project $consumer --no-restore --no-launch-profile
if ($LASTEXITCODE) { throw 'Package consumer validation failed.' }
