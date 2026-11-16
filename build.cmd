@echo off
rem Compile KKAO et depose l'executable dans dist\
cd /d "%~dp0"
dotnet publish KKAO.csproj -c Release -o dist -nologo
if errorlevel 1 (
    echo.
    echo ECHEC de la compilation.
    pause
    exit /b 1
)
echo.
echo OK : dist\KKAO.exe
