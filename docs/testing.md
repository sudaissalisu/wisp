# Testing notes

Last updated: 2026-10-24

## Automated

### Native engine

```bat
tools\build-native.bat
```

The integration test starts two engines in-process, performs an encrypted TLS
transfer of a 12 MiB file, small files, an empty file, image folders, and 40
small files, then verifies byte counts and SHA-256 hashes. It also covers path
traversal, reserved Windows names, and other manifest validation cases.

Latest run:

```text
test directory: C:\Users\...\Temp\wisp-test-...
transferred 17.0784 MiB in 987 ms (17.3033 MiB/s)
all native tests passed
```

Loopback single-file throughput measured separately with the CLI at roughly
77 MiB/s for a 128 MiB file (TLS setup included, loopback). Mixed small-file
transfers are lower because of filesystem and antivirus overhead.

### C# interop

```powershell
dotnet run --project dotnet\tools\Wisp.Smoke\Wisp.Smoke.csproj -c Release
```

Exercises the C ABI from managed code: creates two engines, sends an 8 MiB file,
waits for managed progress/completion events, and verifies the hash.

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

## Not yet verified

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
