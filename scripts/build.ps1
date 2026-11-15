param([switch]$BundleTools, [switch]$Zip, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'artifacts\ClipDock-win-x64'
Push-Location $root
try {
    dotnet build ClipDock.slnx -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        dotnet run --project tests/ClipDock.Checks -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw '.NET checks failed.' }
        npm.cmd test
        if ($LASTEXITCODE -ne 0) { throw 'Extension checks failed.' }
        npm.cmd run check
        if ($LASTEXITCODE -ne 0) { throw 'JavaScript syntax check failed.' }
    }
    # Start from an empty folder: leftovers of an earlier build (a bundled tools folder, files that
    # no longer exist in the source) would otherwise be shipped in this package.
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    dotnet publish src/ClipDock.App -c Release -r win-x64 --self-contained true -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
    dotnet publish src/ClipDock.NativeHost -c Release -r win-x64 --self-contained true -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Native host publish failed.' }
    Copy-Item -LiteralPath (Join-Path $root 'extension') -Destination $output -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $output -Recurse -Force
    foreach ($name in @('README.md', 'THIRD-PARTY.md', 'LICENSE', 'START-HERE.html')) {
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination $output -Force
    }
    foreach ($name in @('Install.ps1', 'Uninstall.ps1', 'INSTALL.cmd', 'UNINSTALL.cmd')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $output -Force
    }
    if ($BundleTools) {
        & (Join-Path $output 'ClipDock.NativeHost.exe') --install-tools (Join-Path $output 'tools')
        if ($LASTEXITCODE -ne 0) { throw 'Tool installation failed.' }
    }
    node scripts/check-package.mjs $output
    if ($LASTEXITCODE -ne 0) { throw 'Packaged native host checks failed.' }
    if ($Zip) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = Join-Path $root 'artifacts\ClipDock-win-x64.zip'
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
        [System.IO.Compression.ZipFile]::CreateFromDirectory($output, $archive, [System.IO.Compression.CompressionLevel]::Optimal, $true)
        Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Select-Object Hash, Path
    }
    Write-Output "Ready: $output"
}
finally { Pop-Location }
