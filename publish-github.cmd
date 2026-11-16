@echo off
rem Publie une version de KKAO sur GitHub (Koesio-Managed-Services/KKAO) :
rem compile, assemble les paquets des applis, prepare out\release, cree la release (brouillon), envoie les fichiers, publie.
rem Prerequis : GitHub CLI connecte (gh auth login) avec le droit d'ecrire dans le depot ; code deja pousse sur main.
setlocal
cd /d "%~dp0"
set REPO=Koesio-Managed-Services/KKAO

gh auth status >nul 2>&1
if errorlevel 1 (
    echo GitHub CLI n'est pas connecte : lancez d'abord   gh auth login
    exit /b 1
)

call build-bundle.cmd
if errorlevel 1 goto :fail

echo Preparation de la release...
start "" /wait "out\build\KKAO.exe" --release
if errorlevel 1 (
    type out\release.log
    goto :fail
)
type out\release.log
set /p TAG=<out\release-tag.txt

gh release view %TAG% --repo %REPO% >nul 2>&1
if not errorlevel 1 (
    echo La release %TAG% existe deja : montez la version dans KKAO.csproj.
    goto :fail
)

echo Creation de la release %TAG% (brouillon)...
gh release create %TAG% --repo %REPO% --draft --title "KKAO %TAG:~1%" --notes-file out\release-notes.md
if errorlevel 1 goto :fail
for %%f in (out\release\*) do (
    echo   envoi de %%~nxf
    gh release upload %TAG% "%%f" --repo %REPO% --clobber
    if errorlevel 1 goto :fail
)
gh release edit %TAG% --repo %REPO% --draft=false --latest
if errorlevel 1 goto :fail

echo.
echo Publiee : https://github.com/%REPO%/releases/tag/%TAG%
echo Verification comme un collegue : out\build\KKAO.exe --verify-online out\verif-en-ligne.txt
exit /b 0

:fail
echo ECHEC de la publication
exit /b 1
