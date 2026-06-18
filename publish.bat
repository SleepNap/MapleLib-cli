@echo off
REM One-shot single-file exe build.
REM
REM Output:   .\dist\xml-img-patcher.exe   (one self-contained exe, ~80MB)
REM            -> copy this anywhere, target machine does NOT need .NET installed.
REM
REM Original publish artifact stays at:
REM   .\MapleLib.XmlImgPatcher\bin\Release\net10.0-windows\win-x64\publish\xml-img-patcher.exe

setlocal
pushd "%~dp0"

dotnet publish MapleLib.XmlImgPatcher\MapleLib.XmlImgPatcher.csproj -c Release
if errorlevel 1 (
    echo.
    echo [publish.bat] dotnet publish failed.
    popd
    exit /b 1
)

set "SRC=%~dp0MapleLib.XmlImgPatcher\bin\Release\net10.0-windows\win-x64\publish\xml-img-patcher.exe"
set "DST=%~dp0dist\xml-img-patcher.exe"

if not exist "%~dp0dist" mkdir "%~dp0dist"
copy /Y "%SRC%" "%DST%" >nul
if errorlevel 1 (
    echo [publish.bat] copy to dist\ failed.
    popd
    exit /b 1
)

echo.
echo ============================================================
echo  done.
echo  standalone exe: %DST%
for %%I in ("%DST%") do echo  size: %%~zI bytes
echo ============================================================

popd
endlocal
