@echo off
REM Forge launcher. Clique DUPLO normal -- NAO "executar como administrador".
REM So o daemon eleva (UAC), a UI fica como usuario normal (modelo de seguranca).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-forge.ps1"
echo.
pause
