@echo off
setlocal
set "ROOT=%~dp0.."
set "CMAKE=C:\Users\Dreadshades\wisp-tools\cmake-4.4.4-windows-x86_64\bin\cmake.exe"
set "NINJA=C:\Users\Dreadshades\wisp-tools\ninja"
set "NDK=%LOCALAPPDATA%\Android\Sdk\ndk\28.2.13676358"
set "TOOLCHAIN=%NDK%\build\cmake\android.toolchain.cmake"
set "PATH=%NINJA%;%PATH%"
if not exist "%TOOLCHAIN%" (
  echo Android NDK toolchain not found at %TOOLCHAIN%
  exit /b 1
)
if not exist "C:\Users\Dreadshades\wisp-tools\android_openssl\ssl_3\arm64-v8a\libssl.a" (
  echo Android OpenSSL libraries not found.
  exit /b 1
)

for %%A in (arm64-v8a x86_64) do (
  echo Building wisp_core for %%A...
  "%CMAKE%" -S "%ROOT%\native" -B "%ROOT%\build\android-%%A" -G Ninja ^
    -DCMAKE_TOOLCHAIN_FILE="%TOOLCHAIN%" ^
    -DANDROID_ABI=%%A ^
    -DANDROID_PLATFORM=android-26 ^
    -DANDROID_STL=c++_static ^
    -DCMAKE_BUILD_TYPE=Release ^
    -DWISP_BUILD_TESTS=OFF
  if errorlevel 1 exit /b 1
  "%CMAKE%" --build "%ROOT%\build\android-%%A" --parallel
  if errorlevel 1 exit /b 1
)
echo Android native build complete.
endlocal
