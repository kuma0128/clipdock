param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
try {
    $installRoot = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\ClipDock'))
    $sourceRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
    foreach ($required in @('ClipDock.exe', 'ClipDock.dll', 'ClipDock.deps.json', 'ClipDock.runtimeconfig.json',
            'ClipDock.Core.dll', 'ClipDock.NativeHost.exe', 'ClipDock.NativeHost.dll', 'ClipDock.NativeHost.deps.json',
            'ClipDock.NativeHost.runtimeconfig.json', 'coreclr.dll', 'extension\manifest.json',
            'START-HERE.html', 'LICENSE', 'THIRD-PARTY.md', 'Uninstall.ps1', 'UNINSTALL.cmd')) {
        if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot $required) -PathType Leaf)) { throw "Incomplete distribution: $required. Extract the entire ZIP before running INSTALL.cmd." }
    }
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
        allowed_origins = @('chrome-extension://oeolbdjffnnmaanghklbkhfhgihepfhd/', 'chrome-extension://okehkphoehepefjddoiinohnhpjghefe/')
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
    Write-Host 'If ClipDock is installed from the Chrome Web Store, reopen the extension.'
    Write-Host 'Otherwise, load the extension folder below from chrome://extensions (Developer mode).'
    Write-Host (Join-Path $installRoot 'extension')
    if (-not $NoLaunch) {
        Start-Process -FilePath (Join-Path $installRoot 'ClipDock.exe')
        Start-Process -FilePath (Join-Path $installRoot 'START-HERE.html')
    }
}
catch { Write-Error $_; exit 1 }
