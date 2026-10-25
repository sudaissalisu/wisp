# Release assets

Large binaries are not stored in Git. Build locally or download a release from:

https://github.com/sudaissalisu/wisp/releases

Expected assets for 0.1.0:

- `Wisp-Setup-0.1.0.exe` — Windows 11 x64 installer; bundles the required
  .NET 10 Desktop Runtime and Windows App Runtime.
- `Wisp-Windows-x64-0.1.0.zip` — portable Windows build; requires both runtimes
  to be installed.
- `Wisp-android-0.1.0.apk` — self-contained universal debug-signed APK for
  Android 8.0+ (arm64-v8a and x86_64).

The APK is for development/testing and is not production-signed. The Windows
installer requests administrator rights to install the runtime prerequisites.
