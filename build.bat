@echo off
:: ================================================================
:: Launcher for build.ps1
::
:: This file exists only so the build stays double-clickable. All of
:: the actual work lives in build.ps1 next to it.
::
:: -ExecutionPolicy Bypass is needed because a freshly installed
:: Windows refuses to run unsigned .ps1 files, and -NoProfile keeps a
:: developer's PowerShell profile from altering the build environment.
::
:: Arguments are forwarded, so these work as expected:
::   build.bat -Clean
::   build.bat -Run
::   build.bat -Configuration Debug -NoInstall
::   build.bat -Preset ninja-x64 -NonInteractive
:: ================================================================

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
exit /b %ERRORLEVEL%
