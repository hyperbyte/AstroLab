@echo off
title AstroLab
cd /d "%~dp0"
echo.
echo   AstroLab  -  http://localhost:5151
echo   O browser abre automaticamente. Fecha esta janela para parar a app.
echo.
dotnet run -c Release --no-launch-profile
