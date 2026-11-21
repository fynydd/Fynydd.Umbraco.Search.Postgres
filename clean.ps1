$ErrorActionPreference = 'Stop'
foreach ($relativePath in @('src/bin', 'src/obj', 'tests/bin', 'tests/obj')) {
    $buildPath = Join-Path $PSScriptRoot $relativePath
    if (Test-Path $buildPath) { Remove-Item $buildPath -Recurse -Force }
}
