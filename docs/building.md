# Building Wisp

Wisp has three build products:

1. the shared native transfer engine,
2. the WinUI 3 desktop app and installer,
3. the .NET MAUI Android app.

The source is designed to be built independently: the native engine does not
depend on the desktop or mobile projects.

## Prerequisites

### Windows native + desktop

- Windows 11 x64
- Visual Studio 2019 or 2022 Build Tools with MSVC and a Windows 10/11 SDK
- CMake 3.20+ and Ninja
- .NET SDK 10 with `maui-android` and `maui-windows` workloads
- OpenSSL 3.5+ for Windows, including the x64 import libraries
- Inno Setup 6 (only for creating the installer)

The validated local environment used MSVC 19.29, CMake 4.4.4, Ninja 1.13.2,
.NET 10.0.402, and OpenSSL 3.6.1.

### Android

- .NET SDK 10 + MAUI Android workload
- JDK 17
- Android SDK platform 36, build-tools 36, platform-tools
- Android NDK r28+
- A prebuilt OpenSSL for Android under `android_openssl/ssl_3` (KDAB's tree),
  or supply `-DWISP_ANDROID_OPENSSL_ROOT=...`

At least one Android device is required to validate Wi-Fi discovery and transfers.
Two Android 10+ devices are required to validate the temporary QR link end to
end (the host can be Android 8+, the scanner needs Android 10+). The Android
build restores ZXing.Net.Maui.Controls, ZXing.Net, QRCoder, and the CameraX
AndroidX packages from NuGet; no extra manual setup is required.

## Browser link and voice parser smoke test

```powershell
dotnet run --project dotnet	ools\Wisp.BrowserSmoke\Wisp.BrowserSmoke.csproj -c Release
```

This runs the portable `BrowserLinkServer` on loopback and checks the embedded
page, token rejection, a 1 MiB upload hash, file listing/download hashes, the
text-to-clipboard event, and several voice phrases against a temporary library.
It needs no Android device and no network beyond localhost.

## Native engine

```bat
tools\build-native.bat
```

This configures `build/native`, builds `wisp_core.dll`, and runs the native tests.
The test covers frame validation, path traversal, TLS transfer, chunk hashes,
interrupted transfer state, and large/small files.

Android native:

```bat
tools\build-android-native.bat
```

Outputs `build/android-arm64-v8a/bin/libwisp_core.so` and
`build/android-x86_64/bin/libwisp_core.so`. Both are linked with 16 KiB page
alignment.

## Windows app

```powershell
dotnet build dotnet\src\Wisp.Windows\Wisp.Windows.csproj -c Release -p:Platform=x64
```

Run:

```text
dotnet\src\Wisp.Windows\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\Wisp.exe
```

The app requires the .NET 10 Desktop Runtime and Windows App Runtime 2.5. The
installer bundles both.

### Installer

1. Download the two runtime installers into `installer/deps/` (see
   `installer/fetch-deps.ps1`).
2. Build the release output above.
3. Compile:

```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\wisp.iss
```

Output: `dist/Wisp-Setup-0.4.1.exe`.

The setup installs to `Program Files\Wisp`, creates Start Menu/optional desktop
shortcuts, and registers an optional startup entry. It requests administrator
rights because the runtime installers require them.

> The Windows App SDK self-contained publish path currently crashes inside
> `Microsoft.UI.Xaml.dll` on this toolchain. The installer therefore uses the
> verified framework-dependent Release build plus bundled runtime installers.

### Portable zip

```powershell
powershell -ExecutionPolicy Bypass -File tools\pack-windows.ps1 -Version 0.4.1
```

Output: `dist/Wisp-Windows-x64-0.4.1.zip`. The script writes a real ZIP with
forward-slash entry names. Do not use GNU tar's `-a` on Windows (it produces a
tar archive even with a `.zip` name, which the 0.1.0 asset accidentally did) or
`Compress-Archive` (backslash entry names).

### Checksums

```powershell
cd dist
sha256sum Wisp-android-0.4.1.apk Wisp-Setup-0.4.1.exe Wisp-Windows-x64-0.4.1.zip > SHA256SUMS.txt
```

## Android app

```powershell
dotnet build dotnet\src\Wisp.Android\Wisp.Android.csproj -c Debug -f net10.0-android `
  -p:AndroidPackageFormat=apk -p:EmbedAssembliesIntoApk=true
adb install -r dotnet\src\Wisp.Android\bin\Debug\net10.0-android\com.wisp.app-Signed.apk
```

`EmbedAssembliesIntoApk=true` is required for a shareable APK. Without it, a
debug APK installed outside the build machine aborts because Fast Deployment
expects assemblies pushed over ADB.

The APK is currently debug-signed. A release keystore and signed release build
are future work.

## Release checklist

- Native tests pass.
- Windows Release build launches.
- Android standalone APK installs and launches without a debugger.
- A real device-to-device or device-to-PC transfer succeeds.
- `THIRD_PARTY_NOTICES.md` is current.
- Do not commit `dist/`, `installer/deps/`, keystores, identity keys, or APKs.
