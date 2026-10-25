# Changelog

## 0.1.0 — 2026-10-24

### Added

- Native C++17 transfer engine with TLS 1.3, mutual certificates, certificate
  pinning, six-digit pairing codes, parallel data channels, SHA-256 chunks,
  atomic finalization, and path/manifest limits.
- Local discovery using broadcast, multicast, and a unicast subnet sweep, with a
  30-second managed grace period for temporarily missing peers.
- Shared .NET 10 core with typed native event wrappers, trust storage, settings,
  and durable transfer history.
- WinUI 3 Windows app with Nearby/Transfers/Devices/Settings, drag-and-drop,
  folder picker, transfer sheet, tray icon, activity gauge, sounds, history, and
  first-run onboarding.
- Windows installer with bundled .NET 10 Desktop Runtime and Windows App Runtime
  installers, plus a portable x64 build.
- .NET MAUI Android app with local discovery, share-target receiving,
  AirDrop-style send sheet, foreground transfer service, progress/result
  notifications, home-screen widget, light/dark/system themes, onboarding,
  settings menu, history, generated Wisp notification icon, and biometric gates.
- Documentation for architecture, wire protocol, threat model, testing,
  reproducible builds, release assets, and contribution workflow.

### Known gaps

- No DirectLink/BLE/Wi-Fi Aware/QR zero-network flow yet.
- No Windows-hosted temporary hotspot/QR flow yet.
- No per-file browser, in-app image viewer, or Windows media player yet.
- No Quick Settings visibility tile yet.
- Android identity keys are stored in app-private storage rather than wrapped by
  Android Keystore; Windows keys are not DPAPI-wrapped.
- Android OpenSSL is a prebuilt 3.1.8 tree and must be updated before production.
- APK is debug-signed.
