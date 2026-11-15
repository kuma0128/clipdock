param([switch]$BundleTools, [switch]$Zip, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
$output = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot 'ClipDock-win-x64'))
if ([System.IO.Path]::GetDirectoryName($output) -ne $artifactRoot) { throw 'Package output must stay inside artifacts.' }
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
    # Discard files left by previous packages.
    if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    dotnet publish src/ClipDock.App -c Release -r win-x64 --self-contained true -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
    dotnet publish src/ClipDock.NativeHost -c Release -r win-x64 --self-contained true -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Native host publish failed.' }
    $runtime = Get-Content -LiteralPath (Join-Path $output 'ClipDock.runtimeconfig.json') -Raw | ConvertFrom-Json
    $assets = Get-Content -LiteralPath (Join-Path $root 'src\ClipDock.App\obj\project.assets.json') -Raw | ConvertFrom-Json
    foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
        $packageId = ($framework.name + '.Runtime.win-x64').ToLowerInvariant()
        $package = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path (Join-Path $folder $packageId) $framework.version
            if (Test-Path -LiteralPath $candidate -PathType Container) { $package = $candidate; break }
        }
        if ($null -eq $package) { throw "Runtime package not found: $packageId/$($framework.version)" }
        $license = @('LICENSE.TXT', 'LICENSE') | ForEach-Object { Join-Path $package $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if ($null -eq $license) { throw "Runtime license not found: $packageId/$($framework.version)" }
        $licenseOutput = Join-Path (Join-Path (Join-Path $output 'licenses\dotnet') $framework.name) $framework.version
        New-Item -ItemType Directory -Path $licenseOutput -Force | Out-Null
        Copy-Item -LiteralPath $license -Destination $licenseOutput -Force
        $notices = Join-Path $package 'THIRD-PARTY-NOTICES.TXT'
        if (Test-Path -LiteralPath $notices -PathType Leaf) { Copy-Item -LiteralPath $notices -Destination $licenseOutput -Force }
        elseif ($framework.name -eq 'Microsoft.NETCore.App') { throw "Runtime third-party notices not found: $packageId/$($framework.version)" }
    }
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
