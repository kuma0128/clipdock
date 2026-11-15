#Requires -Version 7.0
param([string]$InstallerPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or
    $env:RUNNER_OS -cne 'Windows' -or [string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    throw 'Installer lifecycle tests may run only on a disposable GitHub-hosted Windows runner.'
}
$root = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($env:GITHUB_WORKSPACE) -or
    [System.IO.Path]::GetFullPath($env:GITHUB_WORKSPACE) -ne $root) {
    throw 'Run this test from the checked-out GitHub Actions workspace.'
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$version = [string](Get-Content -LiteralPath (Join-Path $root 'extension\manifest.json') -Raw | ConvertFrom-Json).version
$developmentOutput = Join-Path $root 'artifacts\installer\development'
if (-not $InstallerPath) { $InstallerPath = Join-Path $developmentOutput "ClipDock-$version-win-x64-unsigned-dev-setup.exe" }
$InstallerPath = (Resolve-Path -LiteralPath $InstallerPath).Path
Assert-True ($InstallerPath.StartsWith($developmentOutput + '\', [StringComparison]::OrdinalIgnoreCase) -and
    [System.IO.Path]::GetFileName($InstallerPath) -eq "ClipDock-$version-win-x64-unsigned-dev-setup.exe") 'Use the development installer from this workspace.'
Assert-True ((Get-AuthenticodeSignature -LiteralPath $InstallerPath).Status -eq 'NotSigned') 'This test expects an explicitly unsigned development build.'

$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\ClipDock'
$dataRoot = Join-Path $env:LOCALAPPDATA 'ClipDock'
$nativeManifest = Join-Path $dataRoot 'native-host.json'
$nativeKey = 'Software\Google\Chrome\NativeMessagingHosts\com.clipdock.downloader'
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{7AB34D02-4DD5-40C0-980E-432F864B41D4}_is1'
$programShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'ClipDock.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'ClipDock.lnk'
$registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
$machineRegistry = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
$logs = Join-Path $root 'artifacts\installer\smoke-logs'
$preserved = @{}

function Read-NativeRegistration {
    $key = $registry.OpenSubKey($nativeKey)
    if ($null -eq $key) { return $null }
    try { return $key.GetValue('') }
    finally { $key.Dispose() }
}

function Test-RegistryKey([Microsoft.Win32.RegistryKey]$Base, [string]$Name) {
    $key = $Base.OpenSubKey($Name)
    if ($null -eq $key) { return $false }
    $key.Dispose()
    return $true
}

function Assert-AppClosed {
    Assert-True (@(Get-Process -Name ClipDock, ClipDock.NativeHost -ErrorAction SilentlyContinue).Count -eq 0) 'The installer unexpectedly launched ClipDock or left a native host running.'
}

function Invoke-Setup([string]$Executable, [string]$Step, [switch]$Uninstall) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/LOG=`"$(Join-Path $logs ($Step + '.log'))`"")
    if (-not $Uninstall) { $arguments += @('/LANG=english', '/TASKS=desktopicon') }
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit(180000)) { throw "Installer timed out during $Step; see its log." }
        Assert-True ($process.ExitCode -eq 0) "Installer failed during $Step (exit $($process.ExitCode)); see its log."
    }
    finally { $process.Dispose() }
    Assert-AppClosed
}

function Preserve-Fixture([string]$Path, [byte[]]$Bytes) {
    Assert-True (-not (Test-Path -LiteralPath $Path)) "Refusing to overwrite an existing fixture path: $Path"
    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    [System.IO.File]::WriteAllBytes($Path, $Bytes)
    $preserved[$Path] = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Assert-Preserved([string]$Step) {
    foreach ($entry in $preserved.GetEnumerator()) {
        Assert-True (Test-Path -LiteralPath $entry.Key -PathType Leaf) "$Step removed user data: $($entry.Key)"
        Assert-True ((Get-FileHash -LiteralPath $entry.Key -Algorithm SHA256).Hash -eq $entry.Value) "$Step changed user data: $($entry.Key)"
    }
    Write-Output "PASS $Step preserves settings, history, engines and videos"
}

function Assert-NativePing([string]$Origin) {
    $start = [System.Diagnostics.ProcessStartInfo]::new((Join-Path $installRoot 'ClipDock.NativeHost.exe'))
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add($Origin)
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    $response = [System.IO.MemoryStream]::new()
    try {
        Assert-True ($process.Start()) 'Could not start the installed native host.'
        $body = [Text.Encoding]::UTF8.GetBytes('{"action":"ping"}')
        $header = [BitConverter]::GetBytes([int]$body.Length)
        $process.StandardInput.BaseStream.Write($header, 0, $header.Length)
        $process.StandardInput.BaseStream.Write($body, 0, $body.Length)
        $process.StandardInput.Close()
        Assert-True ($process.WaitForExit(30000)) 'The installed native host did not answer ping.'
        $process.StandardOutput.BaseStream.CopyTo($response)
        Assert-True ($process.ExitCode -eq 0) ('Installed native host rejected ping: ' + $process.StandardError.ReadToEnd())
        $bytes = $response.ToArray()
        Assert-True ($bytes.Length -ge 4) 'Native ping returned no frame.'
        $length = [BitConverter]::ToInt32($bytes, 0)
        Assert-True ($length -eq $bytes.Length - 4) 'Native ping returned an invalid frame.'
        $result = [Text.Encoding]::UTF8.GetString($bytes, 4, $length) | ConvertFrom-Json
        Assert-True ($result.ok -eq $true -and $result.version -eq $version) 'Installed native host version or health does not match the installer.'
    }
    finally { $response.Dispose(); $process.Dispose() }
}

function Assert-Installed {
    foreach ($file in @('ClipDock.exe', 'ClipDock.NativeHost.exe', 'ClipDock.Core.dll', 'extension\manifest.json', 'PRIVACY.md')) {
        Assert-True (Test-Path -LiteralPath (Join-Path $installRoot $file) -PathType Leaf) "Installed file missing: $file"
    }
    foreach ($file in @('Install.ps1', 'Uninstall.ps1', 'INSTALL.cmd', 'UNINSTALL.cmd')) {
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $installRoot $file))) "Legacy installer remains: $file"
    }
    Assert-True (Test-RegistryKey $registry $uninstallKey) 'Per-user uninstall registration is missing.'
    Assert-True (-not (Test-RegistryKey $machineRegistry $uninstallKey)) 'Installer unexpectedly registered a machine-wide uninstall entry.'
    Assert-True ((Read-NativeRegistration) -eq $nativeManifest) 'Chrome registration points to the wrong manifest.'
    $manifest = Get-Content -LiteralPath $nativeManifest -Raw | ConvertFrom-Json
    Assert-True ($manifest.name -eq 'com.clipdock.downloader' -and
        $manifest.path -eq (Join-Path $installRoot 'ClipDock.NativeHost.exe')) 'Native manifest points to the wrong installed host.'
    $expectedOrigins = @('chrome-extension://oeolbdjffnnmaanghklbkhfhgihepfhd/', 'chrome-extension://okehkphoehepefjddoiinohnhpjghefe/')
    Assert-True (@(Compare-Object ($manifest.allowed_origins | Sort-Object) ($expectedOrigins | Sort-Object)).Count -eq 0 -and
        $manifest.allowed_origins.Count -eq 2) 'Native manifest must allow only the two ClipDock identities.'
    $shell = New-Object -ComObject WScript.Shell
    foreach ($path in @($programShortcut, $desktopShortcut)) {
        Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Missing shortcut: $path"
        Assert-True ($shell.CreateShortcut($path).TargetPath -eq (Join-Path $installRoot 'ClipDock.exe')) "Shortcut points outside the installed app: $path"
    }
    foreach ($origin in $expectedOrigins) { Assert-NativePing $origin }
    Assert-AppClosed
}

function Invoke-Uninstall([string]$Step) {
    $uninstallers = @(Get-ChildItem -LiteralPath $installRoot -Filter 'unins*.exe' -File)
    Assert-True ($uninstallers.Count -eq 1) 'Expected one registered uninstaller after installation/update.'
    Invoke-Setup $uninstallers[0].FullName $Step -Uninstall
    foreach ($file in @('ClipDock.exe', 'ClipDock.NativeHost.exe', 'ClipDock.Core.dll')) {
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $installRoot $file))) "Uninstall left application file: $file"
    }
    Assert-True (-not (Test-RegistryKey $registry $uninstallKey)) 'Uninstall left its Add/Remove Programs registration.'
    Assert-True (-not (Test-Path -LiteralPath $programShortcut) -and -not (Test-Path -LiteralPath $desktopShortcut)) 'Uninstall left an owned shortcut.'
    Assert-Preserved $Step
}

try {
    Assert-AppClosed
    Assert-True (-not (Test-Path -LiteralPath $installRoot)) 'A ClipDock installation already exists on this runner.'
    Assert-True (-not (Test-RegistryKey $registry $nativeKey) -and -not (Test-RegistryKey $registry $uninstallKey)) 'ClipDock is already registered on this runner.'
    Assert-True (-not (Test-Path -LiteralPath $nativeManifest) -and -not (Test-Path -LiteralPath $programShortcut) -and
        -not (Test-Path -LiteralPath $desktopShortcut)) 'A ClipDock manifest or shortcut already exists on this runner.'
    New-Item -ItemType Directory -Path $logs -Force | Out-Null
    Preserve-Fixture (Join-Path $dataRoot 'settings.json') ([Text.Encoding]::UTF8.GetBytes('{"quality":"720","installerSmoke":"retain"}'))
    Preserve-Fixture (Join-Path $dataRoot 'history.dat') ([byte[]](0, 1, 2, 128, 255, 42))
    Preserve-Fixture (Join-Path $dataRoot 'tools\installer-smoke-engine.txt') ([Text.Encoding]::UTF8.GetBytes('existing engine'))
    Preserve-Fixture (Join-Path $env:USERPROFILE 'Downloads\ClipDock\installer-smoke-video.mp4') ([Text.Encoding]::UTF8.GetBytes('downloaded video fixture'))

    Invoke-Setup $InstallerPath '01-install'
    Assert-Installed
    Assert-Preserved 'install'
    Write-Output 'PASS silent per-user installation, shortcuts and both native origins'

    Preserve-Fixture (Join-Path $installRoot 'user-video.mp4') ([Text.Encoding]::UTF8.GetBytes('user video saved inside the application folder'))
    foreach ($file in @('Install.ps1', 'Uninstall.ps1', 'INSTALL.cmd', 'UNINSTALL.cmd')) {
        Set-Content -LiteralPath (Join-Path $installRoot $file) -Value 'legacy installer fixture'
    }
    Set-Content -LiteralPath (Join-Path $installRoot 'README.md') -Value 'old application file'
    Invoke-Setup $InstallerPath '02-update'
    Assert-Installed
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $installRoot 'README.md')).Hash -eq
        (Get-FileHash -LiteralPath (Join-Path $root 'artifacts\ClipDock-win-x64\README.md')).Hash) 'Update did not replace the old application file.'
    Assert-Preserved 'update'
    Write-Output 'PASS in-place update replaces application files and retires legacy uninstall scripts'

    Invoke-Uninstall '03-uninstall-owned'
    Assert-True (-not (Test-RegistryKey $registry $nativeKey) -and -not (Test-Path -LiteralPath $nativeManifest)) 'Uninstall did not remove its own native registration and manifest.'
    Write-Output 'PASS owned native registration removed'

    $alternateRoot = Join-Path $env:RUNNER_TEMP ('clipdock-alternate-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $alternateRoot | Out-Null
    foreach ($scenario in @('changed-host-path', 'changed-manifest-path')) {
        Invoke-Setup $InstallerPath ("04-install-$scenario")
        Assert-Installed
        $registeredManifest = if ($scenario -eq 'changed-host-path') { $nativeManifest } else { Join-Path $alternateRoot 'native-host.json' }
        @{ name = 'com.clipdock.downloader'; type = 'stdio'; allowed_origins = @('chrome-extension://okehkphoehepefjddoiinohnhpjghefe/');
            path = (Join-Path $alternateRoot 'ClipDock.NativeHost.exe') } | ConvertTo-Json | Set-Content -LiteralPath $registeredManifest -Encoding utf8
        $key = $registry.CreateSubKey($nativeKey)
        try { $key.SetValue('', $registeredManifest) }
        finally { $key.Dispose() }
        $otherManifestHash = (Get-FileHash -LiteralPath $registeredManifest).Hash
        Invoke-Uninstall ("05-uninstall-$scenario")
        Assert-True ((Read-NativeRegistration) -eq $registeredManifest -and (Test-Path -LiteralPath $registeredManifest)) 'Uninstall removed another installation''s native registration.'
        Assert-True ((Get-FileHash -LiteralPath $registeredManifest).Hash -eq $otherManifestHash) 'Uninstall changed another installation''s native manifest.'
        Write-Output "PASS uninstall retains another installation's registration: $scenario"
    }
    Write-Output 'Installer lifecycle checks passed on the disposable GitHub-hosted runner.'
}
finally { $registry.Dispose(); $machineRegistry.Dispose() }
