# Release assets

Large binaries are not stored in Git. Build locally or download a release from:

https://github.com/sudaissalisu/wisp/releases

## 0.2.0

- [Wisp-Setup-0.2.0.exe](https://github.com/sudaissalisu/wisp/releases/download/v0.2.0/Wisp-Setup-0.2.0.exe) — Windows 11 x64 installer; bundles the required .NET 10 Desktop Runtime and Windows App Runtime.
- [Wisp-Windows-x64-0.2.0.zip](https://github.com/sudaissalisu/wisp/releases/download/v0.2.0/Wisp-Windows-x64-0.2.0.zip) — portable Windows build; requires both runtimes to be installed.
- [Wisp-android-0.2.0.apk](https://github.com/sudaissalisu/wisp/releases/download/v0.2.0/Wisp-android-0.2.0.apk) — self-contained universal debug-signed APK for Android 8.0+ (arm64-v8a and x86_64).
- `SHA256SUMS.txt` — checksums for the three assets above.

0.2.0 adds the Android-to-Android temporary QR link, the QR-first send flow,
explicit disconnect controls, the SSM Limited About screens, and the transfer
progress fix. See the [changelog](../CHANGELOG.md).

## 0.1.0

The first development release remains available at
[releases/tag/v0.1.0](https://github.com/sudaissalisu/wisp/releases/tag/v0.1.0).

The APK is for development/testing and is not production-signed. The Windows
installer requests administrator rights to install the runtime prerequisites.
