$ErrorActionPreference = 'Stop'
$target = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\ClipDock'))
if ([System.IO.Path]::GetFullPath($PSScriptRoot) -ne $target) { throw 'Run the installed copy in LocalAppData\Programs\ClipDock.' }
if (Get-Process -Name ClipDock,ClipDock.NativeHost -ErrorAction SilentlyContinue) { throw 'Close ClipDock first.' }
$nativeKey = 'Software\Google\Chrome\NativeMessagingHosts\com.clipdock.downloader'
[Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey($nativeKey, $false)
$shortcutShell = New-Object -ComObject WScript.Shell
foreach ($folder in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    $shortcutPath = Join-Path $folder 'ClipDock.lnk'
    if ((Test-Path -LiteralPath $shortcutPath) -and $shortcutShell.CreateShortcut($shortcutPath).TargetPath -eq (Join-Path $target 'ClipDock.exe')) {
        Remove-Item -LiteralPath $shortcutPath -Force
    }
}
# A folder cannot be removed while it is a working directory, and "Run with PowerShell" starts inside it.
# Set-Location moves PowerShell; the process itself keeps its own copy, so move that as well.
$elsewhere = [System.IO.Path]::GetTempPath()
Set-Location -LiteralPath $elsewhere
[Environment]::CurrentDirectory = $elsewhere
# Exact target validated above; never enumerate paths in another shell for deletion.
Remove-Item -LiteralPath $target -Recurse -Force
Write-Host 'ClipDock removed. Remove its Chrome extension from chrome://extensions.'
Write-Host 'Downloaded videos and LocalAppData\ClipDock settings/history were retained.'
