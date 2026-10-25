# Third-party notices

Wisp is built on the following open-source components. Versions reflect the current
development build; see the package manifests for the exact resolved versions.

## Runtime libraries

| Component | Use | License |
|---|---|---|
| OpenSSL 3.5+ (Windows) / 3.1.8 (Android prebuilt) | TLS 1.3 and cryptography | Apache License 2.0 |
| .NET 10 / .NET MAUI | Shared app logic and Android UI | MIT |
| Windows App SDK / WinUI 3 | Windows UI | MIT |
| H.NotifyIcon.WinUI 2.4.1 | Windows taskbar tray icon | MIT |
| Microsoft.Data.Sqlite? | not used in this build | — |

## Fonts and assets

| Component | Use | License |
|---|---|---|
| Material Icons font (Google) | Android navigation and status icons | Apache License 2.0 |
| Open Sans (Google) | Android UI type | SIL Open Font License 1.1 |
| Wisp app icon, splash mark, UI sounds | Original assets generated for this project | © Wisp project |

The Android OpenSSL binaries in `android_openssl/ssl_3` are the prebuilt KDAB
Android OpenSSL tree (OpenSSL 3.1.8). They are used for development only and must
be replaced with a maintained OpenSSL 3.5 LTS (or another supported TLS
implementation) for a production release. See the repository's `README.md`.

## Platform SDKs

Android SDK, Android NDK, and Java are used under their respective licenses.
They are build-time dependencies and are not distributed in the repository.

If you redistribute a Wisp build, include this file and the upstream license
texts for the components you ship (notably OpenSSL, Material Icons, Open Sans,
and H.NotifyIcon).
