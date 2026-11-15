#Requires -Version 7.0
param(
    [string]$PackageDirectory,
    [string]$CompilerPath,
    [string]$SignToolPath,
    [string]$CertificateThumbprint,
    [ValidateSet('CurrentUser', 'LocalMachine')][string]$CertificateStoreLocation = 'CurrentUser',
    [uri]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$AllowUnsignedDevelopment,
    [switch]$AllowUnsignedRelease
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
$installerRoot = [System.IO.Path]::GetFullPath((Join-Path $artifactRoot 'installer'))
$ownBinaries = @('ClipDock.exe', 'ClipDock.dll', 'ClipDock.Core.dll')

function Invoke-SigningHelper([string]$Path, [switch]$VerifyOnly) {
    $arguments = @('-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'sign-artifact.ps1'),
        '-ConfigurationPath', $script:signingConfiguration, '-FilePath', $Path)
    if ($VerifyOnly) { $arguments += '-VerifyOnly' }
    & $script:powershellPath @arguments
    if ($LASTEXITCODE -ne 0) { throw "Signature check failed for $Path." }
}

function Inno-Quoted([string]$Value) {
    if ($Value.Contains('"') -or $Value.Contains("`r") -or $Value.Contains("`n")) { throw 'Invalid signing command path.' }
    return '$q' + $Value.Replace('$', '$$') + '$q'
}

try {
    if (-not $IsWindows) { throw 'The Windows installer must be built on Windows with PowerShell 7.' }
    if ($AllowUnsignedDevelopment -and $AllowUnsignedRelease) {
        throw 'Unsigned development and unsigned release modes are mutually exclusive.'
    }
    $unsigned = $AllowUnsignedDevelopment -or $AllowUnsignedRelease
    if (-not $unsigned -and [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        throw 'A signed release requires -CertificateThumbprint. Use -AllowUnsignedRelease only for an explicitly approved unsigned public release, or -AllowUnsignedDevelopment for local installer testing.'
    }
    if ($unsigned -and $CertificateThumbprint) {
        throw 'A signing certificate cannot be combined with an unsigned build mode.'
    }
    if (-not $PackageDirectory) { $PackageDirectory = Join-Path $artifactRoot 'ClipDock-win-x64' }
    $PackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
    $sourceFiles = @(Get-ChildItem -LiteralPath $PackageDirectory -Force -Recurse)
    if ((Get-Item -LiteralPath $PackageDirectory).Attributes -band [System.IO.FileAttributes]::ReparsePoint -or
        $sourceFiles.Where({ $_.Attributes -band [System.IO.FileAttributes]::ReparsePoint }).Count -gt 0) {
        throw 'The distribution must not contain symbolic links or junctions.'
    }
    foreach ($name in @($ownBinaries) + @('ClipDock.deps.json', 'ClipDock.runtimeconfig.json',
        'coreclr.dll', 'LICENSE', 'THIRD-PARTY.md', 'PRIVACY.md', 'START-HERE.html')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PackageDirectory $name) -PathType Leaf)) { throw "Incomplete distribution: $name. Run scripts/build.ps1 first." }
    }
    foreach ($name in @('docs', 'licenses')) {
        if (-not (Test-Path -LiteralPath (Join-Path $PackageDirectory $name) -PathType Container)) { throw "Incomplete distribution: $name." }
    }
    if ((Test-Path -LiteralPath (Join-Path $PackageDirectory 'extension')) -or
        @(Get-ChildItem -LiteralPath $PackageDirectory -Filter 'ClipDock.NativeHost.*' -File).Count -ne 0) {
        throw 'The distribution contains retired browser integration files. Rebuild it first.'
    }
    $version = [string]([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'A three-component release version is required.' }
    foreach ($name in $ownBinaries) {
        $binaryVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $PackageDirectory $name))
        if ("$($binaryVersion.FileMajorPart).$($binaryVersion.FileMinorPart).$($binaryVersion.FileBuildPart)" -ne $version) {
            throw "Version mismatch in $name; rebuild the distribution."
        }
    }
    if (-not $CompilerPath) {
        $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($compilerCommand) { $CompilerPath = $compilerCommand.Source }
        else {
            $CompilerPath = @((Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
                (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe')) |
                Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        }
    }
    if (-not $CompilerPath) { throw 'Inno Setup 7.1.1 or later is required. Install it from jrsoftware.org, then pass -CompilerPath if ISCC.exe is not on PATH.' }
    $CompilerPath = (Resolve-Path -LiteralPath $CompilerPath).Path
    $compilerVersionText = (& $CompilerPath --version | Out-String).Trim()
    $compilerVersion = $null
    if ($LASTEXITCODE -ne 0 -or -not [version]::TryParse($compilerVersionText, [ref]$compilerVersion) -or
        $compilerVersion.Major -ne 7 -or $compilerVersion -lt [version]'7.1.1') {
        throw 'Use Inno Setup 7.1.1 or later in the 7.x series.'
    }

    if (-not $unsigned) {
        $CertificateThumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
        if ($CertificateThumbprint -notmatch '^[A-F0-9]{40}$') { throw 'Specify the SHA-1 thumbprint of the public code-signing certificate.' }
        if ($TimestampUrl.Scheme -notin @('http', 'https') -or $TimestampUrl.UserInfo -or -not $TimestampUrl.Host) { throw 'Specify an HTTP(S) RFC 3161 timestamp service.' }
        if (-not $SignToolPath) {
            $signToolCommand = Get-Command signtool.exe -ErrorAction SilentlyContinue
            if ($signToolCommand) { $SignToolPath = $signToolCommand.Source }
        }
        if (-not $SignToolPath) { throw 'Microsoft SignTool is required for a release. Pass -SignToolPath from the Windows SDK.' }
        $SignToolPath = (Resolve-Path -LiteralPath $SignToolPath).Path
        $certificate = Get-Item -LiteralPath "Cert:\$CertificateStoreLocation\My\$CertificateThumbprint"
        if (-not $certificate.HasPrivateKey -or $certificate.Subject -eq $certificate.Issuer) { throw 'A CA-issued signing certificate with an accessible private key is required. Self-signed certificates are not supported.' }
    }

    New-Item -ItemType Directory -Path $installerRoot -Force | Out-Null
    $workDirectory = Join-Path $installerRoot ('work-' + [Guid]::NewGuid().ToString('N'))
    $payload = Join-Path $workDirectory 'payload'
    $compiled = Join-Path $workDirectory 'compiled'
    New-Item -ItemType Directory -Path $payload, $compiled -Force | Out-Null
    Get-ChildItem -LiteralPath $PackageDirectory -Force | Copy-Item -Destination $payload -Recurse -Force
    $excluded = @('Install.ps1', 'Uninstall.ps1', 'INSTALL.cmd', 'UNINSTALL.cmd', '.clipdock-package.json')
    foreach ($name in $excluded) {
        $path = Join-Path $payload $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    foreach ($file in Get-ChildItem -LiteralPath $payload -Filter '*.pdb' -File -Recurse) {
        Remove-Item -LiteralPath $file.FullName -Force
    }

    $compilerArguments = @('--no-ide-signtools', '--quiet-progress', "/DPackageDirectory=$payload", "/DAppVersion=$version", "/DInstallerOutput=$compiled")
    if ($AllowUnsignedDevelopment) {
        $compilerArguments += '/DUnsignedDevelopment=1'
        $baseName = "ClipDock-$version-win-x64-unsigned-dev-setup"
        $mode = 'development'
        Write-Warning 'Building an unsigned development installer. Do not publish it as a signed release.'
    }
    elseif ($AllowUnsignedRelease) {
        $compilerArguments += '/DUnsignedRelease=1'
        $baseName = "ClipDock-$version-win-x64-unsigned-setup"
        $mode = 'release'
        Write-Warning 'Building the explicitly requested unsigned public installer. Windows may display publisher or SmartScreen warnings.'
    }
    else {
        $script:powershellPath = (Get-Process -Id $PID).Path
        $script:signingConfiguration = Join-Path $workDirectory 'signing.json'
        @{
            signToolPath = $SignToolPath
            certificateThumbprint = $CertificateThumbprint
            certificateStoreLocation = $CertificateStoreLocation
            timestampUrl = $TimestampUrl.AbsoluteUri
            evidenceDirectory = Join-Path $workDirectory 'signature-evidence'
        } | ConvertTo-Json | Set-Content -LiteralPath $script:signingConfiguration -Encoding utf8
        foreach ($name in $ownBinaries) { Invoke-SigningHelper (Join-Path $payload $name) }
        $signingCommand = (Inno-Quoted $script:powershellPath) + ' -NoProfile -NonInteractive -File ' +
            (Inno-Quoted (Join-Path $PSScriptRoot 'sign-artifact.ps1')) + ' -ConfigurationPath ' +
            (Inno-Quoted $script:signingConfiguration) + ' -FilePath $f'
        $compilerArguments += '--signtool=clipdock=' + $signingCommand
        $baseName = "ClipDock-$version-win-x64-setup"
        $mode = 'release'
    }
    $compilerArguments += Join-Path $PSScriptRoot 'ClipDock.iss'
    & $CompilerPath @compilerArguments
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed (exit $LASTEXITCODE)." }
    $setup = Join-Path $compiled ($baseName + '.exe')
    if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw 'The compiler did not produce the expected installer.' }
    if (-not $unsigned) {
        foreach ($name in $ownBinaries) { Invoke-SigningHelper (Join-Path $payload $name) -VerifyOnly }
        $uninstallers = @(Get-ChildItem -LiteralPath (Join-Path $workDirectory 'signature-evidence') -Filter 'uninstaller-*.exe' -File)
        if ($uninstallers.Count -eq 0) { throw 'No signed uninstaller was produced.' }
        foreach ($file in $uninstallers) { Invoke-SigningHelper $file.FullName -VerifyOnly }
        Invoke-SigningHelper $setup -VerifyOnly
    }
    $destination = Join-Path $installerRoot $mode
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $final = Join-Path $destination ($baseName + '.exe')
    Copy-Item -LiteralPath $setup -Destination $final -Force
    $hash = (Get-FileHash -LiteralPath $final -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $baseName.exe" | Set-Content -LiteralPath ($final + '.sha256') -Encoding ascii
    Write-Output "Ready ($mode): $final"
    Write-Output "SHA256: $hash"
}
catch {
    Write-Error $_
    exit 1
}
