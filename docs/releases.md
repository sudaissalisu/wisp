# Release assets

Large binaries are not stored in Git. Build locally or download a release from:

https://github.com/sudaissalisu/wisp/releases

## 0.4.1

- [Wisp-Setup-0.4.1.exe](https://github.com/sudaissalisu/wisp/releases/download/v0.4.1/Wisp-Setup-0.4.1.exe) — Windows 11 x64 installer; bundles the required .NET 10 Desktop Runtime and Windows App Runtime.
- [Wisp-Windows-x64-0.4.1.zip](https://github.com/sudaissalisu/wisp/releases/download/v0.4.1/Wisp-Windows-x64-0.4.1.zip) — portable Windows build; requires both runtimes to be installed.
- [Wisp-android-0.4.1.apk](https://github.com/sudaissalisu/wisp/releases/download/v0.4.1/Wisp-android-0.4.1.apk) — self-contained universal debug-signed APK for Android 8.0+ (arm64-v8a and x86_64).
- [SHA256SUMS.txt](https://github.com/sudaissalisu/wisp/releases/download/v0.4.1/SHA256SUMS.txt) — checksums for the three assets above.

0.4.1 fixes the Android media-viewer dialog sizing found on a physical Galaxy
S22. It contains the 0.4.0 browser link/voice/search work unchanged.

## 0.4.0

- [Wisp-Setup-0.4.0.exe](https://github.com/sudaissalisu/wisp/releases/download/v0.4.0/Wisp-Setup-0.4.0.exe) — Windows 11 x64 installer; bundles the required .NET 10 Desktop Runtime and Windows App Runtime.
- [Wisp-Windows-x64-0.4.0.zip](https://github.com/sudaissalisu/wisp/releases/download/v0.4.0/Wisp-Windows-x64-0.4.0.zip) — portable Windows build; requires both runtimes to be installed.
- [Wisp-android-0.4.0.apk](https://github.com/sudaissalisu/wisp/releases/download/v0.4.0/Wisp-android-0.4.0.apk) — self-contained universal debug-signed APK for Android 8.0+ (arm64-v8a and x86_64).
- [SHA256SUMS.txt](https://github.com/sudaissalisu/wisp/releases/download/v0.4.0/SHA256SUMS.txt) — checksums for the three assets above.

0.4.0 adds the zero-install browser link, Ask Wisp voice commands, and Files
search. No wire-protocol changes; compatible with 0.3.x peers.

## 0.3.0

- [Wisp-Setup-0.3.0.exe](https://github.com/sudaissalisu/wisp/releases/download/v0.3.0/Wisp-Setup-0.3.0.exe) — Windows 11 x64 installer; bundles the required .NET 10 Desktop Runtime and Windows App Runtime.
- [Wisp-Windows-x64-0.3.0.zip](https://github.com/sudaissalisu/wisp/releases/download/v0.3.0/Wisp-Windows-x64-0.3.0.zip) — portable Windows build; requires both runtimes to be installed.
- [Wisp-android-0.3.0.apk](https://github.com/sudaissalisu/wisp/releases/download/v0.3.0/Wisp-android-0.3.0.apk) — self-contained universal debug-signed APK for Android 8.0+ (arm64-v8a and x86_64).
- [SHA256SUMS.txt](https://github.com/sudaissalisu/wisp/releases/download/v0.3.0/SHA256SUMS.txt) — checksums for the three assets above.

0.3.0 adds the Files tab and media players, opens received files from history,
redesigns the minimized transfer dock, and speeds up transfers with zero-copy
DATA framing and adaptive channels. No protocol changes; compatible with 0.2.x
peers.

## 0.2.1

- [Wisp-Setup-0.2.1.exe](https://github.com/sudaissalisu/wisp/releases/download/v0.2.1/Wisp-Setup-0.2.1.exe) — Windows 11 x64 installer; bundles the required .NET 10 Desktop Runtime and Windows App Runtime.
- [Wisp-Windows-x64-0.2.1.zip](https://github.com/sudaissalisu/wisp/releases/download/v0.2.1/Wisp-Windows-x64-0.2.1.zip) — portable Windows build; requires both runtimes to be installed.
- [Wisp-android-0.2.1.apk](https://github.com/sudaissalisu/wisp/releases/download/v0.2.1/Wisp-android-0.2.1.apk) — self-contained universal debug-signed APK for Android 8.0+ (arm64-v8a and x86_64).
- [SHA256SUMS.txt](https://github.com/sudaissalisu/wisp/releases/download/v0.2.1/SHA256SUMS.txt) — checksums for the three assets above.

0.2.1 fixes Android back navigation and stops trusted devices from being asked
to compare the pairing code again. No protocol changes; it interoperates with
0.2.0 peers. See the [changelog](../CHANGELOG.md).

## 0.2.0

- [Wisp-Setup-0.2.0.exe](https://github.com/sudaissalisu/wisp/releases/download/v0.2.0/Wisp-Setup-0.2.0.exe) — Windows 11 x64 installer; bundles the required .NET 10 Desktop Runtime and Windows App Runtime.
- [Wisp-Windows-x64-0.2.0.zip](https://github.com/sudaissalisu/wisp/releases/download/v0.2.0/Wisp-Windows-x64-0.2.0.zip) — portable Windows build; requires both runtimes to be installed.
- [Wisp-android-0.2.0.apk](https://github.com/sudaissalisu/wisp/releases/download/v0.2.0/Wisp-android-0.2.0.apk) — self-contained universal debug-signed APK for Android 8.0+ (arm64-v8a and x86_64).

0.2.0 added the Android-to-Android temporary QR link, the QR-first send flow,
explicit disconnect controls, the SSM Limited About screens, and the transfer
progress fix.

## 0.1.0

The first development release remains available at
[releases/tag/v0.1.0](https://github.com/sudaissalisu/wisp/releases/tag/v0.1.0).

The APK is for development/testing and is not production-signed. The Windows
installer requests administrator rights to install the runtime prerequisites.
