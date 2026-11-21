#!/usr/bin/env bash
set -euo pipefail
repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
dotnet pack "$repo_dir/src/Umbraco.Search.Postgres.csproj" --configuration Release --nologo
