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
- QR display/scanning and a LocalOnlyHotspot fallback
- Windows-hosted temporary hotspot with QR; requires MSIX package identity and
  `wiFiControl`
- Automatic teardown and restoration of the previous network

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
