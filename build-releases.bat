@echo off
setlocal
chcp 65001 >nul

set "SCRIPT=%~dp0release\Build-Releases.ps1"
if not exist "%SCRIPT%" (
  echo ERRO: script de release não encontrado: %SCRIPT%
  exit /b 1
)

if "%~1"=="" (
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%"
) else (
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" -Version "%~1"
)

if errorlevel 1 (
  echo.
  echo A geração das releases falhou.
  exit /b 1
)

echo.
echo Releases geradas com sucesso.
exit /b 0
