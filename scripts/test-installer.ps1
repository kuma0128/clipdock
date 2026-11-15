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

$version = [string]([xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
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

function Set-LegacyRegistration([string]$ManifestPath, [string]$HostPath) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $ManifestPath) -Force | Out-Null
    @{ name = 'com.clipdock.downloader'; type = 'stdio'; path = $HostPath } |
        ConvertTo-Json | Set-Content -LiteralPath $ManifestPath -Encoding utf8
    $key = $registry.CreateSubKey($nativeKey)
    try { $key.SetValue('', $ManifestPath) }
    finally { $key.Dispose() }
}

function Assert-Installed {
    foreach ($file in @('ClipDock.exe', 'ClipDock.dll', 'ClipDock.Core.dll', 'PRIVACY.md')) {
        Assert-True (Test-Path -LiteralPath (Join-Path $installRoot $file) -PathType Leaf) "Installed file missing: $file"
    }
    foreach ($file in @('Install.ps1', 'Uninstall.ps1', 'INSTALL.cmd', 'UNINSTALL.cmd', '.clipdock-package.json',
        'ClipDock.NativeHost.exe', 'ClipDock.NativeHost.dll', 'ClipDock.NativeHost.deps.json', 'ClipDock.NativeHost.runtimeconfig.json', 'extension')) {
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $installRoot $file))) "Legacy installer remains: $file"
    }
    Assert-True (Test-RegistryKey $registry $uninstallKey) 'Per-user uninstall registration is missing.'
    Assert-True (-not (Test-RegistryKey $machineRegistry $uninstallKey)) 'Installer unexpectedly registered a machine-wide uninstall entry.'
    $shell = New-Object -ComObject WScript.Shell
    foreach ($path in @($programShortcut, $desktopShortcut)) {
        Assert-True (Test-Path -LiteralPath $path -PathType Leaf) "Missing shortcut: $path"
        Assert-True ($shell.CreateShortcut($path).TargetPath -eq (Join-Path $installRoot 'ClipDock.exe')) "Shortcut points outside the installed app: $path"
    }
    Assert-AppClosed
}

function Invoke-Uninstall([string]$Step) {
    $uninstallers = @(Get-ChildItem -LiteralPath $installRoot -Filter 'unins*.exe' -File)
    Assert-True ($uninstallers.Count -eq 1) 'Expected one registered uninstaller after installation/update.'
    Invoke-Setup $uninstallers[0].FullName $Step -Uninstall
    $cleanupDeadline = [DateTime]::UtcNow.AddSeconds(15)
    while (@(Get-ChildItem -LiteralPath $installRoot -Filter 'unins*.exe' -File -ErrorAction SilentlyContinue).Count -gt 0) {
        if ([DateTime]::UtcNow -ge $cleanupDeadline) { throw 'Uninstaller did not finish removing its bootstrap executable.' }
        Start-Sleep -Milliseconds 100
    }
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
    Assert-True (-not (Test-RegistryKey $registry $nativeKey) -and -not (Test-Path -LiteralPath $nativeManifest)) 'Standalone install created browser integration.'
    Assert-Preserved 'install'
    Write-Output 'PASS silent per-user standalone installation and shortcuts'

    Preserve-Fixture (Join-Path $installRoot 'user-video.mp4') ([Text.Encoding]::UTF8.GetBytes('user video saved inside the application folder'))
    foreach ($file in @('Install.ps1', 'Uninstall.ps1', 'INSTALL.cmd', 'UNINSTALL.cmd')) {
        Set-Content -LiteralPath (Join-Path $installRoot $file) -Value 'legacy installer fixture'
    }
    foreach ($file in @('ClipDock.NativeHost.exe', 'ClipDock.NativeHost.dll', 'ClipDock.NativeHost.deps.json', 'ClipDock.NativeHost.runtimeconfig.json',
        'extension\manifest.json', 'extension\background.js', 'extension\icons\16.png', 'docs\webstore-review.md')) {
        $path = Join-Path $installRoot $file
        New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
        Set-Content -LiteralPath $path -Value 'retired browser integration fixture'
    }
    Set-LegacyRegistration $nativeManifest (Join-Path $installRoot 'ClipDock.NativeHost.exe')
    Set-Content -LiteralPath (Join-Path $installRoot 'README.md') -Value 'old application file'
    Invoke-Setup $InstallerPath '02-update'
    Assert-Installed
    Assert-True (-not (Test-RegistryKey $registry $nativeKey) -and -not (Test-Path -LiteralPath $nativeManifest)) 'Update retained its owned legacy native registration.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $installRoot 'docs\webstore-review.md'))) 'Update retained a retired store document.'
    Assert-True ((Get-FileHash -LiteralPath (Join-Path $installRoot 'README.md')).Hash -eq
        (Get-FileHash -LiteralPath (Join-Path $root 'artifacts\ClipDock-win-x64\README.md')).Hash) 'Update did not replace the old application file.'
    Assert-Preserved 'update'
    Write-Output 'PASS update retires owned native registration, extension files and legacy uninstall scripts'

    Invoke-Uninstall '03-uninstall-owned'
    Assert-True (-not (Test-RegistryKey $registry $nativeKey) -and -not (Test-Path -LiteralPath $nativeManifest)) 'Uninstall did not remove its own native registration and manifest.'
    Write-Output 'PASS owned native registration removed'

    $alternateRoot = Join-Path $env:RUNNER_TEMP ('clipdock-alternate-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $alternateRoot | Out-Null
    foreach ($scenario in @('changed-host-path', 'changed-manifest-path')) {
        Invoke-Setup $InstallerPath ("04-install-$scenario")
        Assert-Installed
        $registeredManifest = if ($scenario -eq 'changed-host-path') { $nativeManifest } else { Join-Path $alternateRoot 'native-host.json' }
        Set-LegacyRegistration $registeredManifest (Join-Path $alternateRoot 'ClipDock.NativeHost.exe')
        $otherManifestHash = (Get-FileHash -LiteralPath $registeredManifest).Hash
        Invoke-Setup $InstallerPath ("05-update-$scenario")
        Assert-Installed
        Assert-True ((Read-NativeRegistration) -eq $registeredManifest -and
            (Get-FileHash -LiteralPath $registeredManifest).Hash -eq $otherManifestHash) 'Update changed another installation''s native registration.'
        Invoke-Uninstall ("05-uninstall-$scenario")
        Assert-True ((Read-NativeRegistration) -eq $registeredManifest -and (Test-Path -LiteralPath $registeredManifest)) 'Uninstall removed another installation''s native registration.'
        Assert-True ((Get-FileHash -LiteralPath $registeredManifest).Hash -eq $otherManifestHash) 'Uninstall changed another installation''s native manifest.'
        Write-Output "PASS update and uninstall retain another installation's registration: $scenario"
    }
    $portable = Join-Path $root 'artifacts\ClipDock-win-x64'
    & (Join-Path $portable 'Install.ps1') -NoLaunch
    Assert-AppClosed
    Assert-True (Test-Path -LiteralPath (Join-Path $installRoot '.clipdock-package.json')) 'ZIP install omitted its owned file inventory.'
    Assert-True ((Read-NativeRegistration) -eq $registeredManifest) 'ZIP installation changed a foreign native registration.'
    $editedReadme = Join-Path $installRoot 'README.md'
    Set-Content -LiteralPath $editedReadme -Value 'user modified document'
    $preserved[$editedReadme] = (Get-FileHash -LiteralPath $editedReadme).Hash
    & (Join-Path $installRoot 'Uninstall.ps1')
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $installRoot 'ClipDock.exe'))) 'ZIP uninstall retained its application executable.'
    Assert-True ((Read-NativeRegistration) -eq $registeredManifest -and
        (Get-FileHash -LiteralPath $registeredManifest).Hash -eq $otherManifestHash) 'ZIP uninstall changed a foreign native registration.'
    Assert-Preserved 'ZIP install/uninstall'
    Write-Output 'PASS ZIP installation and owned-file uninstall preserve user data and modified documents'
    Write-Output 'Installer lifecycle checks passed on the disposable GitHub-hosted runner.'
}
finally { $registry.Dispose(); $machineRegistry.Dispose() }
