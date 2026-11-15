$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\ClipDock'))
if ([IO.Path]::GetFullPath($PSScriptRoot) -ne $target) { throw 'Run the installed copy in LocalAppData\Programs\ClipDock.' }
if (Get-Process -Name ClipDock,ClipDock.NativeHost -ErrorAction SilentlyContinue) { throw 'Close ClipDock first.' }
if (@(Get-ChildItem -LiteralPath $target -Filter 'unins*.exe' -File).Count) { throw 'Use the registered EXE uninstaller for this installation.' }
$inventory = Join-Path $target '.clipdock-package.json'
$package = Get-Content -LiteralPath $inventory -Raw | ConvertFrom-Json
$owned = @()
foreach ($file in $package.files) {
    if ($file.path -isnot [string] -or [IO.Path]::IsPathRooted($file.path) -or $file.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Invalid package inventory.' }
    $path = [IO.Path]::GetFullPath((Join-Path $target $file.path))
    if (-not $path.StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Package inventory escapes the installation directory.' }
    $ancestor = $path
    while ($ancestor.Length -ge $target.Length) {
        if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'The installation contains a symbolic link or junction. No files were removed.'
        }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    $owned += @{ path = $path; sha256 = $file.sha256 }
}
$nativeKey = 'Software\Google\Chrome\NativeMessagingHosts\com.clipdock.downloader'
function Test-OwnedManifest([string]$Path) {
    try {
        $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        return $manifest.path -is [string] -and [IO.Path]::IsPathRooted($manifest.path) -and
            [IO.Path]::GetFullPath($manifest.path) -eq (Join-Path $target 'ClipDock.NativeHost.exe')
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
$shortcutShell = New-Object -ComObject WScript.Shell
foreach ($folder in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
    $shortcutPath = Join-Path $folder 'ClipDock.lnk'
    if ((Test-Path -LiteralPath $shortcutPath) -and $shortcutShell.CreateShortcut($shortcutPath).TargetPath -eq (Join-Path $target 'ClipDock.exe')) {
        Remove-Item -LiteralPath $shortcutPath -Force
    }
}
$elsewhere = [IO.Path]::GetTempPath()
Set-Location -LiteralPath $elsewhere
[Environment]::CurrentDirectory = $elsewhere
foreach ($file in $owned) {
    if ((Test-Path -LiteralPath $file.path -PathType Leaf) -and (Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash -eq $file.sha256) {
        Remove-Item -LiteralPath $file.path -Force
    }
}
Remove-Item -LiteralPath $inventory -Force
$directories = @($owned | ForEach-Object { [IO.Path]::GetDirectoryName($_.path) } | Sort-Object -Unique | Sort-Object Length -Descending)
foreach ($path in $directories) {
    if ((Test-Path -LiteralPath $path -PathType Container) -and @(Get-ChildItem -LiteralPath $path -Force).Count -eq 0) { Remove-Item -LiteralPath $path }
}
Write-Host 'ClipDock removed. Downloaded videos, engines, settings, history and modified files were retained.'
