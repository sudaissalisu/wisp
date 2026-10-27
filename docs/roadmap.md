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

## Shipped in 0.2.1

- Android back closes the topmost sheet, leaves a Settings category, or steps
  back through tabs before it can leave the app
- Trusted devices are matched by their pinned TLS fingerprint and skip the
  pairing-code card on later transfers

## Shipped in 0.3.0

- Files tab with type filters over received files
- Image viewer with zoom/pan, video player with speed/mute/skip, audio player
- Open received files from transfer history
- Redesigned minimized transfer dock
- Zero-copy DATA framing, receive buffer reuse, adaptive parallel channels
- FileProvider for opening non-media files in installed apps

## Next: AI-era adoption

The goal is to be the obvious local-transfer app, not an AI demo. These are
ordered by adoption impact and privacy cost.

1. **Zero-install browser receiver.** A phone or laptop that cannot install Wisp
   opens a local HTTPS page (QR or short URL), pairs with a code, and sends or
   receives through the browser. This removes the biggest adoption blocker: the
   other person not having the app.
2. **iOS client and web sender.** Same protocol surface as the browser receiver,
   so mixed households and small teams do not need all-Android hardware.
3. **On-device semantic Files search.** OCR document text and generate local
   image/video embeddings with ML Kit or ONNX Runtime Mobile; search "invoice
   from June" or "beach video" without uploading anything. Index only the Wisp
   folder and keep the model on-device.
4. **Voice-intent sending.** "Send the video I took yesterday to Sara" with
   on-device speech recognition and a small intent parser, followed by the
   normal confirmation sheet. Never auto-send from voice alone.
5. **AI file actions, opt-in.** Summarize a PDF, transcribe an audio/video file
   with whisper.cpp, extract text, suggest a filename/tag, and detect duplicates.
   Run locally; show a clear "processed on this device" label.
6. **Clipboard and link handoff.** A tiny text/link transfer path between trusted
   devices, with an explicit "Send clipboard" action rather than silent sync.
7. **Resume and reliability.** Persist chunk bitmaps next to `.part` files, resume
   after a network drop or process death, and offer "send when charging".
8. **Group receive rooms.** One host, several receivers for a photo dump, class,
   or event; each receiver gets the same auth flow and progress.
9. **Windows parity.** Files/media browsing and system-player handoff on Windows,
   plus a Windows-hosted QR hotspot.
10. **Optional encrypted relay.** A clearly separate, opt-in remote mode with
    end-to-end keys for people who are not on the same network; never silently
    enabled and never required for local transfers.

Deliberately not doing: cloud accounts, silent analytics, uploading files to an
AI service, or ads inside transfer flows. SSM Limited's privacy positioning is
the product advantage.

## Next: personal-file polish

- Per-file history entries with thumbnails
- Swipe navigation between images and videos in the viewer
- Subtitle track selection, picture-in-picture, and casting
- Background audio playback with a media session
- Windows media viewer/player
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
