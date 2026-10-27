# Android temporary QR link

This document describes the Android-to-Android connection path that does not
need a shared Wi-Fi network. One phone hosts a private Android LocalOnlyHotspot;
the other joins it by scanning a QR code and adds the host to the existing Wisp
transfer engine as a pinned peer.

## Why a temporary link?

Normal Wisp discovery is LAN-only: both devices must be on the same subnet and
the access point must allow client traffic. Guest networks, client isolation,
and phones on different bands can block that. A LocalOnlyHotspot is a private
Wi-Fi network owned by the host app, so the two phones get a direct L2 path with
no internet involvement.

There is no second transfer protocol. After the network is joined, the existing
TLS 1.3 engine, certificate pinning, pairing code, manifest, and chunked
transfer flow are reused unchanged.

## Host flow: Create connection

1. `CreateTemporaryLink_Click` requests camera/location/nearby-Wi-Fi permission
   as needed. Camera is only requested for scanning.
2. The app calls `WifiManager.startLocalOnlyHotspot` (`TemporaryHotspot`).
3. On `onStarted`, the app reads the SSID and WPA2 passphrase from
   `LocalOnlyHotspotReservation` (`SoftApConfiguration` on Android 11+, legacy
   `WifiConfiguration` on older releases).
4. The app resolves the host IPv4 address:
   - First from `java.net.NetworkInterface`: `ap*`, `swlan*`, `softap*`,
     `wlan1*`, or `p2p*` interfaces. This is preferred because a device with
     concurrent station + hotspot connections may still expose its home Wi-Fi
     in `ConnectivityManager`.
   - Then from `ConnectivityManager` `LinkProperties` of every Wi-Fi network.
   - AOSP's common `192.168.43.1` soft-AP address is used only as a last-resort
     fallback, with a warning in the UI.
5. The Wisp engine is restarted without a process-wide Wi-Fi binding (the old
   listener was pinned to the home Wi-Fi that may now be unavailable). It keeps
   LAN discovery enabled on the new interface.
6. The app generates a random 128-bit one-time token and arms the native engine
   with `wisp_engine_set_expected_link_token`. It also calls
   `wisp_engine_set_bind_address` with the hotspot IPv4 address, so files sent
   back from the host use the soft-AP interface; LocalOnlyHotspot is not exposed
   as a `ConnectivityManager` network.
7. A QR code is rendered locally with QRCoder. No network service is used to
   create or display it. The overlay also shows the SSID, password, endpoint,
   and countdown.

QR URI format:

```text
wisp://connect?v=1&ssid=...&pass=...&host=192.168.43.1&port=54321
  &fp=<64 hex SHA-256 certificate fingerprint>
  &token=<32 hex one-time token>&name=<display name>&exp=<unix seconds>
```

The code expires after ten minutes unless a transfer starts. Selecting
**Disconnect** stops the hotspot, clears the token, unbinds, waits for Android to
restore the previous Wi-Fi, and restarts normal discovery.

The host does not have to keep the sheet open. The X button hides it and the
link stays up; the Nearby page shows a **LIVE** badge with a summary, an **Open
link** action, and a **Disconnect** button. This is deliberate: closing a sheet
should not silently stop a working network, and the explicit Disconnect action
is easy to find.

## Entry points

The same two actions are available wherever they are likely to be needed:

- the **Temporary link** card on Nearby (always visible);
- **Scan QR code** / **Create link** in the empty nearby state shown when no LAN
  peer is discovered;
- **Scan a QR code** in the share sheet when no nearby device is listed. The
  staged files are kept and sent automatically as soon as the scanned link
  connects.

Android's back button closes the topmost sheet (link, share, transfer offer, or
settings category) before it can leave the app.

## Guest flow: Scan code

1. `Scan code` requests camera access and opens the in-app
   `zxing:CameraBarcodeReaderView` (ZXing.Net.Maui) in a bottom sheet.
2. The QR URI is validated before use: version, SSID, WPA2 passphrase length,
   IPv4 host, port, 64-hex pinned fingerprint, 32-hex token, and expiry.
3. `TemporaryWifiLink` builds a `WifiNetworkSpecifier` with the SSID and
   passphrase and calls `ConnectivityManager.requestNetwork(request, callback,
   handler, 45_000)`. Android shows its own Wi-Fi consent prompt.
4. On `onAvailable`, the process is bound with
   `ConnectivityManager.bindProcessToNetwork(network)`, so every native socket
   created afterwards uses the temporary network.
5. The Wisp engine is restarted with discovery enabled on the temporary network
   and the process binding left intact. The host is also added manually, so
   discovery is not required for the first connection.
6. `WispEngine.SetLinkToken(token)` arms the sender. `WispEngine.AddPeer` adds a
   manual peer using the host from the QR payload, the port, and the pinned
   certificate fingerprint. The native engine derives the peer ID from the
   fingerprint, exactly as with the manual-address fallback.
7. The connected sheet offers **Send files** and **Disconnect**. Sending uses the
   existing share sheet/transfer sheet and engine. The host is also added to the
   nearby list (as a manual peer) so the user can see what the link is attached
   to; it disappears when the link is torn down.

`Disconnect` unregisters the network callback, unbinds the process, waits for
the previous Wi-Fi to reconnect, and restarts discovery.

On the host, the guest is learned from normal discovery on the hotspot. The host
only adopts a peer whose address falls inside the hotspot subnet from the QR
URL, so a home-network peer cannot be mistaken for the guest. Once that peer is
present, the host's **Send files** action targets it and either phone can start
a transfer.

## One-time token handshake

The receiver (host) sets an expected token; the sender (guest) sets the link
token it presents. The token is appended to the `HELLO` payload as an optional
field:

```text
u16 protocol_version, string name, string platform, [string link_token]
```

The native receiver binds the expected token to the first matching `HELLO`
using a constant-time comparison in `Engine::consume_expected_link_token`. The
token remains associated with that peer certificate fingerprint so the same
guest can start additional transfers over the active link; a missing/different
token or a replay from another identity is rejected with an `ERROR` frame. A
peer that does not know the token can still use normal Wi-Fi discovery and
pairing; the token is only armed while a temporary link is active.

Compatibility notes:

- Old peers reject the trailing token field. Only the guest sends it, and the
  guest only reaches a new Android host through this QR flow, so no old peer is
  part of the path.
- `HELLO_ACK` never includes a token.
- The token is not a substitute for certificate pinning or the user-visible
  six-digit pairing code. It binds the QR-created network session to the
  discovery payload and prevents using a stale code after the host has stopped
  waiting.

## Progress reporting (fixed in 0.2.0)

Before 0.2.0 the live transfer sheet only received progress when the transfer
completed, so it stayed at 0% for the whole transfer even though the Transfers
list item updated. `MainPage.OnProgress` now calls
`UpdateTransferOverlay(progress)` for every intermediate event. Additional fixes:

- Native progress always emits the first non-zero `bytes_done`, even if the
  transfer finishes inside the 250 ms throttle window.
- Native progress emits a final forced event with `bytes_done == bytes_total`
  before the receiver's completion event.
- The sender and receiver log progress at the start, every five seconds, and at
  the end (`WISP_LOG_DEBUG`); the Android page logs throttled UI observations
  under the `wisp:ui` tag.
- `ShowTransferOverlay` continues from the current TransferItem progress
  instead of resetting to zero.

The native test and C# smoke test both assert that intermediate progress events
arrive for a large file; neither fakes progress.

## Permissions

Manifest:

```xml
<uses-permission android:name="android.permission.CAMERA" />
<uses-permission android:name="android.permission.ACCESS_FINE_LOCATION" />
<uses-permission android:name="android.permission.CHANGE_NETWORK_STATE" />
```

Existing Wisp permissions (`NEARBY_WIFI_DEVICES`, `ACCESS_LOCAL_NETWORK`,
`CHANGE_WIFI_STATE`, notifications, foreground service) remain. Camera is
requested at scan time, not at app startup. Location is requested only when
Nearby Wi-Fi Devices is unavailable. The camera is an optional feature so the
APK still installs on devices without one.

## Platform limits

- Hotspot hosting requires Android 8.0 (API 26); joining with
  `WifiNetworkSpecifier` requires Android 10 (API 29).
- Some OEMs disable LocalOnlyHotspot in work profiles, while tethering is
  policy-blocked, or when another hotspot is active. The UI maps the Android
  error codes to a readable message.
- The host IP detection has no public Android API. Vendors use different
  interface names; the fallback warns the user when detection fails.
- Only one temporary link can be active per app instance. Normal discovery
  resumes after Disconnect.
- The primary QR path is guest → host. Host → guest sending works once the guest
  appears through LAN discovery on the hotspot subnet; if it has not appeared
  yet, the host's **Send files** action explains that the guest can send first.
- A Windows-hosted hotspot with its own QR flow is not implemented.

## Testing checklist (two physical phones required)

1. Install the same APK on both phones; grant Nearby Wi-Fi/Location/Camera.
2. Phone A: **Temporary link → Create connection**. Confirm the QR and SSID
   appear within a few seconds and the endpoint shows an address on the hotspot
   subnet.
3. Phone B: **Scan code**, approve Android's Wi-Fi prompt, and confirm the
   connected sheet names Phone A.
4. Send a large file (at least 200 MB) from B to A. Watch the live sheet, the
   Transfers item, the header gauge, and the notification progress advance
   before completion.
5. Compare the six-digit pairing code, accept, and verify the received hash and
   `Downloads/Wisp` export.
6. Tap **Disconnect** on both phones and confirm each returns to its normal
   Wi-Fi and rediscovers normal LAN peers.
7. Repeat with Phone A as sender after the link is connected to verify the host
   can reach the guest when discovery sees it.
8. Confirm an expired QR is rejected, and that a second scanner cannot reuse the
   consumed token.
9. Close the sheet with the X and confirm the link stays active, the LINK badge
   remains on Nearby, and an additional transfer still succeeds before tapping
   **Disconnect**.
10. Share a file into Wisp on the guest while no LAN peer is visible, tap
    **Scan a QR code** in the share sheet, and confirm the staged file sends by
    itself once the link connects.
