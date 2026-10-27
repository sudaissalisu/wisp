# Roadmap

This roadmap distinguishes implemented work from planned work. Items are not
promises; they are engineering milestones.

## Shipped in 0.1.0

- Windows 11 WinUI 3 client and Android .NET MAUI client
- Direct TLS 1.3 transfers over LAN with mutual certificates and pairing codes
- Parallel chunked transfers with SHA-256 verification
- Broadcast/multicast/unicast-sweep discovery and peer-expiry debouncing
- Windows tray icon, app icon, activity gauge, sounds, and transfer history
- Android share sheet, AirDrop-style send sheet, foreground service, progress
  and result notifications, home-screen widget, theme support, onboarding,
  settings menu, history, and biometric gates

## Shipped in 0.2.0

- Android-to-Android temporary QR link using LocalOnlyHotspot,
  WifiNetworkSpecifier, pinned peer injection, a one-time HELLO token, and
  automatic teardown/Wi-Fi restoration (two-phone hardware pass pending)
- QR-first send flow: scan/create entry points in the empty state and share
  sheet, automatic send of staged files after scanning, and explicit disconnect
- Intermediate transfer progress on the live sheet, list, notification, and
  header gauge from the first byte
- SSM Limited About screens, MIT license, and an AdMob placement plan

## Next: personal-file polish

- Per-file history entries with thumbnails
- Android image viewer with swipe navigation
- Windows media viewer/player
- Open/reveal individual received files from history
- Better notification text contrast on Android OEM skins
- Verify biometric prompts with actual enrolled fingerprints

## Next: zero-configuration connectivity

- DirectLink abstraction shared by both clients
- Android ↔ Android Wi-Fi Aware with Wi-Fi Direct fallback
- BLE endpoint handoff
- Windows-hosted temporary hotspot with QR; requires MSIX package identity and
  `wiFiControl`
- Two-phone hardware validation of the Android temporary link, including OEM
  hotspot interface names and work-profile policy


## Later

- Quick Settings visibility tile backed by a persistent Android runtime service
- Transfer resume across process restarts and network drops
- Signed release APK/AAB, MSIX/Store packaging, auto-update
- Android 17 local-network permission polish and a larger device matrix
- 500 Mbps-class link utilization; 500 MB/s requires different hardware (Wi-Fi 7
  or a wired link), not merely a scheduling change

## Explicit non-goals for now

- Cloud relay or accounts
- Internet file transfer
- Bypassing Android or Windows consent dialogs
