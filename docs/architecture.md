# Wisp architecture

## Components

### Wisp.Native (C++17)

A flat C ABI shared library (`wisp_core.dll` / `libwisp_core.so`) owns:

- Ed25519 identity generation and persistence (self-signed X.509 certificate)
- TLS 1.3 server and client contexts, mutual certificate exchange, ALPN `wisp/1`
- Fingerprint pinning and pairing-code derivation
- Frame encoding/decoding and bounded manifest parsing
- Outgoing and incoming transfer state machines
- Four parallel TLS data channels and a control channel
- Per-chunk SHA-256 calculation and verification
- Filesystem writes through positioned I/O, `.part` files, and atomic finalization
- UDP LAN discovery: limited broadcast, directed subnet broadcast, multicast, and a unicast subnet sweep

The C ABI exposes an opaque engine handle and JSON event callbacks so the UI can evolve without recompiling the engine.

Key sources:

- `native/src/identity.*` — certificate and TLS contexts
- `native/src/net.*` — sockets, listeners, TLS I/O
- `native/src/protocol.*` — frames, hello, offer, response, data, errors
- `native/src/discovery.*` — LAN announcements and unicast probing
- `native/src/engine_send.cpp` — sender state machine and data-channel workers
- `native/src/engine_receive.cpp` — receiver consent, writes, verification, finalization
- `native/src/capi.cpp` — C ABI

### Wisp.Core (C#)

Portable .NET 10 library shared by both UIs:

- Typed wrappers around events (`PeerInfo`, `TransferOffer`, `TransferProgress`, `TransferCompletion`, `TransferFailure`, `PairingInfo`)
- P/Invoke bindings and callback lifetime management
- Native string marshaling and event deserialization
- Trusted-device pin store (`trusted.json`)
- Cross-platform settings store (`settings.json`)
- Managed peer cache used by the UI

Events are raised on native worker threads. The UIs must marshal them to their dispatcher; both current apps do.

### Windows app (WinUI 3)

- `MainWindow` shell with Mica backdrop and NavigationView
- Generated Wisp app icon in the window/taskbar, H.NotifyIcon tray icon
- Header activity pill with progress ring, file count, and percentage
- WinMM UI sounds for connect/disconnect and outcomes
- Persistent history loaded into the Transfers list
- Nearby device cards, drag/drop, file and folder pickers
- Incoming offer ContentDialog with pairing code and trust checkbox
- Transfers list with speed, progress, cancel, and open-folder
- Devices page with identity, trusted devices, and manual address fallback
- Settings page for device name, destination, and trusted auto-accept
- Native dependencies copied next to the app as unpackaged files

### Android app (.NET MAUI)

- Single-page Material-style UI with light/dark/system themes and a floating pill navigation bar
- Header activity gauge drawn with `Microsoft.Maui.Graphics`
- Material SnackBar feedback through `AndroidFeedback`; newline-free bottom toasts
- Sounds via SoundPool, notification channels with a monochrome Wisp vector icon
- Local home-screen widget: nearby count, live progress, and quick-send action
- UDP discovery backed by a `WifiManager.MulticastLock`
- Process network binding to the active Wi-Fi network so native sockets do not escape over cellular
- Share-sheet receiver (`ACTION_SEND` / `ACTION_SEND_MULTIPLE`) and file picker staging
- `WispForegroundService` keeps the process alive while a transfer is active
- Received files are written under the app-specific external directory and copied to `Downloads/Wisp` through MediaStore on Android 10+
- Trusted devices and identity are visible in Settings

## Connection lifecycle

```text
Sender                                      Receiver
  │  discover (broadcast / multicast / sweep)  │
  │◄──────────────── peer beacon ──────────────│
  │  TCP connect → TLS 1.3 (mutual certs)       │
  │──────────────── HELLO ────────────────────►│
  │◄─────────────── HELLO_ACK ─────────────────│
  │  both compute 6-digit pairing code          │
  │──────────────── OFFER (manifest) ─────────►│
  │              user consent                   │
  │◄───────────── OFFER_RESPONSE + token ───────│
  │  1–4 data TLS channels, ATTACH + token      │
  │──────────────── DATA chunks ──────────────►│  write .part, verify SHA-256
  │  periodic PING on control                   │
  │──────────────── FINISH ───────────────────►│
  │                                             │  verify all chunks, rename
  │◄─────────────── DONE ───────────────────────│
  │              COMPLETE UI event              │
```

The receiver owns the destination folder and the acceptance decision. The sender never gets to choose an arbitrary path on the receiving machine.

## Threading and lifetime

- A listener thread accepts sockets.
- Each accepted connection gets a worker; the control connection and each data channel run concurrently.
- A dedicated discovery thread sends beacons, probes, and expires peers.
- Event callbacks may arrive from any native thread.
- The engine counts active workers and waits for them on shutdown. Socket timeouts bound how long shutdown can take.
- The UI wraps the native handle in `WispEngine` and only calls it after start / before dispose.

## Discovery design

Wisp uses three complementary mechanisms on UDP port `47778`:

1. **Beacon announcements** every 1.5s to limited broadcast, each interface's directed broadcast, and multicast group `239.255.42.99`
2. **Peer beacons** containing public identity: display name, platform, peer ID, TCP port, certificate fingerprint
3. **Unicast subnet sweep** for networks that drop broadcast/multicast between clients. A probe (`WISP-PROBE|1`) is sent directly to each host in /22–/30 networks; a Wisp peer replies with a unicast beacon to the probe source. This is why discovery works on restrictive APs where the first two mechanisms fail.

Peers expire after 25 seconds without a sighting, with guarded timestamp arithmetic to avoid unsigned underflow. The managed layer additionally debounces removal by 30 seconds so Wi-Fi power-save bursts never flicker the nearby list.

Discovery advertisements are public and unauthenticated. They are only a rendezvous hint; authentication is done by TLS certificate fingerprint and the user-visible pairing code.

## Why parallel TLS channels?

A single TLS connection serializes all data through one OpenSSL instance and one core's encryption work. Four channels:

- Keep multiple cores busy on encryption and hashing
- Reduce head-of-line stalls on lossy Wi-Fi
- Allow independent writes to different offsets/files
- Bound memory with fixed per-channel buffers

The sender assigns 4 MiB chunks from an atomic queue. Each data channel authenticates with a random token issued only on the accepted control channel and bound to the mutual TLS identities.

## File handling and performance

- 4 MiB chunks; 4 data channels by default (configurable 1–8)
- Sender reads source files with positioned reads; receiver writes with positioned writes
- Per-chunk SHA-256 over the ciphertext-protected payload; corrupt chunks fail the transfer
- Receiver preallocates the part file, writes out-of-order chunks, and renames only after all chunks are present
- No compression: media is already compressed and compression would cost more CPU than it saves on a LAN
- No per-chunk UI callback storm: progress is throttled and only summary values cross the ABI
- Android's `dataSync` foreground service provides a visible transfer notification and process lifetime

### Measured local numbers

On the development machine over loopback:

- 128 MiB single file: ~77 MiB/s including TLS setup and process overhead
- 17 MiB mixed file set with 40 small files: ~9 MiB/s, dominated by filesystem and Windows Defender churn
- C# interop smoke test: 8 MiB in ~0.35 s

Real Wi-Fi throughput depends on AP, band, power saving, and storage. No benchmark claim is made beyond these local observations.

## Future architecture work

- Resume metadata and chunk bitmaps persisted next to `.part` files; query on reconnect
- mDNS/DNS-SD service discovery with TXT metadata and system service pickers
- QR pairing that carries endpoint, fingerprint, and a one-time token
- Windows Mobile Hotspot / Android LocalOnlyHotspot fallback
- BLE proximity advertising as a UI hint, not a payload transport
- MSIX packaging with share-target registration and a scoped firewall rule
- Keystore/DPAPI wrapping of identity private keys
- Android 17 local-network permission UX and Wi-Fi Aware exploration
- Windows ARM64 build and a larger device matrix
