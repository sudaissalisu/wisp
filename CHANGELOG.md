# Changelog

## 0.5.1 — 2026-10-28

### Fixed

- Media viewer crash on Android: the decor content view was assigned a base
  `ViewGroup.LayoutParams`, but the decor FrameLayout requires
  `MarginLayoutParams`. Opening a video/audio/image threw
  `ClassCastException: ViewGroup$LayoutParams cannot be cast to
  ViewGroup$MarginLayoutParams`. Use `FrameLayout.LayoutParams` for the content
  and keep the 0.4.1 fullscreen sizing fix.

### Device notes

- 0.5.0's update check was verified on the Galaxy S22: About reported
  “You’re on the latest version (0.5.0).”
- 0.5.0 crashed when opening a video; the log pointed at the layout-params cast
  above. 0.5.1 is built with the fix. The phone disconnected again before the
  retest, so the final media check is still pending.

## 0.5.0 — 2026-10-28

### Added

- In-app update checks on Android and Windows against a static HTTPS manifest at
  `update.json` (GitHub raw). The request carries only the app version/platform
  user agent; there is no custom update server or analytics.
- Android **Update available** banner on Nearby and a release-notes sheet with
  Download / Later / Skip this version; Windows About **Check** and a release
  notes ContentDialog.
- Manual **Check for updates** in Settings → About on both platforms; automatic
  checks at most once per 24 hours.
- `SKIPPED_UPDATE_VERSION` and once-per-version prompt state persisted in
  settings.
- Headless `UpdateService` tests for manifest parsing, version comparison, HTTPS
  download validation and snake_case asset URLs.

### Fixed

- Update manifest asset URLs are mapped from `release_url`, `android_url`, and
  `windows_url`; the initial camelCase-only parser would have silently lost all
  download links.

### Notes

- 0.5.0 is the first build with the checker, so it will correctly report itself
  as current. The prompt is exercised against a newer manifest or a test build.
- Downloads open the signed APK/installer in the browser; Android requires the
  user to allow installs from that browser. No silent install, delta update, or
  mandatory enforcement.
- The earlier media-dialog fix from 0.4.1 is included.

## 0.4.1 — 2026-10-28

### Fixed

- Media viewer dialogs now force their content root to match the window. On the
  Galaxy S22 the video was measured at its intrinsic height, so the frame sat at
  the top of the screen and the seek/speed/mute controls were offscreen. The
  dialog now fills the window, hides the system bars for video, and keeps the
  controls at the bottom.

### Notes

- 0.4.0 was tested on a physical Galaxy S22 before this fix: browser link page,
  token rejection, upload/download hash, Downloads/Wisp export, clipboard text,
  Files search, browser overlay back behavior and Files-to-Nearby back navigation
  all passed. The media-dialog fix is built in 0.4.1 but the phone disconnected
  before the retest; verify the four bullet checks in `docs/testing.md`.

## 0.4.0 — 2026-10-27

### Added

- **Browser link** on Android: a local HTTP server and embedded offline page
  that let any browser on the same Wi-Fi upload files, download received files,
  and send a URL/text to the phone clipboard. No install, account, or cloud.
  Includes a QR code, copy-link action, 30-minute inactivity expiry, explicit
  Stop, multiple browser clients, upload progress, and export of uploads to
  `Downloads/Wisp`.
- **Ask Wisp voice commands**: Android speech capture plus a local parser for
  phrases such as “Send the video from yesterday to Sara” and “Share the invoice
  from June to John”. Wisp shows the matched file, target, and transcript and
  waits for confirmation before sending.
- **Files search** over file names, types, dates, and extracted text from
  text-like documents. Supports month, today, yesterday, week, and month queries.
- `LanAddress` fallback for Android builds where `NetworkInterface` does not
  expose the Wi-Fi IPv4 address.
- Portable `BrowserLinkServer`, `LibraryFile`, and `VoiceCommandParser` in
  Wisp.Core; browser, library, and voice tests run without an Android device.

### Changed

- Android versionCode 5; both apps and the installer are 0.4.0.
- Browser uploads use a dedicated single-file `Downloads/Wisp` export instead
  of re-copying the whole received folder.
- Files search rebuilt at scan time with a 16 KiB text sample per text-like file
  and a 2 MiB index budget.

### Security

- Browser link uses a random 128-bit token on every request, binds to one LAN
  address, enforces request/upload limits, sanitizes names and paths, and serves
  a CSP-restricted embedded page. It is local HTTP, not Wisp TLS; the UI warns to
  use a trusted Wi-Fi.

### Not in this build

- Native iOS client (the web page works in Safari).
- OCR, image/video embeddings, PDF summaries, Whisper transcription, and other
  model-dependent local AI actions.
- Resume across process death, send-when-charging, native Wisp group rooms, and
  an encrypted internet relay.

## 0.3.0 — 2026-10-27

### Added

- Android **Files** tab with All / Photos / Videos / Music / Docs filters over
  the received-files folder, file counts and sizes, refresh, and empty states.
- In-app Android media viewers:
  - images with pinch zoom, pan, double-tap reset and EXIF rotation;
  - video with play/pause, ±10-second skip, seek bar, 0.5×–2× speed, mute,
    auto-hiding controls, a full-screen dialog and keep-screen-on;
  - audio with seek, skip, speed and mute.
- Transfer **History** rows now open the received file; multi-file transfers
  show a file picker sheet. Non-media files open through a minimal FileProvider
  in whichever installed app handles the MIME type.
- Redesigned minimized transfer dock above the navigation bar: peer title,
  percentage, live status/speed, expand-on-tap, a separate Cancel action, and
  automatic dismissal after completion.
- Adaptive parallel data channels: the sender chooses 2–6 TLS streams from CPU
  cores and transfer size when the caller leaves the channel count at 0.
- Zero-copy DATA framing and a reused receiver chunk buffer.
- Channel-count diagnostics in the native log and transfer-performance notes.

### Changed

- `WispConfig.DataChannels` defaults to 0 (adaptive). An explicit 1–8 still
  pins the channel count.
- Android and Windows version metadata moved to 0.3.0; Android versionCode 4.

### Fixed

- A failed transfer no longer forces the sheet's progress bar to 100%.
- The minimized transfer dock no longer sits over the navigation bar, no longer
  stays at 100% forever, and keeps Cancel available only while a transfer is
  active.

### Performance

- The native integration test improved from roughly 12.0 MiB/s to 14–15 MiB/s
  on the same loopback workstation (mixed 17 MiB file set, including TLS setup).
  The C# 8 MiB interop test measured 25–28 MiB/s. These are loopback numbers;
  Wi-Fi results still need a two-device pass.

### Documentation

- New [files and media](docs/media-and-files.md) and
  [transfer performance](docs/transfer-performance.md) notes, plus roadmap
  ideas for on-device AI and zero-install receiving.

## 0.2.1 — 2026-10-27

### Fixed

- Android back navigation now closes the topmost sheet, leaves a Settings
  category, or returns to the previous tab before Android can exit the app.
  Nearby is the home tab, so back there exits. On .NET 10 MAUI the root page's
  `OnBackButtonPressed` override is not reached, so the activity registers an
  `OnBackPressedCallback` that delegates to the page.
- Trusted devices no longer show the six-digit pairing code on later transfers.
  Trust was keyed on the TLS certificate fingerprint, but the offer sheet always
  displayed the code and the per-device auto-accept flag was never set. A
  fingerprint match now shows a "trusted device" note instead; first contact
  still requires comparing the code.
- Android auto-accept now uses the Settings toggle for any trusted device
  instead of an unreachable per-device flag.

### Notes

- No protocol or wire-format changes. 0.2.1 is compatible with 0.2.0 peers.

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
