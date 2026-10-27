# Testing notes

Last updated: 2026-10-27

## Automated

### Native engine

```bat
tools\build-native.bat
```

The integration test starts two engines in-process, performs an encrypted TLS
transfer of a 12 MiB file, small files, an empty file, image folders, and 40
small files, then verifies byte counts and SHA-256 hashes. It also covers path
traversal, reserved Windows names, and other manifest validation cases. The same
test arms the receiver with a one-time link token, sends the token from the
sender, asserts the receiver bound it, asserts that sender and receiver both
observed intermediate `bytes_done` progress before completion, and binds the
sender's source address to `127.0.0.1`. It then sends with a different token and
asserts the receiver rejects it, followed by a second transfer with the original
token from the same peer, which must still succeed.

Latest run (2026-10-25):

```text
test directory: C:\Users\...\Temp\wisp-test-76ce93d7e262
transferred 17.0784 MiB in 1483 ms (11.5161 MiB/s)
sender error: {... "message":"temporary link token rejected","code":1}
all native tests passed
```

Loopback single-file throughput measured separately with the CLI at roughly
77 MiB/s for a 128 MiB file (TLS setup included, loopback). Mixed small-file
transfers are lower because of filesystem and antivirus overhead.

### C# interop

```powershell
dotnet run --project dotnet\tools\Wisp.Smoke\Wisp.Smoke.csproj -c Release
```

Exercises the C ABI from managed code: round-trips a QR payload with spaces,
ampersands and an emoji, creates two engines, sets the same one-time link token
through `SetExpectedLinkToken`/`SetLinkToken`, binds the sender source address,
sends an 8 MiB file, counts intermediate `ProgressChanged` events on both sides,
waits for completion, and verifies the hash.

Latest run (2026-10-25):

```text
QR payload round-trip: 192.168.43.1:54321, Phone 😊
progress events: sender=1, receiver=1
verified 8,389,385 bytes in 0.48s (16.6 MiB/s)
```

### Android build

```powershell
dotnet build dotnet\src\Wisp.Android\Wisp.Android.csproj -c Debug -f net10.0-android `
  -p:AndroidPackageFormat=apk -p:EmbedAssembliesIntoApk=true
```

Latest result: **Build succeeded** (2026-10-25). The signed debug APK is
`dotnet\src\Wisp.Android\bin\Debug\net10.0-android\com.wisp.app-Signed.apk`
(~101 MB, arm64-v8a + x86_64). `aapt2 dump badging` confirms `CAMERA`,
`ACCESS_FINE_LOCATION`, `CHANGE_NETWORK_STATE`, `NEARBY_WIFI_DEVICES`, and
optional camera features in the merged manifest. The build reports NU1608
AndroidX version-range warnings from combining MAUI's AndroidX set with the
CameraX versions required by ZXing.Net.Maui; NuGet resolves the newer MAUI
versions and the APK builds, but an on-device camera pass is still required.

## Manual validation performed

### Windows 11

- App starts unpackaged with Mica, custom title bar, and app icon.
- Header activity gauge shows live file count and percentage during a transfer.
- Pairing InfoBar displays the six-digit code.
- A 250 MiB file was sent from the native CLI into the GUI; the offer was accepted
  and the file arrived at `Downloads\Wisp\wisp-big.bin`.
- The Transfers page shows the finished item with direction, size, "Received"
  state, and an Open folder action.
- Transfer history persisted to `%LOCALAPPDATA%\Wisp\history.json`.
- Tray icon and UI sound files are wired (not visually/audibly re-verified after
  the final tray build).

### Android on Samsung Galaxy S22 (Android 16)

- Debug APK installed over ADB; self-contained APKs embed assemblies and native
  `.so` files for arm64-v8a and x86_64.
- Chinese/CJK-looking nav glyphs were fixed by registering the Material Icons
  font under its real file alias (`MaterialIcons-Regular`).
- Redesigned Nearby page renders: compact hero, Material icon nav, floating pill
  nav, card surfaces, and the header activity gauge.
- Material Snackbar appears at the bottom for feedback ("You're live").
- Notification permission was granted by the system prompt; nearby Wi-Fi and
  local-network permissions are requested together and can be managed from the
  new Settings permissions card.
- Foreground service confirmed running with the screen off:
  `isForeground=true foregroundId=73`.
- Earlier light-theme run showed the need for fixed light hero text and
  status-bar icon contrast; both were changed after that run and require a
  final on-device visual pass.

### Connection / discovery

- Phone-to-PC unicast TCP worked in both directions on the same /24 subnet.
- Access point filtered limited broadcast and multicast between the two stations;
  the native unicast subnet sweep was added as the fallback and peers were
  discovered through it.
- A peer-flicker bug was found in the discovery expiry calculation: the loop used
  a timestamp captured before a blocking socket read, so a beacon received during
  that read could subtract to a huge unsigned value and expire the peer
  immediately. Fixed by using a fresh timestamp and a guarded subtraction.
- A 30-second managed grace window also debounces peer removal so the UI never
  flickers during Wi-Fi power-save bursts.

### Final on-device verification (standalone APK, Samsung S22, Android 16)

- Standalone APK installed with `adb install -r` (no fast deployment) and opened
  without crashing.
- A 30 MB file was sent from the Windows native CLI into the Android app:
  offer sheet showed sender, file, size, six-digit code, and Accept/Decline.
- Accepting completed the transfer; the success notification appeared with the
  Wisp monogram icon (not a warning icon), reading “Files received · Received
  30.0 MB from PC Sender”.
- The file was exported to `Downloads/Wisp/wisp-phone-test.bin` (30,457,280 bytes).
- Transfer history showed the received item in both the current session and the
  durable History section.
- Light theme rendered with white cards, dark text, dark system status-bar icons,
  and the mint/nav accents. The header spacing was tightened.
- Peer stability after the expiry fix: one native CLI probe over 35 seconds
  reported `found=1, lost=0` for the phone (previously 89 found / 89 lost over 65
  seconds).
- The widget provider is registered
  (`com.wisp.app/...WispWidgetProvider`), and its resources are in the APK.

### Newest build (share sheet, live transfer popup, biometrics, categorized settings)

Built but not installed because the phone disconnected from ADB during the final
package build. These flows compile and are implemented but require a fresh
on-device pass:

- Gallery/Files share → Wisp → scanning nearby sheet → tap device → send.
- Live transfer sheet staying open through 100%, minimize pill, and progress
  notification.
- Security → biometric accept and biometric app unlock toggles.
- Categorized Settings chips and the new monochrome notification icon in a live
  foreground notification.
- Adding the home-screen widget and its quick-send action.

### Temporary QR link build (2026-10-25)

The Android app was compiled with ZXing.Net.Maui 0.10.4, QRCoder 1.8.1, and the
LocalOnlyHotspot/WifiNetworkSpecifier flow. No device was attached during this
build (`adb devices` was empty), and only one physical phone has been used for
the project to date, so the two-phone QR flow was **not** exercised on hardware.

What was verified automatically:

- native `wisp_core` built for Windows x64, arm64-v8a, and x86_64
- native integration test: one-time token handshake, token consumption, and
  intermediate sender/receiver progress
- C# interop smoke test: `SetExpectedLinkToken`/`SetLinkToken`, intermediate
  `ProgressChanged` events, and hash verification
- Android APK build succeeded; merged manifest contains CAMERA,
  ACCESS_FINE_LOCATION, CHANGE_NETWORK_STATE, NEARBY_WIFI_DEVICES, and optional
  camera features

### 0.3.0 build (2026-10-27)

- Native engine rebuilt with zero-copy DATA frames, receiver buffer reuse and
  adaptive channels. `wisp_native_tests` passed and averaged 14.6 MiB/s on the
  17 MiB mixed set (12.1 MiB/s before); the C# smoke test passed at 25–28 MiB/s.
- Android 0.3.0 (versionCode 4) built with the Files tab, native media dialogs,
  FileProvider, history file opening and the new minimized dock.
- Windows 0.3.0 built and launched; the native DLL and version metadata are
  current.
- No Android device was attached, so the new media UI has not been exercised on
  hardware. Manual checklist:
  1. Receive an image, a video, an audio file and a PDF; all four appear in Files.
  2. Check each filter and the count/total-size summary.
  3. Image: pinch, pan, double-tap, EXIF-rotated photo, rotation of the device.
  4. Video: play/pause, seek, ±10 s, every speed step, mute, auto-hide, completion
     rewind, backgrounding stops playback.
  5. Audio: same transport controls and audio focus after dismissal.
  6. PDF/doc: opens in an installed viewer through FileProvider without a storage
     permission prompt.
  7. History: single-file and multi-file transfer tap-to-open.
  8. Minimize a transfer: dock shows title/percent/speed, sits above the nav bar,
     Cancel works, completion auto-hides it, tap reopens the sheet.
  9. Failed transfer shows a stopped state, not 100%.
  10. Run a >100 MiB transfer and confirm the log reports the expected adaptive
      channel count and the progress/notification stay live.

### 0.2.1 build (2026-10-27)

- Android 0.2.1 (versionCode 3) and Windows 0.2.1 builds.
- Back navigation is now registered through an AndroidX `OnBackPressedCallback`
  in `MainActivity`, because .NET 10 MAUI does not invoke the root page's
  `OnBackButtonPressed` override. Expected order: close link/share/offer/transfer
  sheets → leave a Settings category → return to the previous tab (Nearby is
  home) → exit.
- Trusted devices skip the pairing-code card. The check compares the offer's TLS
  certificate fingerprint with `trusted.json`; a changed key shows the code again.
- No Android device was attached during this build. The back order and the
  trusted-device sheet still need a physical pass:
  1. Transfers → Settings → About → back returns to Settings; back again returns to
   Transfers; back again returns to Nearby; back once more exits.
  2. Scan sheet → back closes it without leaving Wisp.
  3. Incoming offer → back declines and the sender stops waiting.
  4. Active transfer sheet → back minimizes; the transfer keeps running.
  5. Trust a device, send again, and confirm the next offer shows “Trusted
     device — its key is already pinned” with no six-digit code.
  6. Forget the device in Settings → Security and confirm the code returns.

### 0.2.0 release build (2026-10-27)

- Native engine rebuilt for Windows x64, arm64-v8a, and x86_64; the native
  integration test passed with intermediate progress and the link-token
  handshake.
- C# interop smoke test passed, including QR payload round-trip, staged progress
  events, and the 8 MiB hash check.
- Android APK rebuilt as `Wisp-android-0.2.0.apk` with the QR-first empty state,
  share-sheet QR entry, persistent link card/disconnect, back-button sheet
  handling, and the SSM Limited About screen.
- Windows Release build and `Wisp-Setup-0.2.0.exe` rebuilt with the updated
  About card and version metadata.
- No Android device was attached during this build, so the new UI states (QR
  entry points, auto-send after scan, X keeps the link, Disconnect) were not
  exercised on hardware. They are included in the two-phone checklist below.

What still requires two physical phones:

- LocalOnlyHotspot SSID/passphrase retrieval and the `SoftApConfiguration`
  path on Android 11+
- hotspot IPv4 detection on the actual OEM interface name
- the system `WifiNetworkSpecifier` consent dialog and process binding
- scanner camera preview, QR decode, and the one-time token rejection path
- Disconnect restoring the previous Wi-Fi on both phones
- the live transfer sheet, Transfers item, header gauge, and notification
  advancing with intermediate bytes for a 200 MB+ file over the hotspot
- closing the link sheet and confirming the link stays active until Disconnect
- sharing files into Wisp with no LAN peer, scanning the host code, and
  confirming the staged files send automatically

Use the checklist in [qr-temporary-link.md](qr-temporary-link.md#testing-checklist-two-physical-phones-required).

## Not yet verified

- Two-phone temporary QR link flow and its Wi-Fi restoration (see above).
- Final on-device run of the newest share-sheet/biometric APK (USB disconnected).
- Adding the widget to a real home screen and tapping its Send shortcut.
- A complete Android → Windows transfer initiated from the Android UI.
- Windows tray icon appearance in the notification area.
- Windows ARM64 and any Android device other than the Galaxy S22.
- Long-duration transfers, resume across process death, and multi-gigabyte tests.

## Device-lab recommendations

Before calling the build production-ready:

1. Re-run the cross-device matrix on at least one Wi-Fi 5 and one Wi-Fi 6E AP,
   with and without AP client isolation.
2. Verify Android 13/14/15/16 permission flows and background limits on each.
3. Verify the widget, notification actions, and light/dark/system themes.
4. Run a 10+ GiB transfer with screen off and a deliberate network drop.
5. Check a device with a notched display and gesture navigation insets.
6. Verify firewall prompts and rules on a clean Windows 11 user profile.
