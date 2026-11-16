@echo off
rem Prepare KKAO a partager aux collegues : out\KKAO-<version>\ (dossier) et out\KKAO-<version>.zip
rem Les applis (bundle\apps\*.json) sont embarquees, pas installees : le collegue clique "Installer" dans KKAO.
cd /d "%~dp0"
echo Compilation de KKAO...
dotnet publish KKAO.csproj -c Release -o out\build -nologo -v q
if errorlevel 1 goto :fail
echo Assemblage du paquet (applis embarquees, zip)... patience, Electron est gros.
start "" /wait "out\build\KKAO.exe" --bundle
if errorlevel 1 goto :fail
for %%f in (out\KKAO-*.log) do type "%%f"
echo.
echo OK : dossier et zip dans %~dp0out
goto :eof
:fail
for %%f in (out\KKAO-*.log) do type "%%f"
echo ECHEC
exit /b 1
