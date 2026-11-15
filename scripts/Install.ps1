param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
try {
    $installRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\ClipDock'))
    $sourceRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'ClipDock.exe'))) { throw 'Run INSTALL.cmd from the extracted distribution folder.' }
    $running = Get-Process -Name ClipDock,ClipDock.NativeHost -ErrorAction SilentlyContinue
    if ($running) { throw 'Close ClipDock before installing or updating.' }
    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    if ($sourceRoot -ne $installRoot) {
        Get-ChildItem -LiteralPath $sourceRoot | Copy-Item -Destination $installRoot -Recurse -Force
    }
    $dataRoot = Join-Path $env:LOCALAPPDATA 'ClipDock'
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    $manifestPath = Join-Path $dataRoot 'native-host.json'
    $manifest = @{
        name = 'com.clipdock.downloader'; description = 'ClipDock Chrome bridge'
        path = (Join-Path $installRoot 'ClipDock.NativeHost.exe'); type = 'stdio'
        allowed_origins = @('chrome-extension://oeolbdjffnnmaanghklbkhfhgihepfhd/')
    } | ConvertTo-Json
    [System.IO.File]::WriteAllText($manifestPath, $manifest, [System.Text.UTF8Encoding]::new($false))
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Google\Chrome\NativeMessagingHosts\com.clipdock.downloader')
    $key.SetValue('', $manifestPath); $key.Dispose()
    $shortcutShell = New-Object -ComObject WScript.Shell
    foreach ($folder in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
        $shortcut = $shortcutShell.CreateShortcut((Join-Path $folder 'ClipDock.lnk'))
        $shortcut.TargetPath = Join-Path $installRoot 'ClipDock.exe'
        $shortcut.WorkingDirectory = $installRoot
        $shortcut.Description = 'ClipDock - Chrome video downloader'
        $shortcut.Save()
    }
    Write-Host "Installed: $installRoot"
    Write-Host 'Load the extension folder below from chrome://extensions (Developer mode).'
    Write-Host (Join-Path $installRoot 'extension')
    if (-not $NoLaunch) {
        # These are the installer's intentional interactive UI, not background helpers.
        Start-Process -FilePath (Join-Path $installRoot 'ClipDock.exe')
        Start-Process -FilePath (Join-Path $installRoot 'START-HERE.html')
    }
}
catch { Write-Error $_; exit 1 }
