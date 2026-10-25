@echo off
setlocal
set "ROOT=%~dp0.."
set "VCVARS=C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
set "CMAKE=C:\Users\Dreadshades\wisp-tools\cmake-4.4.4-windows-x86_64\bin\cmake.exe"
set "NINJA=C:\Users\Dreadshades\wisp-tools\ninja"
set "PATH=%NINJA%;%PATH%"

if not exist "%CMAKE%" (
  echo CMake not found at %CMAKE%
  exit /b 1
)
call "%VCVARS%" >nul
if errorlevel 1 exit /b 1

if /I "%~1"=="clean" (
  rmdir /s /q "%ROOT%\build\native" 2>nul
  shift
)

"%CMAKE%" -S "%ROOT%\native" -B "%ROOT%\build\native" -G Ninja -DCMAKE_BUILD_TYPE=Release
if errorlevel 1 exit /b 1
"%CMAKE%" --build "%ROOT%\build\native" --parallel
if errorlevel 1 exit /b 1

if /I not "%~1"=="nottest" (
  "%ROOT%\build\native\bin\wisp_native_tests.exe"
  if errorlevel 1 exit /b 1
)
endlocal
