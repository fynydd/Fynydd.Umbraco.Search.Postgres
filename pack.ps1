$ErrorActionPreference = 'Stop'
dotnet pack (Join-Path $PSScriptRoot 'src/Umbraco.Search.Postgres.csproj') --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
