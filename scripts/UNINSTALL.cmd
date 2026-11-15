@echo off
rem Keep everything below on ONE line. Uninstall.ps1 deletes this folder, this file included, while it runs:
rem (goto) ends batch mode so cmd never reads this file again, and cd leaves the folder so it is not locked.
(goto) 2>nul & cd /d "%TEMP%" & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall.ps1" & pause
