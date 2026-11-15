$ErrorActionPreference = 'Stop'
$target = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\ClipDock'))
if ([System.IO.Path]::GetFullPath($PSScriptRoot) -ne $target) { throw 'Run the installed copy in LocalAppData\Programs\ClipDock.' }
if (Get-Process -Name ClipDock,ClipDock.NativeHost -ErrorAction SilentlyContinue) { throw 'Close ClipDock first.' }
$nativeKey = 'Software\Google\Chrome\NativeMessagingHosts\com.clipdock.downloader'
$key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($nativeKey)
if ($null -ne $key) {
    try { $manifestPath = $key.GetValue('') }
    finally { $key.Dispose() }
    if ($manifestPath -is [string] -and (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        try {
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            if ($manifest.path -is [string] -and [System.IO.Path]::GetFullPath($manifest.path) -eq (Join-Path $target 'ClipDock.NativeHost.exe')) {
                [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($nativeKey, $false)
            }
        }
        catch { Write-Warning 'The Chrome registration could not be read and was retained.' }
    }
}
$shortcutShell = New-Object -ComObject WScript.Shell
foreach ($folder in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    $shortcutPath = Join-Path $folder 'ClipDock.lnk'
    if ((Test-Path -LiteralPath $shortcutPath) -and $shortcutShell.CreateShortcut($shortcutPath).TargetPath -eq (Join-Path $target 'ClipDock.exe')) {
        Remove-Item -LiteralPath $shortcutPath -Force
    }
}
# Release both working directories before removing the installed folder.
$elsewhere = [System.IO.Path]::GetTempPath()
Set-Location -LiteralPath $elsewhere
[Environment]::CurrentDirectory = $elsewhere
Remove-Item -LiteralPath $target -Recurse -Force
Write-Host 'ClipDock removed. Remove its Chrome extension from chrome://extensions.'
Write-Host 'Downloaded videos and LocalAppData\ClipDock settings/history were retained.'
