# Wisp

**Wisp** is a local-first, AirDrop-style file transfer app for **Windows 11 and Android**. Files move directly between devices over the local network. There is no account, no cloud relay, and no analytics.

Wisp is a project by **SSM Limited**. It is free and open-source software,
released under the [MIT License](LICENSE).

> Working name. The product and protocol are called Wisp throughout the repo.

## Status

The repository contains a working end-to-end MVP:

- WinUI 3 desktop app (C#) with taskbar tray icon, window/taskbar app icon, header activity gauge, UI sounds, and persistent transfer history
- .NET MAUI Android app (C#), tested on a Samsung Galaxy S22 running Android 16: Material icon navigation, a floating pill nav, light/dark/system themes, Material SnackBar feedback, full runtime-permission prompts, an always-visible discovery foreground service, live progress + success/failure notifications with a monochrome Wisp shade icon, and a home-screen widget with nearby count, live progress, and a quick-send shortcut
- AirDrop-style Android send sheet: sharing from Gallery/Files opens Wisp with a scanning nearby-device sheet; choosing a device sends immediately, and a live transfer sheet stays until 100% and can minimize to a progress pill + notification
- Biometric protections on Android: optional confirmation before accepting files and optional app unlock, using Android BiometricPrompt with device-credential fallback
- Categorized Android Settings (General / Transfer / Security / About) with appearance, sound, notification, receiving, permissions, identity, and trust controls
- Android-to-Android temporary QR links that bypass shared Wi-Fi: the host opens a LocalOnlyHotspot and shows a QR code; the other phone scans it, joins with `WifiNetworkSpecifier`, binds the process to that network, and adds the host as a pinned peer in the existing transfer engine. The link is torn down and the previous Wi-Fi restored on Disconnect.
- A QR-first empty state and share sheet: when no nearby device appears, Wisp offers **Scan QR code** and **Create link**. Sharing files into Wisp and then scanning a code sends the staged files automatically once the private link connects.
- An always-visible **Disconnect** action for an active temporary link, plus a LINK badge and summary on the Nearby page. Closing the link sheet leaves an established link running; disconnecting is explicit.
- About screens identify Wisp as a project by SSM Limited and link to the MIT-licensed source.
- A self-contained sideload APK (`dist/Wisp-android-0.2.0.apk`) with assemblies embedded for both arm64-v8a and x86_64
- Shared C++17 transfer engine compiled for Windows x64 and Android arm64-v8a / x86_64
- TLS 1.3 transfer with mutual authentication, certificate pinning, and pairing codes
- Parallel chunked transfers with per-chunk SHA-256 verification
- LAN discovery using broadcast, multicast, and a unicast subnet sweep fallback for access points that filter broadcast traffic, with a 30-second peer grace window so the nearby list never flickers
- UI sounds for connect/disconnect and transfer success/failure (original generated wav assets in `art/`)
- Android foreground service that keeps discovery alive while Wisp runs, and carries live transfer progress in the notification panel
- Light/dark/system themes with a dedicated light palette, fixed hero-card contrast, and status-bar icon contrast
- Persistent transfer history on both platforms
- A 30-second managed peer grace window and a corrected native expiry calculation eliminate the on/off flicker in the nearby list
- Windows drag-and-drop, file/folder pickers, trusted devices, transfer history for the current session
- Android share-sheet receiving, file picker, foreground-service lifetime for active transfers, and export to `Downloads/Wisp`
- Native integration tests and a C# interop smoke test

The first release is intentionally local-only. See [Current limitations](#current-limitations) and [docs/threat-model.md](docs/threat-model.md).

## How it works

```text
┌────────────────────┐                 ┌────────────────────┐
│  WinUI 3 (C#)      │                 │  .NET MAUI (C#)    │
│  UI + platform API │                 │  UI + platform API │
└─────────┬──────────┘                 └─────────┬──────────┘
          │        Wisp.Core (C#)                │
          │   peers · trust · settings · events  │
          └───────────────┬──────────────────────┘
                          │ flat C ABI / P/Invoke
                 ┌────────▼────────┐
                 │ Wisp.Native     │
                 │ C++17 engine    │
                 │ TLS 1.3 · TCP   │
                 │ chunks · resume │
                 └────────┬────────┘
                          │ direct Wi-Fi / Ethernet
                    another Wisp device
```

The receiving device hosts an authenticated TLS listener. The sender discovers it, verifies its pinned certificate, offers a manifest, and waits for the user to accept. Data then moves over up to four TLS connections in 4 MiB chunks; each chunk carries a SHA-256 digest. Files are written to `.part` files and renamed only after every chunk is present.

**Why TCP and not QUIC?** On a local network, QUIC's mobility and 0-RTT advantages are mostly irrelevant, while user-space packet processing can reduce throughput on phone CPUs. TLS 1.3 over TCP is simpler, better supported by both platforms, and fast enough to saturate a typical Wi-Fi link. The transport is behind the C ABI so a QUIC implementation can be added later.

## Repository layout

```text
native/                 C++17 engine, C ABI, CLI test harness, native tests
dotnet/src/Wisp.Core/   Shared C# models, native interop, trust/settings stores
dotnet/src/Wisp.Windows WinUI 3 desktop app
dotnet/src/Wisp.Android .NET MAUI Android app
dotnet/tools/Wisp.Smoke C# native-interop smoke test
docs/                   Architecture, protocol, threat model, testing notes
tools/                  Build/setup helpers
```

## Build

### Prerequisites

- Windows 11
- Visual Studio 2019/2022 Build Tools with MSVC v142/v143, CMake, and a Windows SDK
- CMake 3.20+ and Ninja
- .NET SDK 10 with the `maui-android` and `maui-windows` workloads
- Android SDK platform 36, build-tools 36, platform-tools, NDK r28+, and JDK 17
- OpenSSL 3.5+ for Windows (headers and x64 import libraries)
- Prebuilt Android OpenSSL under `android_openssl/ssl_3` (the current local build uses the KDAB Android OpenSSL tree)

This workspace was validated with MSVC 19.29, CMake 4.4.4, Ninja 1.13.2, .NET 10.0.402, NDK 28.2.13676358, and OpenSSL 3.6.1 on Windows.

### Windows native core and tests

```bat
tools\build-native.bat
```

This configures `build\native`, builds `wisp_core.dll`, and runs the native integration tests. The test creates a temporary two-engine transfer, verifies file hashes, and prints throughput.

### Windows app

```powershell
dotnet build dotnet\src\Wisp.Windows\Wisp.Windows.csproj -c Debug -p:Platform=x64
dotnet\src\Wisp.Windows\bin\x64\Debug\net10.0-windows10.0.19041.0\win-x64\Wisp.exe
```

The app is currently unpackaged (`WindowsPackageType=None`). The native DLL and OpenSSL DLLs are copied next to `Wisp.exe`.

### Android native core

The Android build expects OpenSSL in `C:\Users\<you>\wisp-tools\android_openssl` or a path supplied with `-DWISP_ANDROID_OPENSSL_ROOT=...`.

```bat
tools\build-android-native.bat
```

Outputs:

- `build\android-arm64-v8a\bin\libwisp_core.so`
- `build\android-x86_64\bin\libwisp_core.so`

Both are linked with a 16 KiB maximum page size for current Android requirements.

### Android app

Build a standalone APK:

```powershell
dotnet build dotnet\src\Wisp.Android\Wisp.Android.csproj -c Debug -f net10.0-android `
  -p:AndroidPackageFormat=apk -p:EmbedAssembliesIntoApk=true
adb install -r dotnet\src\Wisp.Android\bin\Debug\net10.0-android\com.wisp.app-Signed.apk
```

Or install with fast deployment on a connected device:

```powershell
dotnet build dotnet\src\Wisp.Android\Wisp.Android.csproj -c Debug -f net10.0-android -t:Install
```

The APK includes the native engine for arm64-v8a and x86_64 when both `.so` files exist. Assemblies are embedded (`EmbedAssembliesIntoApk=true`), so an APK copied to any phone opens normally — this was the cause of the earlier crash-on-open when sharing debug builds. The current debug build, including the QR scanner/generator and CameraX dependencies, is about 101 MB (universal ABI).

## Using Wisp

1. Open Wisp on both devices and connect them to the same Wi-Fi network.
2. Wait for the other device to appear under **Nearby**. If the AP filters broadcast/multicast, Wisp's unicast subnet sweep finds it within a few seconds.
3. On Windows, drag files onto a device card or use **Send files** / **Folder**. On Android, tap **Send** or share files into Wisp from another app.
4. On first contact, compare the six-digit code shown on both devices before accepting.
5. Choose **Trust this device** to pin its key. Trusted devices can be configured for auto-accept in Settings.

Default destinations:

- Windows: `%USERPROFILE%\Downloads\Wisp`
- Android: app-specific external storage, copied to `Downloads/Wisp` after receipt on Android 10+

### Temporary link without shared Wi-Fi (Android → Android)

Use this when the two phones are on different networks, a guest Wi-Fi isolates
clients, or no router is available:

1. On the phone that will receive files, tap **Temporary link → Create connection**
   (or **Create link** in the empty nearby state). Wisp starts a private
   LocalOnlyHotspot and shows a QR code, the Wi-Fi name/password, the host
   endpoint, and the pinned certificate fingerprint.
2. On the other phone, tap **Temporary link → Scan code** (or **Scan QR code**),
   then point the camera at the QR. Approve Android's Wi-Fi prompt. Wisp joins
   the private network, routes its engine through it, and adds the host as a
   pinned peer. If you started from a share sheet, the staged files are sent as
   soon as the link connects.
3. Choose **Send files** in the connected sheet. The normal share sheet,
   six-digit pairing code, and live progress flow are unchanged.
4. Tap **Disconnect** on both phones when finished. The hotspot/network request
   is released and each phone returns to its previous Wi-Fi and normal discovery.
   Closing the sheet with the X leaves an active link running (the LINK badge and
   **Disconnect** button stay on the Nearby page), so you can send more files
   before tearing it down.

The QR contains a random one-time token that the guest presents in its native
`HELLO`; the host binds it to the first peer identity and rejects other peers. See
[docs/qr-temporary-link.md](docs/qr-temporary-link.md) for the full flow,
security properties, and test checklist.

## Current limitations

These are deliberately transparent rather than hidden:

- **Discovery is LAN-only unless you use a temporary link.** Wisp uses UDP port `47778` on the local subnet. It does not traverse VLANs, guest networks, or AP client isolation with discovery alone. The Android-to-Android temporary QR link bypasses that by owning a LocalOnlyHotspot; mDNS/DNS-SD discovery and a Windows-hosted hotspot remain future work.
- **No resume across app restarts.** A transfer interrupted by a network drop or process kill must currently be retried from the beginning. `.part` files and per-chunk hashing provide the foundation for resume.
- **No BLE radar, Wi-Fi Direct, or Windows-hosted hotspot.** Android-to-Android QR temporary links are implemented; Windows hosting and Wi-Fi Direct require significantly more platform-specific work.
- **Temporary links need two Android devices.** Hosting needs Android 8.0+; scanning and joining need Android 10+. The current build was compiled but could not be exercised with a second physical phone in this environment; see [docs/testing.md](docs/testing.md).
- **Android identity keys are stored in app-private storage**, not yet wrapped by Android Keystore. Windows keys likewise are not yet DPAPI-wrapped before being passed to the native engine.
- **The Android OpenSSL build is 3.1.8** from a prebuilt tree. For a production release, rebuild or replace it with OpenSSL 3.5 LTS (or another maintained TLS implementation) and verify 16 KiB page alignment.
- **No MSIX/Store packaging or signed release APK.** The Windows app is unpackaged but does have a tray icon; Android uses a debug signing key.
- **The Android home-screen widget** shows nearby count, live progress, and a quick-send shortcut, but Android updates it only while the app process runs; it does not update from a cold start.
- **The distributed APK is debug-signed.** Install it by enabling “Install unknown apps” for your file manager/browser. A signed release APK and Play/Store packaging are future work.
- **The last physical-device pass could not be completed** because the phone disconnected from ADB during the final share-sheet/biometric build. The preceding build was verified on-device for transfer, notification, history, light theme, and notification icon; see [docs/testing.md](docs/testing.md).
- **Android app lock is cold-start/return based**, not a system-level app lock. It prompts on launch and after 20 seconds in the background when enabled.
- **No automatic background acceptance.** An incoming transfer asks for consent while the app is running; long transfers get an Android foreground-service notification.
- **Physical-device matrix is small.** Windows 11 x64 and one Samsung Galaxy S22 were used. No Windows ARM64 or Android table/emulator validation yet.

## Security

Wisp uses TLS 1.3 with mutual certificates, a pinned peer fingerprint, and a six-digit short authentication string derived from the TLS exporter. Incoming paths are validated against traversal and Windows reserved names; manifests are bounded; files are staged and only finalized after all chunks verify. Android temporary-link QR codes carry the pinned fingerprint plus a random one-time token bound to the first guest identity; treat the QR as a temporary password. See [docs/threat-model.md](docs/threat-model.md) for the full model and residual risks.

Please do not treat this MVP as independently audited cryptography or a finished consumer security product.

## Downloads

Binaries are published as GitHub release assets because they are too large for
the repository (the installer exceeds GitHub's normal file limit).

- [Wisp v0.2.0 release](https://github.com/sudaissalisu/wisp/releases/tag/v0.2.0)
- [Android APK](https://github.com/sudaissalisu/wisp/releases/download/v0.2.0/Wisp-android-0.2.0.apk) — debug-signed, universal
- [Windows installer](https://github.com/sudaissalisu/wisp/releases/download/v0.2.0/Wisp-Setup-0.2.0.exe) — bundles the .NET and Windows App Runtime prerequisites
- [Portable Windows build](https://github.com/sudaissalisu/wisp/releases/download/v0.2.0/Wisp-Windows-x64-0.2.0.zip) — requires both runtimes

Version 0.2.0 adds the Android temporary QR link, the QR-first send flow,
disconnect controls, the SSM Limited About screens, and the transfer-progress
fix. The [v0.1.0 release](https://github.com/sudaissalisu/wisp/releases/tag/v0.1.0)
remains available for the original build.

The Windows installer requests administrator rights for the runtime installers.
The Android APK is self-contained and does not need Fast Deployment.

## Documentation

- [Architecture](docs/architecture.md)
- [Protocol](docs/protocol.md)
- [Android temporary QR link](docs/qr-temporary-link.md)
- [Threat model](docs/threat-model.md)
- [Building](docs/building.md)
- [Testing notes](docs/testing.md)
- [Roadmap](docs/roadmap.md)
- [Release assets](docs/releases.md)
- [AdMob placement plan](docs/monetization.md)
- [Changelog](CHANGELOG.md)
- [Contributing](CONTRIBUTING.md)
- [Third-party notices](THIRD_PARTY_NOTICES.md)

## License and ownership

Wisp is a project by **SSM Limited**. The source in this repository is released
under the [MIT License](LICENSE); you may use, modify, and redistribute it with
the copyright notice intact. The Wisp name, logo, and app icons remain marks of
SSM Limited. Third-party components keep their own licenses; see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Wisp does not display ads today. The [AdMob placement plan](docs/monetization.md)
records the policy for a future optional build: ads never appear in the send,
accept, transfer, onboarding, QR, or notification flows, and the app can always
be built without the ad SDK.
