param([switch]$NoLaunch)
$ErrorActionPreference = 'Stop'
try {
    $installRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\ClipDock'))
    $sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot)
    foreach ($folder in @($installRoot, (Join-Path $installRoot 'docs'), (Join-Path $installRoot 'licenses'),
        (Join-Path $installRoot 'extension'), (Join-Path $installRoot 'extension\icons'))) {
        if ((Test-Path -LiteralPath $folder) -and ((Get-Item -LiteralPath $folder -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'The installation directory contains a symbolic link or junction. No files were changed.'
        }
    }
    foreach ($required in @('ClipDock.exe', 'ClipDock.dll', 'ClipDock.deps.json', 'ClipDock.runtimeconfig.json',
        'ClipDock.Core.dll', 'coreclr.dll', 'START-HERE.html', 'LICENSE', 'THIRD-PARTY.md',
        'Uninstall.ps1', 'UNINSTALL.cmd', '.clipdock-package.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot $required) -PathType Leaf)) { throw "Incomplete distribution: $required. Extract the entire ZIP before running INSTALL.cmd." }
    }
    if (Get-Process -Name ClipDock,ClipDock.NativeHost -ErrorAction SilentlyContinue) { throw 'Quit ClipDock before installing or updating: right-click its notification-area icon and choose Quit ClipDock.' }
    if ((Test-Path -LiteralPath $installRoot) -and @(Get-ChildItem -LiteralPath $installRoot -Filter 'unins*.exe' -File).Count) {
        throw 'This copy was installed with the EXE installer. Use the latest setup EXE to update it.'
    }
    New-Item -ItemType Directory -Path $installRoot -Force | Out-Null
    if ($sourceRoot -ne $installRoot) { Get-ChildItem -LiteralPath $sourceRoot -Force | Copy-Item -Destination $installRoot -Recurse -Force }
    $shortcutShell = New-Object -ComObject WScript.Shell
    foreach ($folder in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
        $shortcut = $shortcutShell.CreateShortcut((Join-Path $folder 'ClipDock.lnk'))
        $shortcut.TargetPath = Join-Path $installRoot 'ClipDock.exe'
        $shortcut.WorkingDirectory = $installRoot
        $shortcut.Description = 'ClipDock - video and audio downloader'
        $shortcut.Save()
    }
    $nativeKey = 'Software\Google\Chrome\NativeMessagingHosts\com.clipdock.downloader'
    function Test-OwnedManifest([string]$Path) {
        try {
            $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
            return $manifest.path -is [string] -and [IO.Path]::IsPathRooted($manifest.path) -and
                [IO.Path]::GetFullPath($manifest.path) -eq (Join-Path $installRoot 'ClipDock.NativeHost.exe')
        } catch { return $false }
    }
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($nativeKey, $true)
    if ($null -ne $key) {
        try {
            $registered = $key.GetValue('')
            if ($registered -is [string] -and (Test-OwnedManifest $registered)) { $key.DeleteValue('', $false) }
            $empty = $key.ValueCount -eq 0 -and $key.SubKeyCount -eq 0
        } finally { $key.Dispose() }
        if ($empty) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($nativeKey, $false) }
    }
    $canonicalManifest = Join-Path $env:LOCALAPPDATA 'ClipDock\native-host.json'
    if (Test-OwnedManifest $canonicalManifest) { Remove-Item -LiteralPath $canonicalManifest -Force }
    $retired = @('ClipDock.NativeHost.exe', 'ClipDock.NativeHost.dll', 'ClipDock.NativeHost.deps.json',
        'ClipDock.NativeHost.runtimeconfig.json', 'ClipDock.NativeHost.pdb',
        'extension\background.js', 'extension\content.js', 'extension\distribution.js', 'extension\manifest.json',
        'extension\media.js', 'extension\popup.css', 'extension\popup.html', 'extension\popup.js', 'extension\site-policy.js',
        'extension\store-identity.json', 'extension\icons\16.png', 'extension\icons\32.png', 'extension\icons\48.png',
        'extension\icons\128.png', 'extension\icons\256.png', 'docs\webstore-listing-ja.md', 'docs\webstore-review.md')
    foreach ($name in $retired) {
        $path = Join-Path $installRoot $name
        if (Test-Path -LiteralPath $path -PathType Leaf) { Remove-Item -LiteralPath $path -Force }
    }
    foreach ($name in @('extension\icons', 'extension')) {
        $path = Join-Path $installRoot $name
        if ((Test-Path -LiteralPath $path -PathType Container) -and @(Get-ChildItem -LiteralPath $path -Force).Count -eq 0) { Remove-Item -LiteralPath $path }
    }
    Write-Host "Installed: $installRoot"
    if (-not $NoLaunch) { Start-Process -FilePath (Join-Path $installRoot 'ClipDock.exe') }
}
catch { Write-Error $_; exit 1 }
