# Changelog

## 0.2.0 — 2026-10-27

### Added

- Android-to-Android temporary QR link: LocalOnlyHotspot hosting, local QR
  generation with QRCoder, ZXing.Net.Maui camera scanning, WifiNetworkSpecifier
  joining, process network binding, pinned-peer injection through
  `WispEngine.AddPeer`, a one-time HELLO token bound to the first guest
  fingerprint, and automatic teardown/Wi-Fi restoration.
- QR entry points where they are needed: **Scan QR code** and **Create link** in
  the empty nearby state, plus **Scan a QR code** in the share sheet when no LAN
  peer is visible. Files shared into Wisp are staged and sent automatically
  after the scanned link connects.
- An active-link card on Nearby with a LINK badge, connection summary, **Open
  link** and **Disconnect** actions. The host can send back once the guest
  appears on the private subnet through discovery.
- Android back button now dismisses the topmost sheet (link, share, transfer,
  offer, settings category) instead of leaving the app.
- About screens identify Wisp as a project by SSM Limited, show the version, and
  link to the MIT-licensed source and license.
- MIT `LICENSE` and an AdMob placement plan in `docs/monetization.md`.
- CAMERA, ACCESS_FINE_LOCATION, and CHANGE_NETWORK_STATE manifest permissions,
  plus on-demand camera/location runtime requests for the temporary link.
- `QrLinkPayload` in Wisp.Core for validated QR URI construction and parsing.
- Native `wisp_engine_set_link_token` / `wisp_engine_set_expected_link_token`
  and an optional trailing link token in the `HELLO` frame.
- Native `wisp_engine_set_bind_address` and `WispEngine.SetBindAddress` so the
  hotspot host's outgoing transfer sockets use the soft-AP source address.

### Fixed

- Android transfer progress no longer stays at 0% until completion. The live
  transfer sheet subscribes to intermediate progress events, the native engine
  guarantees the first non-zero byte count even inside the throttle window,
  emits a final 100% progress event before completion, and logs progress at
  start/five-second intervals/end for diagnosis.
- `ShowTransferOverlay` resumes from the current item progress instead of
  resetting the meter to zero.
- Closing the temporary-link sheet no longer tears down a working link; ending
  it is an explicit Disconnect action. The link card and toast make that clear.
- The portable Windows archive is now a real ZIP with forward-slash entry
  names. The 0.1.0 asset named `.zip` was a tar archive and could not be opened
  by Windows Explorer, `unzip`, or .NET.
- Multicast lock acquisition is idempotent across engine restarts.

### Documentation

- New [Android temporary QR link](docs/qr-temporary-link.md) guide with flow,
  token handshake, permissions, limits, and a two-phone checklist.
- New [AdMob placement plan](docs/monetization.md) covering placements,
  guardrails, build flags, and privacy requirements.
- Protocol, architecture, testing, roadmap, building, and README updates,
  including the SSM Limited ownership and MIT license sections.

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
